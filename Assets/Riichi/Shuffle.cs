using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

public class Shuffle : UdonSharpBehaviour
{
    public Transform tileParent;
    public Transform placementParent;

    Transform[] tiles;
    Transform[] placements;

    // 136 bytes, index of tile to initial deal placement
    // 17 bytes: bitmap of tiles still in initial deal
    // and pad with 1 byte to make it evenly divisible by 7
    // so the serialization code is simpler
    byte[] shuffleState;
    private const int shuffleStateSize = 136 + 17 + 1;

    // tiles still in initial deal position
    // same as the bitmap in the shufflestate but easier to use
    // see OwnAndSortTiles for use, in the awkward (master is player) case
    // note this isn't synced, but is maintained with updates
    public bool[] dealtTiles;

    int seqNo;
    int lastReadSeq;
    public bool isDealt = false;

    // 7 bits: [4 bits seqNo] [1 bit for "is dealt"]
    // 153 bytes for the rest of the shuffleState
    [UdonSynced] string shuffleState0 = "";
    [UdonSynced] string shuffleState1 = "";

    private float lastWrite;
    // shuffle and tilebitmap change doesn't happen that often
    private const float updateSpeed = 0.5f;
    private float lastRead;
    private const float readUpdateSpeed = 0.4f;

    void Start()
    {
        tiles = new Transform[136];
        placements = new Transform[136];
        dealtTiles = new bool[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tileParent.GetChild(i);
            placements[i] = placementParent.GetChild(i);
            dealtTiles[i] = false;
        }
        shuffleState = new byte[shuffleStateSize];
        //DoShuffle();
    }

    void Interact()
    {
        DoShuffle();
    }

    void Update()
    {
        if (Networking.IsMaster)
        {
            lastWrite += Time.deltaTime;
            if (lastWrite > updateSpeed)
            {
                lastWrite = 0;
                if (UpdateTileBitmap())
                {
                    Serialize();
                    // testing
                    //UpdateLocalTiles();
                }
            }
        } else
        {
            lastRead += Time.deltaTime;
            if (lastRead > readUpdateSpeed)
            {
                lastRead = 0;
                // on client, try deserialize
                if (Deserialize())
                {
                    UpdateLocalTiles();
                }
            }
        }
    }

    void UpdateLocalTiles()
    {
        if (!isDealt)
        {
            // tiles haven't been dealt yet, so do nothing;
            // TODO if you wanted to be able to return to initial undealt state, would need more code
            return;
        }
        // copy bitmap out of shuffleState for ease of use
        var bitmap = new byte[17];
        for (int i = 0; i < 17; ++i)
        {
            bitmap[i] = shuffleState[i + 136];
        }

        for (int i = 0; i < 136; ++i)
        {
            // read jth byte at ith bit
            if ((((int)(bitmap[i / 8]) >> (7 - i % 8)) & 1) == 0)
            {
                var p = placements[shuffleState[i]]; // shufIdx is inside shuffleState
                // Debug.Log($"moved tile {i} into {shuffleState[i]}");
                // freeze (locally);
                tiles[i].gameObject.GetComponent<Rigidbody>().isKinematic = true;
                tiles[i].position = p.position;
                tiles[i].rotation = p.rotation;
            }
        }
    }

    void DoShuffle()
    {
        if (!Networking.IsMaster) return;

        byte swap;
        byte[] shufIdx = new byte[136];
        // take ownership of everything, and initialize shufIdx
        for (int i = 0; i < 136; ++i)
        {
            Networking.SetOwner(Networking.LocalPlayer, tiles[i].gameObject);
            shufIdx[i] = (byte)i;
            // and update non-bitmap while we're at it
            dealtTiles[i] = true;

        }

        for (int i = 135; i >= 1; --i)
        {
            var j = UnityEngine.Random.Range(0, i + 1); // range max is exclusive
            swap = shufIdx[j];
            shufIdx[j] = shufIdx[i];
            shufIdx[i] = swap;
        }
        
        shufIdx.CopyTo(shuffleState, 0);
        isDealt = true;
        for (int i = 136; i < 153; ++i)
        {
            // all tiles now in initial state
            shuffleState[i] = 0b0000_0000;
        }

        Serialize();
        // will check that serialization didn't break for sanity
        Deserialize();
        // actually move the tiles locally, since this doesn't run otherwise.
        UpdateLocalTiles();
    }

