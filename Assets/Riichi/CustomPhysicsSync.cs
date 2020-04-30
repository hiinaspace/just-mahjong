
using System;
using UdonSharp;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

public class CustomPhysicsSync : UdonSharpBehaviour
{
    // udon's "synchronize position" is really broken with
    // non-kinematic rigidbodies. Instead, try to sync position manually
    // through synced variables on each tile; this script tries to make the
    // updates reasonably speedy, considering how slow udon execution is.
    // XXX UdonBehavior[] doesn't exist in udon types, so have to cast it dynamically
    public float syncIntervalSecs = 0.1f;
    public Slider syncIntervalSlider;
    public Text syncIntervalText;
    public Slider chunkSizeSlider;
    public Text chunkSizeText;
    public Toggle writeFullRandom;

    private Component[] tiles;
    private float lastSync = 0;

    private int maxStringSize = 16;
    private int seqNo = 0;

    // master will have full control now.
    [UdonSynced] public string a;
    // let's try this
    [UdonSynced] public string b;
    //[UdonSynced] public string state3;

    void Start()
    {
        // GetComponentsInChildren includes "this", annoying
        //var tilesAndThis = transform.GetComponentsInChildren(typeof(UdonBehaviour));
        //tiles = new Component[136];
        //for (int i = 0; i < 136; ++i)
        //{
        //    tiles[i] = tilesAndThis[i + 1];
        //}
        //Debug.Log($"my player id: {VRCPlayerApi.GetPlayerId(Networking.LocalPlayer)}");
    }

    void Update()
    {
        if (Networking.IsMaster) {
            lastSync += Time.deltaTime;
            syncIntervalSecs = syncIntervalSlider.value / 1000; // in ms
            syncIntervalText.text = $"{syncIntervalSecs}ms sync interval";
            if (lastSync > syncIntervalSecs)
            {
                lastSync = 0;
                {
                    // I think sending an event makes the assembly for FixedUpdate more efficient
                    // compared to actually calling the method. dunno for sure
                    //SendCustomEvent("DoSync");
                    DoSync();
                }
            }
        }
        else
        {
            ReadSync();//SendCustomEvent("ReadSync");
        }
    }

