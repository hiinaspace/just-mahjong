
using System;
using System.Reflection.Emit;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Metadata.W3cXsd2001;
using UdonSharp;
using UnityEditor.MemoryProfiler;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// Riichi game state manager, using Bus to synchronize with other clients.
/// Handles synchronizing unity state with the internal network state, with
/// custom events for all the possible UI interactions.
/// 
/// Very much a megaclass, but the nature of the udon heap not updating
/// consistently between behaviors makes splitting functionality more difficult
/// than it should be. I'd consider sticking the Bus logic in here too to avoid
/// the gross ring buffer communication, but the Bus alone is fairly stable and
/// testable in isolation this way.
/// 
/// heirarchy setup:
/// 
/// RiichiGame
///   Tiles
///   HandPlacements[0-3]
///   Placements
///   RiichiSeat[0-4]
///     Canvas
///       Buttons
///   TableMesh
///   CenterThing
///     Honba and stuff
///   ShuffleButton
///   ScoreDisplay
/// </summary>
public class RiichiGame : UdonSharpBehaviour
{
    public Bus bus;
    public RiichiSeat[] seats;
    // disambiguate between multiple mahjong games in a map.
    // I think I can fit at least 2, at most 8 before the Bus
    // networking system gets too congested to sync reliably.
    public int gameId = 0;
    public LayerMask tileLayer;

    const int EAST = 0, NORTH = 1, WEST = 2, SOUTH = 3;

    // XXX avoid moving tiles until initial shuffle; this is implicitly synced
    // to 'true' as soon as the local client gets any packet related to the game, 
    // first by the table owner when they press the shuffle button.
    private bool gameStarted = false;

    // private bool sanmaMode = TODO maybe eventually

    // abstract game state:
    //public Transform centerThing;
    //private int roundWind = EAST;
    //private int roundNo = 0;
    //private int honba = 0;
    //// XXX the dealer's wind will be east despite being on a different
    //// side of the table; this just controls the rotation of the wind indicator
    //private int dealer = EAST;
    // TODO all the scoring and round state isn't really necessary yet;
    // may be possible to pack that into one nice synced behavior too, rather than
    // try to fit it in to the rats nest of physics sync.
    [HideInInspector]
    public int[] scores = new int[4];

    const int DEAL = 0, HAND = 1, UPRIGHT = 2, TABLE = 3, ARBITRARY = 4;

    // byte sizes for each tile state
    // DEAL is zero because shuffle state is transmitted separately
    private int[] PACK_SIZE = new int[] { 0, 1, 4, 4, 9 };

    private int[] tileState = new int[136];

    // parallel arrays for the different states for simplicity

    private int[] shuffleOrder = new int[136]; // if DEAL, idx into dealTransforms

    private int[] handOrder = new int[136]; // if HAND, position in hand;
    private int[] handSeat = new int[136]; // if HAND, which seat 

    // TODO eventually; the TABLE packing of 4 bytes is sufficient for normal
    // games, and it's tricky to do positioning with riichi sideways (can't
    // just grid them). Maybe it's easier than i think, but don't worry about it for now
    //private int[] discardOrder = new int[136]; // if DISCARD, position in discard
    //private bool[] discardRiichi = new bool[136]; // if DISCARD, whether tile is sideways

    private Vector2[] uprightXZ = new Vector2[136]; // if UPRIGHT, xz on table pos
    private float[] uprightYRot = new float[136]; // if UPRIGHT, Y euler axis rotation
    // I don't think i need upsidedown but upright tiles, they can be arbitrary
    
    private Vector2[] tableXZ = new Vector2[136]; // if TABLE, xy on table pos
    private float[] tableZRot = new float[136]; // if TABLE, Z euler axis rotation
    private bool[] tableUp = new bool[136]; // if TABLE, whether tile is face-up or down