    // returns if bitmap changed (and thus new packet needs writing)
    bool UpdateTileBitmap()
    {
        if (!isDealt) return false;
        var changed = false;
        var bitmap = new byte[17];
        // TODO since a tile can't get put back into deal position, could
        // skip checking any tile that's already been flipped
        for (int i = 0; i < 17; ++i)
        {
            var oldMap = shuffleState[136 + i];
            byte map = oldMap;
            for (int j = 0; j < 8; ++j)
            {
                var k = i * 8 + j;
                var alreadyMoved = (((int)oldMap >> (7-j)) & 1) == 1;
                if (alreadyMoved) continue;

                var t = tiles[k];
                var expected = placements[shuffleState[k]];

                // if the tile moved locally (we're running on the master), then either
                // 1) another player picked took ownership of it, starts broadcasting their
                //    position from OwnAndSortTiles behavior, which ran before this
                //    master check could run. (TODO is this a race?)
                // 2) the master isn't playing, and just grabbed it.
                //
                // in the former case, everything should be good, as all clients will now read the
                // state from that player instead of this Shuffle behavior.
                // in the latter case, the master can move the tile around, but it won't be synced
                // to any other clients; The other clients will just see the tile wherever it was
                // last for them, presumably in the dealt position; so it's weird (master out of sync)
                // but doesn't break the game.
                // TODO to fix this, this method could check whether the tile is now owned by another
                // player, and refuse to relinquish control (snap it back for the master); but if the 
                // master _is_ playing, then that's not what we want.
                // Hurts to think about; I think this is fine for now. If we had more room in shufflestate,
                // you could pack into it some "being moved by master" transforms, but it's dubious.
                // Do need to be careful with the "master could be a player or not" condition in general.
                if (t.position != expected.position || t.rotation != expected.rotation)
                {
                    // flip bit for this tile
                    map |= (byte)(1 << (7-j));
                    dealtTiles[k] = false; // this is just local, for OwnAndSortTiles
                    //Debug.Log($"tile {k} moved, expected in {shuffleState[k]} position {Convert.ToString((int)map, 2)}");
                }
            }
            changed = changed || (oldMap != map);
            bitmap[i] = map;
        }
        //DebugBitmap("old bitmap: ", shuffleState, 136);
        //DebugBitmap("new bitmap: ", bitmap, 0);
        // copy into state
        bitmap.CopyTo(shuffleState, 136);
        return changed;
    }

    void DebugBitmap(string s, byte[] bitmap, int i)
    {
        int n = i + 17;
        while (i < n)
        {
            var l = Convert.ToString((int)bitmap[i++], 2);
            while (l.Length < 8)
            {
                l = "0" + l;
            }
            s += l;
        }
        Debug.Log(s);
    }

    void Serialize()
    {
        // pack shuffleState into the rest 7 bit ascii in synced variables
        int n = 0;
        // 1 + (int)Mathf.Ceil(shuffleStateSize * 8f / 7f)
        char[] chars = new char[177];
        // update seq no, in first ascii of packed state
        seqNo += 1;
        chars[n++] = (char)((seqNo << 4) + (isDealt ? 0 : 1));
        for (int i = 0; i < shuffleStateSize;)
        {
            // pack 7 bytes into 56 bits;
            ulong pack =         shuffleState[i++];
            pack = (pack << 8) + shuffleState[i++];
            pack = (pack << 8) + shuffleState[i++];

            pack = (pack << 8) + shuffleState[i++];
            pack = (pack << 8) + shuffleState[i++];
            pack = (pack << 8) + shuffleState[i++];
            pack = (pack << 8) + shuffleState[i++];
            //DebugLong("packed: ", pack);

            // unpack into 8 7bit asciis
            chars[n++] = (char)((pack >> 49) & (ulong)127);
            chars[n++] = (char)((pack >> 42) & (ulong)127);
            chars[n++] = (char)((pack >> 35) & (ulong)127);
            chars[n++] = (char)((pack >> 28) & (ulong)127);

            chars[n++] = (char)((pack >> 21) & (ulong)127);
            chars[n++] = (char)((pack >> 14) & (ulong)127);
            chars[n++] = (char)((pack >> 7)  & (ulong)127);
            chars[n++] = (char)(pack         & (ulong)127);
            //DebugChars("chars: ", chars, n - 8);
        }
        // drop into the synced variables
        DebugBytes($"wrote new shuffle {seqNo}, isDealt {isDealt} ", shuffleState);
        var s = new string(chars);
        shuffleState0 = s.Substring(0, 88);
        shuffleState1 = s.Substring(88, 89);
    }
    