    public void DoSync()
    {
        // 1 char idx into tiles array, 2 chars position, 2 chars rotation = 6 chars each tile;
        // total of 136 * 6 = 816 chars of state, naively. Can rotate that in about 1.6 seconds.

        // better: exploit the state of the game
        // 32 bits isn't enough for shuffling. so need full 136 byte shuffle state, in separate object (shuffler)
        // then sentinel for all tiles being in initial position from shuffle (rerun shuffle from seed)
        // when tiles get drawn, can have a brief period where player is actually picking them up
        // (full 4 bytes of transform), then when it gets sorted into the hand, have the hand positions
        // as a sentinel
        // 8 bits for switch of state style, then 5 bytes for each "free" tile
        // for hand tiles, sentinel, 4bit no tiles in hand, up to 16 bytes for tile indices in hand;
        // locally, just put their positions to the hand in that order.
        // for discards, can have a max of 18 in discard;. all will be flat on the table; can map 
        // the y euler rotation into maybe 9 bits, then 11 bits for x pos and 11 for z pos;
        // could quantize to an actual discard grid, but seems too restricting and could get weird
        // with the misalignment from riichi indicator.
        // really, all "flat, faceup on table" can get by with 5 bytes each; 1 index, bit for
        // face up or down, 31 bits for y rotation and x,z. 5 * 18 = 90 bytes. + 16 for hand,
        // that comfortably fits in a packet, with 34 bytes leftover for full tiles. at 9 bytes each,
        // that's 3 extra full tiles before spill, a few bytes leftover.

        // or, instead of all those full bytes for indices, have a bitmap of "tiles in state".
        // 136 bits = 17 bytes for bitmap, then write tiles in order.
        // 1 byte for "in original deal placement". have to look up shuffle state (expanded from random)
        // 1 byte for "in hand at pos X" 
        // 5 bytes for "in discard or on table". can expand out the precision into the remaining bits.
        // or, if you drop y euler to 8 bits (256, so 2deg precision), then save the leading 1 bit for
        // "is a discard", then that's 4 bytes.
        // or 9 bytes for full state; can use the extra 5 or so bits in the first byte for more precision
        // or a greater range of positions
        // worst case it's 1124 bytes if the player owns all the tiles and moves them into arbitrary places.
        // that's 6 fragment, or 1.2 seconds sync time. Not bad still honestly, and very much an edge case.

        // [1] [1 bit up or down] [8 bits euler y] [11 bits x] [11 bits z] = 4 bytes, on table
        // [0] [1] [13 bits x] [12 bits y] [13 bits z] [2 bits largest component] [30 bits components] = 9 bytes
        // [0] [0] [1] [2 bits seat pos] [3 bits hand pos] = 1 byte, in hand
        // then for full initial or deal position it's 46 bytes (run length 3)

        // so shuffle behavior has 136 bytes of shuffle state, then 4 bytes for other stuff (in shorts)
        // it's always owned by master. assuming master is not one of the 4 players, then you could still
        // pack (in ascii) an extra 17 byte bitmap of tiles still owned by master because a player hasn't
        // touched them.
        // then client iterates through that for all shuffle/initial tiles, then checks the 4
        // player-owned behaviors for the ~118 bytes of hand/discards/arbitrary.

        // what if a non-player grabs a tile? the OnPickup will run, assign ownership to the non-player.
        // the 4 player states will run, remove that tile from any of the bitmaps, then it'll be left to
        // whoever owned it last (master, or player).
        // instead, the Tile behavior could check that the OnPickup player is one of the 4 players, and
        // skip taking ownership if the local player is not one. Then it'll snap back into place after the player
        // lets go.

        // hmm, don't actually need bitmap since each Tile UdonBehavior can check whether it's owned by the same
        // owner as the syncing UdonBehavior. Always in initial tile gameobject order. that saves bytes.
        // except for if master is playing, becomes difficult. Need at least one bitmap to check whether
        // owner == master means it hasn't been dealt, or that the master is a player.

        // so, need
        // Shuffler behavior, has shuffle() with master check, packs shuffle state into sync, with bitmap
        //  and takes ownership of all tiles (so playerSyncs will drain)
        //  on master update(), check ownership of each tile, and turn off bit if moved from deal position
        //  on non-master fixedUpdate(), check bit of each tile, move into deal position, else skip.

        // 4 playerSyncs
        // OnInteract, take ownership and move sort tiles in hand
        // On owner update(), iterate through tiles in order; if owned, check
        //   transform against localState, and update syncedState to match
        // on non-owner update(), check seqNo for update. if so, iterate through tiles in order, if owner == playerSync owner, 
        //   advance to next packed state and unpack and set local tile transform.

        // what if state gets fragmented for a given player?
        // basically have the full state stored locally and the defragmentation happen separately from deserialization, which only
        // happens if the full unfragmented state is there. This means the update rate will slow down by number of fragments, instead of
        // partial updates. but that should still be an edge case.
        // need max of 9 fragments (all tiles owned, 9 bytes each). that's 4 bits, leaving a 4 bit seqNo. that works.

        // that's 5 gameobjects with the full synced state, per table. Might be possible to fit two tables.
        // actually 10 gamebojects does have some death run packet loss. not consistently though; it'd still be a stretch i think.
        // it might be that if not all the gameobjects update every 200ms though (actual simultaneous games), it could work okay.
        // do one table for now.


        // average case, I'd expect
        // 1 byte seqNo
        // 17 byte bitmap
        // 13 byte hand
        // 12 * 5 byte discards/calls = 60
        // 3 * 9 arbitrary = 27
        // 118 bytes, comfortably in 1 fragment, even in shorts.

        // TODO are both strings atomic, or can you have one fragment update before the other?
        // I think they're atomic considering they seem to share a serialization.

        // TODO for extra effort, could lerp/slerp the tiles into place in Update(), but would require some
        // engineering for efficiency since udon is slow. Maybe better to do the movements in FixedUpdate()
        // since the rigidbody apparently offers some interpolation in inbetween frames if you turn it on.

        // I think that's good enough that trying to do RLE to improve 9 bytes for full state case isn't worth it.
        // could probably only pack down to like 1/2 anyway, so still like a half a second of sync state.

        // TODO for the initial state, it'd count as "all arbitrary" since the're not face up. would want to either
        // start off in shuffled positions (use Start() to move, and change GenTiles()), or have another special
        // "display in order" case, which would be fine too.


        // testing the parameters of how many synced strings and the data you put in them
        // and how many bytes you can fit until udon chokes.

        // type   num strings    max str len    max bytes   
        // short      1           42                 84 (seems possible at 43 chars, but only half the time)
        // ascii      1           126                110.25
        // short      2           35                 140
        // ascii      2           105                183.75
        // short      3           19                 108
        // ascii      3           55                 144.375
        // short      4           12                 96
        // ascii      4           33                 115.5

        // got interesting error 'Caught InsufficientMemoryException while serializing,
        // encoded size is very large 238 >= 238'. So that's an interesting limit.
        // seems to happen irregularly with random packets, so there's some sort of compression
        // going on it seems.
        
        // for timing, the fastest you can change a string and expect them to sync is about 200ms.
        // any lower and packets will get dropped; even 200ms isn't completely reliable and may be better
        // on my network than most, but it seems _mostly_ reliable. packet loss seems the same no matter
        // how many strings.

        // on a whim I tried to change the synce variables to single-char variable names. doesn't affect 
        // serialization or capacity though.

        // goood news: the number of distinct UdonBehaviors with synced variables scales linearly at least to
        // 5 though (with 2 each since that's the max bandwidth). Packet loss at 200ms is still near 0, and limits
        // are the same. Going to keep duplicating gameobjects until I find the limit. I think it's under
        // 136 given that broke with "death run detected".
        // turns out the limit of UdonSynced objects is between 8 and 16, don't want to probe any further since
        // 5 is enough for my purposes. Interestingly with 16 objects, the sync timing is still 200ms for 0% loss
        // and seems to scale the same loss when you decrease the timing. The max per-object is all the same too.

        // so the max reliable throughput for udon is with 8 objects with 2 synced strings each, using only bytes
        // for 210 * 8 = 1680 bytes , at 200ms is  8400 Bps or 67.2kbps.

        a = new string(writePacket());
        b = new string(writePacket());
        //state3 = new string(writePacket());
       Debug.Log($"{gameObject.name} wrote packet {seqNo}");
        seqNo = (ushort)(seqNo + 1);
    }
    char[] writePacket()
    {
        maxStringSize = (int)chunkSizeSlider.value;
        chunkSizeText.text = $"{maxStringSize} chars";
        // add a seqNo to the beginning
        char[] pack = new char[maxStringSize];
        pack[0] = Convert.ToChar(seqNo); // dumb work avoiding thing
        int n = 1, i = 0;
        // just write random data for now
        if (writeFullRandom.isOn)
        {
            while (n < maxStringSize)
            {
                var p = UnityEngine.Random.Range(0, 65535);
                pack[n++] = Convert.ToChar(p);
            }
        } else
        {
            while (n < maxStringSize)
            {
                pack[n++] = (char)UnityEngine.Random.Range(0,127);
            }
        }
        //while (n+5 < maxStringSize)
        //{
        //    pack[n] = (char)i;
        //    n += 1;
        //    packPosition(tiles[i].transform.position, pack, n);
        //    n += 2;
        //    packQuaternion(tiles[i].transform.rotation, pack, n);
        //    n += 2;
        //    // wrap around if we're done
        //    i = (i + 1) % 136;
        //}

        return pack;
    }