    // if ARBITRARY, full precision pos/rot
    // local to the Tile root (center of the table)
    private Vector3[] arbitraryTilePositions = new Vector3[136]; 
    private Quaternion[] arbitraryTileRotations = new Quaternion[136];

    // network efficiency state
    // (owned) tiles that need to be broadcast
    private bool[] locallyMovedTiles = new bool[136];

    // access to actual unity state
    private GameObject[] tiles = new GameObject[136];
    private Transform[] tileTransforms = new Transform[136];
    private Rigidbody[] tileRigidBodies = new Rigidbody[136];
    private BoxCollider[] tileBoxColliders = new BoxCollider[136];

    // note upwards (Y) is for upright tiles; Z is up for tiles on the table;
    private Vector3 tileDimensions = new Vector3(0.0375f, 0.05f, 0.032f);
    private float uprightY;
    private float tableY;

    // some preset transforms for positioning
    private Transform[] dealTransforms = new Transform[136];
    private Transform[][] handTransforms;

    void Start()
    {
        uprightY = tileDimensions.y / 2;
        tableY = tileDimensions.z / 2;

        var tileParent = transform.Find("Tiles");
        var dealParent = transform.Find("Placements");
        for (int i = 0; i < 136; ++i)
        {
            var tile = tileParent.GetChild(i).gameObject;
            tiles[i] = tile;
            tileTransforms[i] = tile.transform;
            tileRigidBodies[i] = tile.GetComponent<Rigidbody>();
            tileBoxColliders[i] = tile.GetComponent<BoxCollider>();

            dealTransforms[i] = dealParent.GetChild(i);

            shuffleOrder[i] = i;
            locallyMovedTiles[i] = false;
        }
        for (int i = 0; i < 4; ++i)
        {
            scores[i] = 25000;
            var handParent = transform.Find($"HandPlacements{i}");
            handTransforms[i] = new Transform[16];
            for (int j = 0; j < 16; ++j)
            {
                handTransforms[i][j] = handParent.GetChild(j);
            }
        }

        // this behavior is running on a new player (in this map, not game), so request
        // that all clients start rebroadcasting their state for us.
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "DoResync");
    }

    private bool IsTableOwner()
    {
        // TODO if we're the owner of the EAST seat, or instance master. so for
        // multiple tables, master doesn't have to be there. could instead use
        // the ownership on this behavior, but would seem weird that table
        // owner doesn't have to be playing as well. thus requiring seat owner.
        return Networking.IsMaster || Networking.IsOwner(seats[EAST].gameObject);
    }

    // to cut down on Update() time, only check a subset of the tiles each frame
    private int localTileCursor;
    private const int checkedTilesPerUpdate = 17;

    private float sendWait;
    private const float sendInterval = 0.2f;
    private int sendCursor;

    // XXX udonsharp can't access other const members, so duplicate from Bus
    private const int maxSyncedStringSize = 105;
    private const int maxPacketCharSize = maxSyncedStringSize * 2;
    // 14 bits leftover to do something with
    // possibly packing player id + seqNo to monitor per-player packet loss.
    private const int headerCharSize = 2; 
    // for simplicity of the byte -> 7bit packing, which packs 7 bytes to 8 chars, 56 bits at a time
    // header size at 2 makes this a nice round 208 chars or 182 bytes
    private const int maxDataCharSize = (int)((maxPacketCharSize - headerCharSize) / 8f) * 8;
    private const int maxDataByteSize = maxDataCharSize / 8 * 7;

    // ring buffers of Bus
    private int recvBufIdx;
    private int ackBufIdx;

    // ring buffer for remotely moved tiles by idx that we want to lerp;
    private const int rmtSize = 256;
    private int[] remotelyMovedTiles = new int[rmtSize];
    private Vector3[] rmtPosition = new Vector3[rmtSize];
    private Quaternion[] rmtRotation = new Quaternion[rmtSize];
    private int rmtHead = 0;
    private int rmtTail = 0;
    private bool rmtEmpty = true;

    private float lerpSpeed = 0.05f;
    private float slerpSpeed = 5f;

    void Update()
    {
        // check recv buffer, read any packets
        while (bus.recvBufferHead != recvBufIdx)
        {
            var packet = bus.recvBuffer[recvBufIdx][0];

            // read out packets, update internal state

            recvBufIdx = (recvBufIdx + 1) % bus.recvBufferSize;
        }

        // check ack buffer, undirty any tiles we sent
        while (bus.successfulAckedHead != ackBufIdx)
        {
            var ackedTiles = (int[])bus.successfulAckedObjects[ackBufIdx];
            foreach (int i in ackedTiles)
            {
                locallyMovedTiles[i] = false;
            }
            ackBufIdx = (ackBufIdx + 1) % bus.recvBufferSize;
        }

        // skip other stuff if table is idle
        if (!gameStarted) return;

        // check local state, move any dirty tiles into position
        // lerp/slerp-wise.
        if (!rmtEmpty)
        {
            for (int i = rmtHead; i < rmtTail; i = (i + 1) % rmtSize)
            {
                var rp = rmtPosition[i];
                var rr = rmtRotation[i];
                var lt = tileTransforms[remotelyMovedTiles[i]];

                lt.position = Vector3.MoveTowards(lt.position, rp, lerpSpeed * Time.deltaTime);
                lt.rotation = Quaternion.RotateTowards(lt.rotation, rr, slerpSpeed * Time.deltaTime);
            }

            // check for completion in order; note that some tiles will get there faster
            // and we'll spend time not moving them; this should be fine.
            do
            {
                var lt = tileTransforms[remotelyMovedTiles[rmtHead]];
                var rp = rmtPosition[rmtHead];
                var rr = rmtRotation[rmtHead];
                if (lt.position == rp && lt.rotation == rr)
                {
                    rmtHead = (rmtHead + 1) % rmtSize;
                }
                else { break; }
            } while (rmtHead != rmtTail);
            if (rmtHead == rmtTail) rmtEmpty = true;
        }

        if (IsSeated())
        {
            for (int i = 0; i < checkedTilesPerUpdate; ++i)
            {
                var n = localTileCursor;
                localTileCursor = (localTileCursor + 1) % 136;
                var tile = tiles[localTileCursor];
                // skip unowned tiles
                if (!Networking.IsOwner(tile)) continue;

                // rely on rigidbody's sleep detection for movement; Definitely quicker
                // than comparing transforms in udon, and I think should work for this
                // purpose. locally kinematic tiles can't have moved either,
                // since the tile behavior makes them unkinematic on pickup, and pickup is
                // the only way to move tiles locally (besides shuffle/sort)
                var r = tileRigidBodies[n];
                if (!r.isKinematic && !r.IsSleeping())
                {
                    locallyMovedTiles[n] = true;
                    UpdateInternalTileState(n);
                }
            }

            // load any dirty tiles into send buffer when ready
            sendWait -= Time.deltaTime;
            if (sendWait < 0 && bus.sendReady)
            {
                sendWait = sendInterval;
                var sendStart = sendCursor; // prevent infinite loop if no locallyMovedTiles

                var buf = bus.sendBuffer;
                buf[0] = (byte)gameId; // disambiguate between games

                int n = 1;
                while (n < maxDataByteSize)
                {
                    while (!locallyMovedTiles[sendCursor])
                    {
                        sendCursor = (sendCursor + 1) % 136;
                        if (sendCursor == sendStart) break;
                    }
                    // XXX break outer while too, no goto
                    if (!locallyMovedTiles[sendCursor]) break;

                    var state = tileState[sendCursor];
                    var packSize = PACK_SIZE[state];
                    if (n + 1 + packSize > maxDataByteSize) break; // not enough room

                    buf[n++] = (byte)sendCursor; // tile idx
                    switch (state)
                    {
                        case HAND:      PackHand(sendCursor, n, buf); break;
                        case UPRIGHT:   PackUpright(sendCursor, n, buf); break;
                        case TABLE:     PackTable(sendCursor, n, buf); break;
                        case ARBITRARY: PackArbitrary(sendCursor, n, buf); break;
                    }
                    n += packSize;
                }

                bus.sendBufferReady = true;
            }
        }
    }

    // packet format:
    // 
    // [2 bits game id]       disambiguate multiple games on same Bus
    // [5 bits game epoch]    disambiguate packets from after shuffle; shuffle bumps epoch, clients reject old epochs
    // [1 bit isShuffle]
    // if isShuffle:
    // [17 bytes bitmap]      tiles that are in deal position
    // [136 bytes]            shuffleOrder
    // else:
    // [1 byte tileIdx, or 255 as EOF]
    // variable length:
    // [1] [1 bit up or down] [8 bits euler z] [11 bits x] [11 bits z] = 4 bytes, on table
    // [0] [1] [2 bits seat pos] [4 bits hand pos] = 1 byte, in hand
    // [0] [0] [1] [7 bits euler y] [11 bits x] [11 bits z] = 4 bytes, upright on table
    // [0] [0] [0] [1] [12 bits x] [12 bits y] [12 bits z] [2 bits largest component] [30 bits components] = 9 bytes
    // 
    // for the regular case of 13 tiles upright/on table in calls, 18 discards on table, 1 tile in hand, that's
    // 109 bytes of tiles positions, 31 bytes tile indices, with 41 bytes to spare. Could instead pack the
    // present tiles into a bitmap, but those are more expensive to encode/decode, so going with full bytes for now.
    #region bitpacking methods

    void WriteInt(int i, int pos, byte[] buf)
    {
        buf[pos]   = (byte)(i >> 24);
        buf[pos+1] = (byte)((i >> 16) & 255);
        buf[pos+2] = (byte)((i >> 8) & 255);
        buf[pos+3] = (byte)(i & 255);
    }

    void PackTable(int i, int n, byte[] buf)
    {
        // int p = 1 << 1; 
        int p = 2 + (tableUp[i] ? 0 : 1);
        p = (p << 8) + PackFloat(Mathf.Repeat(tableZRot[i], 360), 0, 360, 8);
        var v = tableXZ[i];
        p = (p << 11) + PackFloat(v.x, -1, 1, 11);
        p = (p << 11) + PackFloat(v.y, -1, 1, 11);
        WriteInt(p, n, buf);
    }

    void PackHand(int i, int n, byte[] buf)
    {
        // int p = 1 << 2;
        int p = 8 + handSeat[i];
        p = (p << 4) + handOrder[i];
        buf[n] = (byte)p;
    }
    void PackUpright(int i, int n, byte[] buf)
    {
        // int p = 1 << 7;
        int p = 128 + PackFloat(Mathf.Repeat(uprightYRot[i], 360), 0, 360, 7);
        var v = uprightXZ[i];
        p = (p << 11) + PackFloat(v.x, -1, 1, 11);
        p = (p << 11) + PackFloat(v.y, -1, 1, 11);
        WriteInt(p, n, buf);
    }
    void PackArbitrary(int i, int n, byte[] buf)
    {
        var v = arbitraryTilePositions[i];
        var px = PackFloat(v.x, -2, 2, 12);
        var py = PackFloat(v.y, 0, 3, 12);
        var pz = PackFloat(v.z, -2, 2, 12);
        // 0001xxxx|xxxxxxxx|yyyyyyyy|yyyyzzzz|zzzzzzz
        buf[n++] = (byte)(32 + ((px >> 8) & 15));
        buf[n++] = (byte)(px & 255);
        buf[n++] = (byte)((py >> 4) & 255);
        buf[n++] = (byte)((py & 15) << 4 + ((pz >> 8) & 15));
        buf[n++] = (byte)(pz & 255);

        PackQuaternion(arbitraryTileRotations[i], buf, n);
    }

    int PackFloat(float f, float min, float max, int bits)
    {
        f = Mathf.Clamp(f, min, max);
        f = Mathf.InverseLerp(min, max, f);
        return Mathf.RoundToInt(f * ((1 << bits) - 1));
    }
    float UnpackFloat(int i, float min, float max, int bits)
    {
        return Mathf.Lerp(min, max, (float)i / ((1 << bits) - 1));
    }

    void PackQuaternion(Quaternion q, byte[] array, int idx)
    {
        // two bits for largest component idx, 10 bits per smallest 3
        int largest_idx = 0;
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

        int x = PackFloat(q.x / largest, -1, 1, 10);
        int y = PackFloat(q.y / largest, -1, 1, 10);
        int z = PackFloat(q.z / largest, -1, 1, 10);
        int w = PackFloat(q.w / largest, -1, 1, 10);

        int pack = largest_idx;
        if (largest_idx != 0) pack = ((pack << 10) + x);
        if (largest_idx != 1) pack = ((pack << 10) + y);
        if (largest_idx != 2) pack = ((pack << 10) + z);
        if (largest_idx != 3) pack = ((pack << 10) + w);

        array[idx]   = (byte)((pack >> 24) & 255);
        array[idx+1] = (byte)((pack >> 16) & 255);
        array[idx+2] = (byte)((pack >> 8)  & 255);
        array[idx+3] = (byte)(pack         & 255);
    }
    Quaternion UnpackQuaternion(byte[] array, int idx)
    {
        int pack =           array[idx];
        pack = (pack << 8) + array[idx+1];
        pack = (pack << 8) + array[idx+2];
        pack = (pack << 8) + array[idx+3];

        int largest_idx = pack >> 30;
        float a = UnpackFloat(((pack >> 20) & 1023), -1, 1, 10);
        float b = UnpackFloat(((pack >> 10) & 1023), -1, 1, 10);
        float c = UnpackFloat((pack & 1023), -1, 1, 10);
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
    #endregion

    private void UpdateInternalTileState(int n)
    {
        // don't bother checking for DEAL or HAND; those states are entered through
        // deal or sorting, and not worth checking to see if the player somehow lined
        // up a tile right back to where it was. The isKinematic/Sleeping check should
        // prevent checking for those tiles anyway;
        var t = tileTransforms[n];
        var p = t.position;
        var r = t.rotation;
        var e = r.eulerAngles;
        // t.up is normalized local Y (in world coords), so tile is upright when it's mostly 1.
        if (Mathf.Approximately(p.y, uprightY) && t.up.y > 0.99)
        {
            tileState[n] = UPRIGHT;
            uprightXZ[n] = new Vector2(p.x, p.z);
            uprightYRot[n] = e.y;
        }
        // t.forward is straight up (or down) for on table tiles
        else if (Mathf.Approximately(p.y, tableY) && Math.Abs(t.forward.y) > 0.99)
        {
            tileState[n] = TABLE;
            tableXZ[n] = new Vector2(p.x, p.z);
            tableZRot[n] = e.z;
            tableUp[n] = t.forward.y > 0;
        }
        // from IsSleeping check, assume it's now in some new arbitrary position
        else {
            tileState[n] = ARBITRARY;
            arbitraryTilePositions[n] = p;
            arbitraryTileRotations[n] = r;
        }
    }

    private bool IsSeated()
    {
        for (int i = 0; i < 4; ++i)
        {
            var s = seats[i];
            if (Networking.IsOwner(s.gameObject) && s.playerSeated) return true;
        }
        return false;
    }

    void DoResync()
    {
        Debug.Log($"Local resync requested, invalidating all tiles");
        for (int i = 0; i < 136; ++i)
        {
            locallyMovedTiles[i] = true;
        }
    }

    public void Shuffle()
    {
        if (!IsTableOwner()) return;

        gameStarted = true;

        int swap;
        for (int i = 135; i >= 1; --i)
        {
            var j = UnityEngine.Random.Range(0, i + 1); // range max is exclusive
            swap = shuffleOrder[j];
            shuffleOrder[j] = shuffleOrder[i];
            shuffleOrder[i] = swap;
        }

        // take control of all tiles
        var player = Networking.LocalPlayer;
        for (int i = 0; i < 136; ++i)
        {
            Networking.SetOwner(player, tiles[i]);
            tileState[i] = DEAL;
            // normally in Update() we'd defer to our local state in unity
            // since we own the tiles, so also actually move the tiles locally;
            // on other clients this will happen naturally since they don't own
            // the tiles
            var tileT = tileTransforms[i];
            var dealT = dealTransforms[shuffleOrder[i]];
            tileT.position = dealT.position;
            tileT.rotation = dealT.rotation;
            // freeze, so stacked walls work okay.
            tileRigidBodies[i].isKinematic = true;

            // invalidate so we'll send all the tiles
            locallyMovedTiles[i] = true;
        }

        // TODO one problem is that even after the shuffle happens, stale packets could
        // clobber the shuffle state, especially if other clients are randomly broadcasting
        // for entropy repair. Might need a 'gameEpoch' sort of counter in each
        // packet, to reject packets from before the shuffle.
    }

    public void SortHand(int seat)
    {
        var t = seats[seat].transform;
        var zone = seats[seat].handZone;
        var halfExtents = zone.size / 2;
        var collide = Physics.OverlapBox(t.position, halfExtents, t.rotation, tileLayer);
        Sort(collide);
        var placements = handTransforms[seat];
        for (int i = 0; i < collide.Length; ++i)
        {
            var tile = collide[i].transform;
            var obj = tile.gameObject;
            Networking.SetOwner(Networking.LocalPlayer, obj);

            var placement = placements[i];
            tile.position = placement.position;
            tile.rotation = placement.rotation;
            var r = tile.gameObject.GetComponent<Rigidbody>();
            // allow local movement when collided for hand rearrangement
            r.isKinematic = false;
            // but immediately sleep; XXX this prevents the local tile check
            // from immediately seeing a non-kinematic non-sleeping tile and trying
            // to sync it as UPRIGHT.
            r.Sleep();

            // update internal state
            // XXX linear probe, oh well;
            for (int j = 0; j < 136; ++j)
            {
                if (tiles[j] == obj)
                {
                    tileState[j] = HAND;
                    handOrder[j] = i;
                    handSeat[j] = seat;
                    break;
                }
            }
        }
    }

    private void Sort(Collider[] tiles)
    {
        Collider swap;
        // finally those Art of Programming books have value
        bool sorted = false;
        while (!sorted)
        {
            sorted = true;
            for (int i = 0; i < tiles.Length - 1; ++i)
            {
                // tile gameobject names are sorted by tile value and suit
                // since that's more convenient than trying to read values out
                // of some custom udon component
                string tile1 = tiles[i].gameObject.name;
                string tile2 = tiles[i+1].gameObject.name;
                if (tile1.CompareTo(tile2) > 0)
                {
                    swap = tiles[i];
                    tiles[i] = tiles[i+1];
                    tiles[i+1] = swap;
                    sorted = false;
                }
            }
        }
    }
    
    // TODO if udon networking doesn't straight up break with 20ish objects with
    // synced variables (not trying to pack and change them every 200ms), then
    // it'd be easier to sync all this non-transform stuff through that and leave
    // the complex Bus stuff to the transforms. should do a simple world test of
    // N synced objects and see how well it works.

    public void AdjustScore(int seat, int delta)
    {
        scores[seat] += delta;
    }

    private float lastResync = float.MinValue;

    // button to send network rpc to other clients to rebroadcast all their
    // tile and game states.
    public void RequestResync()
    {
        if (Time.time - lastResync < 5) return; // debounce locally
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "DoResync");
        lastResync = Time.time;
    }
}