    void DebugChars(string s, char[] chars, int n)
    {
        for (int i = n; i < (n + 8); ++i)
        {
            var c = Convert.ToString((byte)chars[i], 2);
            while (c.Length < 7)
            {
                c = "0" + c;
            }
            s += $"{c}|";
        }
        Debug.Log(s);
    }
    
    void DebugLong(string s, ulong l)
    {
        var ls = Convert.ToString((long)l, 2);
        var d = s;
        while (ls.Length < 64)
        {
            ls = "0" + ls;
        }
        for (int i = 0; i < 8; ++i)
        {
            s += ls.Substring(i * 8, 8) + "|";
        }
        Debug.Log(s);

        d += "xxxxxxxx|";
        for (int i = 0; i < 8; ++i)
        {
            d += ls.Substring(8 + i * 7, 7) + "|";
        }
        Debug.Log(d);
    }

    void DebugBytes(string s, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; ++i)
        {
            s += $"{Convert.ToString(bytes[i], 16).PadLeft(2, '0')}|";
        }
        Debug.Log(s);
    }

    // returns true if state changed
    bool Deserialize()
    {
        // nothing happened yet
        if (shuffleState0.Length == 0) {
            //Debug.Log("empty shuffle state, not reading");
            return false;
        }
        // XXX udon can't do string[idx] 
        var first = (int)shuffleState0.Substring(0, 1).ToCharArray()[0];

        var seq = (first >> 4) & 15;
        // skip rest if we've already seen this packet
        // TODO skip for debugging
        if (seq == lastReadSeq)
        {
            //Debug.Log($"already saw seqNo {seq}, skipping");
            return false;
        }
        lastReadSeq = seq;

        isDealt = (first & 1) == 0; // last bit is isDealt

        var chars = new char[177];
        shuffleState0.ToCharArray().CopyTo(chars, 0);
        shuffleState1.ToCharArray().CopyTo(chars, 88);
        int n = 1; // skip first
        for (int i = 0; i < shuffleStateSize;)
        {
            //DebugChars("deser: ", chars, n);
            // pack 8 asciis into 56 bits;
            ulong pack =         chars[n++];
            pack = (pack << 7) + chars[n++];
            pack = (pack << 7) + chars[n++];
            pack = (pack << 7) + chars[n++];
            
            pack = (pack << 7) + chars[n++];
            pack = (pack << 7) + chars[n++];
            pack = (pack << 7) + chars[n++];
            pack = (pack << 7) + chars[n++];
            //DebugLong("unpacked: ", pack);

            // unpack into 7 bytes
            shuffleState[i++] = (byte)((pack >> 48) & (ulong)255);
            shuffleState[i++] = (byte)((pack >> 40) & (ulong)255);
            shuffleState[i++] = (byte)((pack >> 32) & (ulong)255);
            shuffleState[i++] = (byte)((pack >> 24) & (ulong)255);

            shuffleState[i++] = (byte)((pack >> 16) & (ulong)255);
            shuffleState[i++] = (byte)((pack >> 8)  & (ulong)255);
            shuffleState[i++] = (byte)((pack >> 0)  & (ulong)255);
        }
        DebugBytes($"read shuffle state {seq}: isDealt {isDealt}, ", shuffleState);

        return true;
    }
}