    public void ReadSync()
    {
        readPacket(a);
       readPacket(b);
        //readPacket(state3);
    }

    void readPacket(string pkt)
    {
        if (pkt.Length == 0) return;
        char[] pack = pkt.ToCharArray();
        int seq = (int)pack[0];
        if (seq != seqNo) {
            Debug.Log($"{gameObject.name} read packet {seq}, packet loss of {seq - seqNo}");
        }
        //for (int j = 0; j < tilesPerStripe; ++j)
        //{
        //    int i = Convert.ToInt32(pack[n]);
        //    n += 1;
        //    var p = unpackPos(pack, n);
        //    n += 2;
        //    var r = unpackQuaternion(pack, n);
        //    n += 2;
        //    // TODO use localPosition, if there are mulitiple tables/move the table from 0,0
        //    tiles[i].transform.position = p;
        //    tiles[i].transform.rotation = r;
        //}
        seqNo = seq;
    }

    void packPosition(Vector3 p, char[] array, int idx)
    {
        // 11 bits for x and z between [1.2,1.2], 10 bits for y between [0.4, 0.9]
        // covers the playable area of the table to millimeter precision
        var x = PackPosComponent(p.x);
        var y = PackYComponent(p.y);
        var z = PackPosComponent(p.z);

        uint pack = x;
        pack = (pack << 11) + z;
        pack = (pack << 10) + y;

        //Debug.Log($"packed {p.x} {p.y} {p.z} into {x} {y} {z} into {pack}");

        array[idx] = Convert.ToChar(pack >> 16);
        array[idx+1] = Convert.ToChar(pack & 65535);
    }
    Vector3 unpackPos(char[] array, int idx)
    {
        uint pack = Convert.ToUInt32(array[idx]);
        pack = (pack << 16) + Convert.ToUInt32(array[idx+1]);

        uint a = (uint)((pack >> 21) & 2047);
        uint b = (uint)((pack >> 10) & 2047);
        uint c = (uint)(pack & 1023);

        var x = UnpackPosComponent(a);
        var z = UnpackPosComponent(b);
        var y = UnpackYComponent(c);

        //Debug.Log($"upacked {pack} into {a} {b} {c} into {x} {y} {z}");

        return new Vector3(x, y, z);
    }

    uint PackPosComponent(float f)
    {
        // [-1.2, 1.2] to [0, 2047]
        if (f < -1.2f) f = -1.2f;
        if (f > 1.2f) f = 1.2f;
        double s = (0.5 * (f / 1.2 + 1) * 2047 + 0.5);
        var r = Convert.ToUInt32(Mathf.Floor((float)s));
        return r;
    }
    float UnpackPosComponent(long i)
    {
        return (i - 1024) * (1.0f / 2047f) * 2.4f;
    }

    uint PackYComponent(float f)
    {
        // [0.5, 0.9] to [0, 1023]
        if (f < 0.5) f = 0.5f;
        if (f > 0.9) f = 0.9f;
        var r = Convert.ToUInt32(Mathf.Floor((float)((f - 0.5) / 0.4 * 1023 + 0.5)));
        return r;
    }
    float UnpackYComponent(long i)
    {
        return i / 1023f * 0.4f + 0.5f;
    }

    void packQuaternion(Quaternion q, char[] array, int idx)
    {
        // two bits for largest component idx, 10 bits per smallest 3
        uint largest_idx = 0;
        float largest = q.x;
        if (Mathf.Abs(q.y) > Mathf.Abs(largest))
        {
            largest_idx = 1;
            largest = q.y;
        }
        if (Mathf.Abs(q.z) > Mathf.Abs(largest))
        {
            largest_idx = 2;
            largest = q.z;
        }
        if (Mathf.Abs(q.w) > Mathf.Abs(largest))
        {
            largest_idx = 3;
            largest = q.w;
        }

        uint x = PackQuatComponent(q.x / largest);
        uint y = PackQuatComponent(q.y / largest);
        uint z = PackQuatComponent(q.z / largest);
        uint w = PackQuatComponent(q.w / largest);

        uint pack = largest_idx;
        if (largest_idx != 0) pack = (uint)((pack << 10) + x);
        if (largest_idx != 1) pack = (uint)((pack << 10) + y);
        if (largest_idx != 2) pack = (uint)((pack << 10) + z);
        if (largest_idx != 3) pack = (uint)((pack << 10) + w);

        array[idx] = Convert.ToChar(pack >> 16);
        array[idx+1] = Convert.ToChar(pack & 65535);
    }
    Quaternion unpackQuaternion(char[] array, int idx)
    {
        uint pack = Convert.ToUInt32(array[idx]);
        pack = (pack << 16) + Convert.ToUInt32(array[idx+1]);

        uint largest_idx = pack >> 30;
        float a = UnpackQuatComponent((uint)((pack >> 20) & 1023));
        float b = UnpackQuatComponent((uint)((pack >> 10) & 1023));
        float c = UnpackQuatComponent((uint)(pack & 1023));
        Quaternion q;
        switch (largest_idx)
        {
            case 0 : q = new Quaternion(1f, a, b, c); break;
            case 1 : q = new Quaternion(a, 1f, b, c); break;
            case 2 : q = new Quaternion(a, b, 1f, c); break;
            default: q = new Quaternion(a, b, c, 1f); break;
        }
        return q.normalized;
    }

    uint PackQuatComponent(float f)
    {
        // map [-1, 1] to [0, 1024]
        return Convert.ToUInt32(Mathf.Floor(0.5f * (f + 1.0f) * 1023 + 0.5f));
    }

    float UnpackQuatComponent(uint i)
    {
        return (i - 512) * (1.0f / 1023f) * 2f;
    }

}
