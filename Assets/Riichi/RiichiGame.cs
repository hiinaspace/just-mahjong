#define DEBUG
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

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
///   
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
    [HideInInspector]
    public int[] scores = new int[4];

    // TODO  the HAND state is really hard to make 'stick' compared to upright
    // in that the checking for locally moved tiles would need to check for exact
    // equality for all our hand positions. We don't, so sorted hand tiles pretty much
    // immediately go into UPRIGHT state the next frame, making the HAND packing not
    // that useful even though it's 4x smaller. 13 * 4 = 52 bytes, plus 18 in the discard
    // for another 72 bytes is only 124 bytes total, still not bad.
    const int DEAL = 0, HAND = 1, UPRIGHT = 2, TABLE = 3, ARBITRARY = 4;

    // byte sizes for each tile state
    // DEAL is zero because shuffle state is transmitted separately
    private int[] PACK_SIZE = new int[] { 0, 1, 4, 4, 9 };

    const int headerSize = 5;

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

    private Vector3[] uprightXZ = new Vector3[136]; // if UPRIGHT, xz on table pos, with constant y
    private float[] uprightYRot = new float[136]; // if UPRIGHT, Y euler axis rotation
    // I don't think i need upsidedown but upright tiles, they can be arbitrary
    
    private Vector3[] tableXZ = new Vector3[136]; // if TABLE, xy on table pos with constant y
    private float[] tableYRot = new float[136]; // if TABLE, Y euler axis rotation
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
    private BoxCollider[] tileBoxColliders = new BoxCollider[136];

    private RiichiTile[] riichiTiles;

    // note upwards (Y) is for upright tiles; Z is up for tiles on the table;
    private Vector3 tileDimensions = new Vector3(0.0375f, 0.05f, 0.032f);
    private float uprightY;
    private float tableY;

    // some preset transforms for positioning
    private Transform[] dealTransforms = new Transform[136];
    private Transform[][] handTransforms;

    // last know tile transform
    private Vector3[] lastKnownPos = new Vector3[136];
    private Quaternion[] lastKnownRot = new Quaternion[136];

    // XXX kludge for syncing score
    bool scoreChanged = false;

    void Start()
    {
        uprightY = tileDimensions.y / 2;
        tableY = tileDimensions.z / 2;

        var tileParent = transform.Find("Tiles");
        var dealParent = transform.Find("Placements");
        riichiTiles = new RiichiTile[136];
        for (int i = 0; i < 136; ++i)
        {
            var tile = tileParent.GetChild(i).gameObject;
            tiles[i] = tile;
            tileTransforms[i] = tile.transform;
            tileBoxColliders[i] = tile.GetComponent<BoxCollider>();

            var rt = tile.GetComponent<RiichiTile>();
            riichiTiles[i] = rt;

            dealTransforms[i] = dealParent.GetChild(i);

            shuffleOrder[i] = i;
            locallyMovedTiles[i] = false;

            uprightXZ[i] = new Vector3(0, uprightY, 0);
            tableXZ[i] = new Vector3(0, tableY, 0);

            lastKnownPos[i] = Vector3.zero;
            lastKnownRot[i] = Quaternion.identity;
        }
        handTransforms = new Transform[4][];
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
        return Networking.IsOwner(seats[EAST].gameObject) && seats[EAST].playerSeated;
    }

    // to cut down on Update() time, only check a subset of the tiles each frame
    private int localTileCursor;
    private const int checkedTilesPerUpdate = 17;

    private float sendWait;
    private const float sendInterval = 1f;

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

    private float shuffleStateWait = 0;
    private const float shuffleStateInterval = 2f;

    float disableWait = 0;

    bool rotEq(Quaternion a, Quaternion b)
    {
        return Mathf.Abs(Quaternion.Dot(a, b)) > 0.999f;
    }

    void Update()
    {
        // check recv buffer, read any packets
        while (bus.recvBufferHead != recvBufIdx)
        {
            var packet = bus.recvBuffer[recvBufIdx];

            ReadPacket(packet);

            recvBufIdx = (recvBufIdx + 1) % bus.recvBufferSize;
        }

        // check ack buffer, undirty any tiles we sent
        while (bus.successfulAckedHead != ackBufIdx)
        {
            var ackedTiles = (int[])bus.successfulAckedObjects[ackBufIdx];
            if (ackedTiles != null)
            {
                for (int i = 0; i < ackedTiles.Length; ++i)
                {
                    var ack = ackedTiles[i];
                    if (ack == 255) break;
                    var rt = riichiTiles[ack];
                    if (rt.IsCustomOwnedAndNotInDealPosition())
                    {
                        rt.SetBackColorOffset(Color.blue);
                    }
                    locallyMovedTiles[ack] = false;
                }
            }
            ackBufIdx = (ackBufIdx + 1) % bus.recvBufferSize;
        }

        // skip other stuff if table is idle
        if (!gameStarted) return;

        if (IsSeated())
        {
            CheckMovedLocalTiles();

            // if we're the table owner, send out deal state packets every once and a while
            if ((shuffleStateWait -= Time.deltaTime) < 0)
            {
                shuffleStateWait = shuffleStateInterval;
                SendShuffleState();
            } else
            {
                BroadcastTiles();
            }
        } else
        {
            // XXX if player isn't seated, dont let them touch tiles
            if ((disableWait -= Time.deltaTime) > 0) return;
            disableWait = 1f;
            DisableTiles();
        }
    }

    int GetServerTime()
    {
        if (Networking.LocalPlayer == null) // editor
        {
            return Mathf.FloorToInt(Time.time * 1000);
        }
        return Networking.GetServerTimeInMilliseconds();
    }

    void SendShuffleState()
    {
        // only EAST needs to send these out. If we're master but not EAST, don't bother
        // XXX IsTableOwner very confusing, i know
        if (!Networking.IsOwner(seats[EAST].gameObject)) return;
        //Debug.Log($"yes we're table owner for shuffle");
        // if bus not ready, or we put a packet there
        if (!bus.sendReady || bus.sendBufferReady) return;
        var buf = bus.sendBuffer;

        //Debug.Log($"sending shuffle state");

        WriteHeader(buf, true, GetServerTime());

        WriteDealBitmap(headerSize, buf);

        // write out entire shuffle order
        var n = headerSize + 17;
        for (int i = 0; i < 136; ++i)
        {
            buf[n++] = (byte)shuffleOrder[i];
        }

        // empty ack object; we'll send the shuffle state periodically anyway
        bus.sendAckObject = new int[0]; 
        bus.sendBufferReady = true;
    }

    float[] lastMoved = new float[136];
    
    void CheckMovedLocalTiles()
    {
        for (int i = 0; i < checkedTilesPerUpdate; ++i)
        {
            var n = localTileCursor++;
            localTileCursor %= 136;

            var rt = riichiTiles[n];

            if (rt.IsCustomOwnedAndNotInDealPosition())
            {
                var t = tileTransforms[n];
                var p = t.localPosition;
                var r = t.localRotation;
                var lp = lastKnownPos[n];
                var lr = lastKnownRot[n];

                bool recentlyMoved = (Time.time - lastMoved[n]) < 1f;
                bool newlyMoved = !recentlyMoved && (p != lp || !rotEq(r, lr));
                if (newlyMoved)
                {
                    lastMoved[n] = Time.time;
                }

                if (newlyMoved || recentlyMoved)
                {
                    //Debug.Log($"{gameId} found {n} tile moved locally");
                    lastKnownPos[n] = p;
                    lastKnownRot[n] = r;
                    locallyMovedTiles[n] = true;

                    rt.SetBackColorOffset(new Color(0.5f, 0.1f, 0.5f));

                    UpdateInternalTileState(n);
                }
            }
        }
    }

    void BroadcastTiles()
    {
        // load any dirty tiles into send buffer when ready
        sendWait -= Time.deltaTime;
        if (sendWait > 0) return;
        // if bus not ready, or we put a packet there
        if (!bus.sendReady || bus.sendBufferReady) return;
        sendWait = UnityEngine.Random.Range(.0f, .2f) + sendInterval;

        var buf = bus.sendBuffer;

        int n = headerSize + 2; 
        var limit = maxDataByteSize - 1; // 1 byte for EOF

        int[] presentTiles = new int[137]; // one extra slot for an EOF after 136 tiles, though it'll never happen
        int j = 0;

        for (int i = 0; i < 136; ++i)
        {
            if (!locallyMovedTiles[i]) continue;
            var state = tileState[i];
            if (state == DEAL) continue; // skip deal packets, table owner will broadcast those

            var packSize = PACK_SIZE[state];
            if (n + 1 + packSize >= limit) break; // not enough room

            presentTiles[j++] = i;
            buf[n++] = (byte)i;
            //Debug.Log($"{gameId} wrote tile {i} state {state} at pos {n}");
            switch (state)
            {
                case HAND: PackHand(i, n, buf); break;
                case UPRIGHT: PackUpright(i, n, buf); break;
                case TABLE: PackTable(i, n, buf); break;
                case ARBITRARY: PackArbitrary(i, n, buf); break;
            }
            n += packSize;
        }

        if (j == 0 && !scoreChanged)
        {
            // actually no tiles changed so leave bus how it is.
            return;
        }

        WriteHeader(buf, false, GetServerTime());

        WriteSeatAndScore(buf, headerSize); // after header
        scoreChanged = false;

        buf[n] = 255; // EOF in packet
        presentTiles[j] = 255; // EOF in ack object

        //Debug.Log($" sent packet from {gameId}");

        bus.sendAckObject = presentTiles; 
        bus.sendBufferReady = true;
    }

    void ReadPacket(byte[] packet)
    {
        var header = packet[0];
        var packetGameId = (header >> 6) & 3;

        var packetTime = ReadSInt(1, packet);
        //Debug.Log($"{gameId} read packet for {packetGameId} {header}");
        if (packetGameId != gameId) return;

        var packetIsShuffle = (header & 1) == 1;
        if (packetIsShuffle)
        {
            // avoid checking local tiles until first shuffle packet from table owner
            gameStarted = true;
        } 

        if (packetIsShuffle)
        {
            //DebugBytes($"{gameId} got shuffle packet ", packet, 182);
            bool[] inShuffle = ReadTileBitmap(headerSize, packet);
            for (int i = 0; i < 136; ++i)
            {
                shuffleOrder[i] = packet[i + headerSize + 17];

                var rt = riichiTiles[i];
                // if tile in shuffle and we haven't moved it locally in the meantime
                if (inShuffle[i] && rt.IsRemoteTile(packetTime))
                {
                    tileState[i] = DEAL;
                    rt.ReleaseCustomOwnership();
                    rt.SetBackColorOffset(new Color(0, 0.1f, 0));
                    MoveLocally(i);
                }
            }
        } else
        {
            //Debug.Log($"{gameId} got tile packet");
            //DebugBytes($"{gameId} got tile packet ", packet, 182);
            ReadSeatAndScore(packet, headerSize);
            int n = headerSize + 2;
            int idx = packet[n++];
            while (idx != 255) // EOF
            {
                //Debug.Log($"see packet {idx} at {n}");

                // tile is remote if
                //   it's kinematic (we never touched it locally since last MoveLocally)
                //   it's nonKinematic but we haven't touched it since 
                // TODO also check packet timestamp against last locally moved or picked up.
                var rt = riichiTiles[idx];
                var remoteTile = rt.IsRemoteTile(packetTime);

                n += ReadTile(idx, n, packet, remoteTile);

                if (remoteTile)
                {
                    rt.ReleaseCustomOwnership();
                    MoveLocally(idx);
                }

                idx = packet[n++];
            }
        }
    }

    void MoveLocally(int idx)
    {
        var t = tileTransforms[idx];
        switch (tileState[idx])
        {
            case DEAL:
                var dt = dealTransforms[shuffleOrder[idx]];
                t.SetPositionAndRotation(dt.position, dt.rotation);
                break;
            case HAND:
                var ht = handTransforms[handSeat[idx]][handOrder[idx]];
                t.SetPositionAndRotation(ht.position, ht.rotation);
                break;
            case UPRIGHT:
                t.localPosition = uprightXZ[idx];
                t.localRotation = Quaternion.Euler(0, uprightYRot[idx], 0);
                break;
            case TABLE:
                t.localPosition = tableXZ[idx];
                t.localRotation = Quaternion.Euler(tableUp[idx] ? 270 : 90, tableYRot[idx], 0);
                break;
            case ARBITRARY:
                t.localPosition = arbitraryTilePositions[idx];
                t.localRotation = arbitraryTileRotations[idx];
                break;
        }
    }

    string print(Vector3 v)
    {
        return $"({v.x}, {v.y}, {v.z})";
    }
    void DebugBytes(string s, byte[] bytes, int len)
    {
        for (int i = 0; i < len; ++i)
        {
            s += $"{Convert.ToString(bytes[i], 16).PadLeft(2,'0')}|";
        }
        Debug.Log(s);
    }

    // packet format:
    // 
    // [2 bits game id]       disambiguate multiple games on same Bus
    // [6 bits empty]         TODO use for something
    // [1 bit isShuffle]
    //
    // [4 bytes serverTime]
    //
    // if isShuffle:
    // [17 bytes bitmap]      tiles that are in deal position
    // [136 bytes]            shuffleOrder
    // else:
    // [2 bits seat] [14 bits player score]
    // variable length:
    // [1 byte tile idx]
    // [1] [1 bit up or down] [8 bits euler z] [11 bits x] [11 bits z] = 4 bytes, on table
    // [0] [1] [2 bits seat pos] [4 bits hand pos] = 1 byte, in hand
    // [0] [0] [1] [7 bits euler y] [11 bits x] [11 bits z] = 4 bytes, upright on table
    // [0] [0] [0] [1] [12 bits x] [12 bits y] [12 bits z] [2 bits largest component] [30 bits components] = 9 bytes
    //
    // since tiles will eventually get acked, regular tile states will be pretty short, 1 tile that's currently moving
    // 13 tiles on hand sort.
    #region bitpacking methods
    void WriteSeatAndScore(byte[] buf, int n)
    {
        // XXX kinda hacky
        int seat = 0;
        for (int i = 0; i < 4; ++i)
        {
            if (Networking.IsOwner(seats[i].gameObject) && seats[i].playerSeated)
            {
                seat = i; break;
            }
        }

        int score = scores[seat] / 100;

        buf[n] = (byte)((seat << 6) + ((score >> 8) & 63));
        buf[n+1] = (byte)(score & 255);
    }

    void ReadSeatAndScore(byte[] buf, int n)
    {
        int seat = (buf[n] >> 6) & 3;
        int score = ((buf[n] & 63) << 8) + buf[n +1];
        scores[seat] = score * 100;
    }

    int ReadTile(int idx, int n, byte[] buf, bool remoteTile)
    {
        // XXX remoteTile checks to avoid clobbering our own tile state
        // very messy.
        // add tile to the ring buffer
        var first = buf[n];
        if ((first & 128) > 0) // table
        {
            if (remoteTile)
            {
                tileState[idx] = TABLE;
                ReadTable(idx, n, buf);
            }
            //Debug.Log($"read tile {idx} TABLE at {n}, {print(tableXZ[idx])} {tableYRot[idx]} {tableUp[idx]}");
            return 4;
        }
        else if ((first & 64) > 0) // hand
        {
            //Debug.Log($"reading tile {idx} HAND at {n}");
            if (remoteTile)
            {
                tileState[idx] = HAND;
                ReadHand(idx, n, buf);
            }
            return 1;
        }
        else if ((first & 32) > 0) // upright
        {
            if (remoteTile)
            {
                ReadUpright(idx, n, buf);
                tileState[idx] = UPRIGHT;
            }
            //Debug.Log($"read tile {idx} UPRIGHT at {n}, {print(uprightXZ[idx])} {uprightYRot[idx]}");
            return 4;
        }
        else // arbitrary
        {
            if (remoteTile)
            {
                tileState[idx] = ARBITRARY;
                ReadArbitrary(idx, n, buf);
            }
            //Debug.Log($"read tile {idx} ARBITRARY at {n} {print(arbitraryTilePositions[idx])} {arbitraryTileRotations[idx]}");
            return 9;
        }
    }
    void WriteHeader(byte[] buf, bool isShuffle, int serverTimeMillis)
    {
        int header = gameId;
        //header = (header << 5) + gameEpoch;
        header = (header << 6) + (isShuffle ? 1 : 0);
        buf[0] = (byte)header;
        //Debug.Log($"writing server time {serverTimeMillis}");
        WriteSInt(serverTimeMillis, 1, buf);
        //DebugBytes("wrote header: ", buf, 5);
    }

    void WriteDealBitmap(int n, byte[] buf)
    {
        for (int i = 0; i < 17; ++i)
        {
            var j = i * 8;
            buf[n + i] = (byte)(
                (tileState[j] == DEAL ? 128 : 0) +
                (tileState[j + 1] == DEAL ? 64 : 0) +
                (tileState[j + 2] == DEAL ? 32 : 0) +
                (tileState[j + 3] == DEAL ? 16 : 0) +
                (tileState[j + 4] == DEAL ? 8 : 0) +
                (tileState[j + 5] == DEAL ? 4 : 0) +
                (tileState[j + 6] == DEAL ? 2 : 0) +
                (tileState[j + 7] == DEAL ? 1 : 0));
        }
    }

    bool[] ReadTileBitmap(int n, byte[] buf)
    {
        bool[] boolmap = new bool[136];
        var j = 0;
        for (int i = 0; i < 17; ++i)
        {
            int b = buf[n + i];
            boolmap[j++] = (b & 128) > 0;
            boolmap[j++] = (b & 64) > 0;
            boolmap[j++] = (b & 32) > 0;
            boolmap[j++] = (b & 16) > 0;
            boolmap[j++] = (b & 8) > 0;
            boolmap[j++] = (b & 4) > 0;
            boolmap[j++] = (b & 2) > 0;
            boolmap[j++] = (b & 1) > 0;
        }
        return boolmap;
    }

    void WriteInt(uint i, int pos, byte[] buf)
    {
        buf[pos]   = (byte)(i >> 24);
        buf[pos+1] = (byte)((i >> 16) & 255);
        buf[pos+2] = (byte)((i >> 8) & 255);
        buf[pos+3] = (byte)(i & 255);
    }
    uint ReadInt(int n, byte[] buf)
    {
        uint pack =          buf[n];
        pack = (pack << 8) + buf[n+1];
        pack = (pack << 8) + buf[n+2];
        pack = (pack << 8) + buf[n+3];
        return pack;
    }
    void WriteSInt(int i, int pos, byte[] buf)
    {
        buf[pos]   = (byte)((i >> 24) & 255);
        buf[pos+1] = (byte)((i >> 16) & 255);
        buf[pos+2] = (byte)((i >> 8) & 255);
        buf[pos+3] = (byte)(i & 255);
    }
    int ReadSInt(int n, byte[] buf)
    {
        int pack =          buf[n];
        pack = (pack << 8) + buf[n+1];
        pack = (pack << 8) + buf[n+2];
        pack = (pack << 8) + buf[n+3];
        return pack;
    }

    void ReadTable(int i, int n, byte[] buf)
    {
        //DebugTablePack(buf, n);
        uint p = ReadInt(n, buf);
        tableUp[i] = ((p >> 30) & 1) > 0;
        tableYRot[i] = UnpackFloat((p >> 22) & 255U, 0, 360, 255);
        var xz = tableXZ[i];
        xz.x = UnpackFloat((p >> 11) & 2047U, -1, 1, 2047);
        xz.z = UnpackFloat(p & 2047U, -1, 1, 2047);
        // XXX somehow the property assignment doesn't apply to array reference, but does to local var?
        tableXZ[i] = xz; 
    }

    void PackTable(int i, int n, byte[] buf)
    {
        uint p = 2U + (tableUp[i] ? 1U : 0U);
        p = (p << 8) + PackFloat(Mathf.Repeat(tableYRot[i], 360), 0, 360, 255);
        var v = tableXZ[i];
        p = (p << 11) + PackFloat(v.x, -1, 1, 2047);
        p = (p << 11) + PackFloat(v.z, -1, 1, 2047);
        WriteInt(p, n, buf);
        //DebugTablePack(buf, n);
    }

    void ReadHand(int i, int n, byte[] buf)
    {
        int p = buf[n];
        handSeat[i] = (p >> 4) & 3;
        handOrder[i] = p & 15;
    }

    void PackHand(int i, int n, byte[] buf)
    {
        // int p = 1 << 2;
        int p = 4 + handSeat[i];
        p = (p << 4) + handOrder[i];
        buf[n] = (byte)p;
    }
    void ReadUpright(int i, int n, byte[] buf)
    {
        uint p = ReadInt(n, buf);
        uprightYRot[i] = UnpackFloat((p >> 22) & 127U, 0, 360, 127);

        var xz = uprightXZ[i];
        xz.x = UnpackFloat((p >> 11) & 2047U, -1, 1, 2047);
        xz.z = UnpackFloat(p & 2047U, -1, 1, 2047);
        // XXX somehow the property assignment doesn't apply to array reference, but does to local var?
        uprightXZ[i] = xz; 
    }

    void PackUpright(int i, int n, byte[] buf)
    {
        // int p = 1 << 7;
        uint p = 128U + PackFloat(Mathf.Repeat(uprightYRot[i], 360), 0, 360, 127);
        var v = uprightXZ[i];
        p = (p << 11) + PackFloat(v.x, -1, 1, 2047);
        p = (p << 11) + PackFloat(v.z, -1, 1, 2047);

        WriteInt(p, n, buf);
    }

    void ReadArbitrary(int i, int n, byte[] buf)
    {
        // 0001xxxx|xxxxxxxx|yyyyyyyy|yyyyzzzz|zzzzzzz
        uint px = (((uint)buf[n] & 15U) << 8) + (uint)buf[n + 1];
        uint py = ((uint)buf[n+2] << 4) + ((uint)(buf[n + 3] >> 4) & 15U);
        uint pz = (((uint)buf[n+3] & 15U) << 8) + (uint)buf[n + 4];

        arbitraryTilePositions[i] = new Vector3(
            UnpackFloat(px, -2, 2, 4095),
            UnpackFloat(py, 0, 3, 4095),
            UnpackFloat(pz, -2, 2, 4095));

        arbitraryTileRotations[i] = UnpackQuaternion(buf, n + 5);
    }

    void PackArbitrary(int i, int n, byte[] buf)
    {
        var v = arbitraryTilePositions[i];
        var px = PackFloat(v.x, -2, 2, 4095);
        var py = PackFloat(v.y, 0, 3, 4095);
        var pz = PackFloat(v.z, -2, 2, 4095);
        // 0001xxxx|xxxxxxxx|yyyyyyyy|yyyyzzzz|zzzzzzz
        buf[n++] = (byte)(16 + ((px >> 8) & 15));
        buf[n++] = (byte)(px & 255);
        buf[n++] = (byte)((py >> 4) & 255);
        buf[n++] = (byte)(((py & 15) << 4) + ((pz >> 8) & 15));
        buf[n++] = (byte)(pz & 255);

        PackQuaternion(arbitraryTileRotations[i], buf, n);
    }

    uint PackFloat(float f, float min, float max, int bitmask)
    {
        f = Mathf.Clamp(f, min, max);
        f = Mathf.InverseLerp(min, max, f);
        return (uint)Mathf.RoundToInt(f * bitmask);
    }
    float UnpackFloat(uint i, float min, float max, int bitmask)
    {
        return Mathf.Lerp(min, max, (float)i / bitmask);
    }

    void PackQuaternion(Quaternion q, byte[] array, int idx)
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

        uint x = PackFloat(q.x / largest, -1, 1, 1023);
        uint y = PackFloat(q.y / largest, -1, 1, 1023);
        uint z = PackFloat(q.z / largest, -1, 1, 1023);
        uint w = PackFloat(q.w / largest, -1, 1, 1023);

        uint pack = largest_idx;
        if (largest_idx != 0) pack = ((pack << 10) + x);
        if (largest_idx != 1) pack = ((pack << 10) + y);
        if (largest_idx != 2) pack = ((pack << 10) + z);
        if (largest_idx != 3) pack = ((pack << 10) + w);
        WriteInt(pack, idx, array);
    }

    Quaternion UnpackQuaternion(byte[] array, int idx)
    {

        var pack = ReadInt(idx, array);
        uint largest_idx = pack >> 30;
        float a = UnpackFloat(((pack >> 20) & 1023U), -1, 1, 1023);
        float b = UnpackFloat(((pack >> 10) & 1023U), -1, 1, 1023);
        float c = UnpackFloat((pack & 1023U), -1, 1, 1023);
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

    // Mathf.Approximately too exact for me
    bool ApproxEq(float a, float b)
    {
        return Mathf.Abs(a - b) < 0.001f;
    }

    private void UpdateInternalTileState(int n)
    {
        // don't bother checking for DEAL or HAND; those states are entered through
        // deal or sorting, and not worth checking to see if the player somehow lined
        // up a tile right back to where it was. 
        var t = tileTransforms[n];
        var p = t.localPosition;
        var r = t.localRotation;
        var e = r.eulerAngles;
        // t.up is normalized local Y (in world coords), so tile is upright when it's mostly 1.
        if (ApproxEq(p.y, uprightY) && t.up.y > 0.9)
        {
            //Debug.Log($"tile {n} moved to UPRIGHT at {p.x} {p.y} {p.z}");
            tileState[n] = UPRIGHT;
            uprightXZ[n].x = p.x;
            uprightXZ[n].z = p.z;
            uprightYRot[n] = e.y;
        }
        // t.forward is straight up (or down) for on table tiles
        else if (ApproxEq(p.y, tableY) && Mathf.Abs(t.forward.y) > 0.9)
        {
            tileState[n] = TABLE;
            tableXZ[n].x = p.x;
            tableXZ[n].z = p.z;
            tableYRot[n] = e.y;
            tableUp[n] = t.forward.y > 0;
            //Debug.Log($"tile {n} moved to TABLE at {p.x} {p.y} {p.z} {e.y} ({e}) isUp = {tableUp[n]}");
        }
        // from IsSleeping check, assume it's now in some new arbitrary position
        else {
            //Debug.Log($"tile {n} moved to ARBITRARY at {p.x} {p.y} {p.z}");
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

    public void DoResync()
    {
        Debug.Log($"Local resync requested, invalidating all tiles");
        for (int i = 0; i < 136; ++i)
        {
            locallyMovedTiles[i] = true;
            var state = tileState[i];
            if (state != DEAL && state != HAND)
            {
                riichiTiles[i].SetBackColorOffset(new Color(0.5f, 0.1f, 0.5f));
                UpdateInternalTileState(i);
            }
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

        for (int i = 0; i < 136; ++i)
        {
            tileState[i] = DEAL;

            var rt = (RiichiTile)riichiTiles[i];
            rt.TakeOwnershipForShuffle();

            var tileT = tileTransforms[i];
            var dealT = dealTransforms[shuffleOrder[i]];
            tileT.SetPositionAndRotation(dealT.position, dealT.rotation);
        }
    }

    public void EnableTiles()
    {
        for (int j = 0; j < 136; ++j)
        {
            tileBoxColliders[j].enabled = true;
        }
    }
    public void DisableTiles()
    {
        for (int j = 0; j < 136; ++j)
        {
            ((RiichiTile)riichiTiles[j]).ReleaseCustomOwnership();
            tileBoxColliders[j].enabled = false;
        }
    }

    public void SortHand(int seat)
    {
        // XXX odd place to do it, not sure yet where to put
        EnableTiles();

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

            // XXX
            if (i >= placements.Length) break;

            var placement = placements[i];
            tile.SetPositionAndRotation(placement.position, placement.rotation);

            // update internal state
            // XXX linear probe, oh well;
            for (int j = 0; j < 136; ++j)
            {
                if (tiles[j] == obj)
                {
                    var rt = riichiTiles[j];
                    rt.TakeCustomOwnership();
                    rt.SetBackColorOffset(new Color(1.0f, 0.1f, 0));

                    tileState[j] = HAND;
                    handOrder[j] = i;
                    handSeat[j] = seat;
                    // reset last known so we won't detect it as moved
                    lastKnownPos[i] = tile.localPosition;
                    lastKnownRot[i] = tile.localRotation;
                    break;
                }
            }
        }
    }

    private void Sort(Collider[] tiles)
    {
        // finally those Art of Programming books have value
        for (int i = 1; i < tiles.Length; ++i)
        {
            // tile gameobject names are sorted by tile value and suit
            // since that's more convenient than trying to read values out
            // of some custom udon component
            var tile1 = tiles[i];
            var j = i - 1;
            while (j >= 0 && tiles[j].gameObject.name.CompareTo(tile1.gameObject.name) > 0)
            {
                tiles[j + 1] = tiles[j];
                j--;
            }
            tiles[j + 1] = tile1;
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
        scoreChanged = true;
    }

    private float lastResync = float.MinValue;

    // button to send network rpc to other clients to rebroadcast all their
    // tile and game states.
    public void RequestResync()
    {
        if (Time.time - lastResync < 5) return; // debounce locally
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "DoResync");
        DoResync(); // also do locally since SendCustomNetworkEvent doesn't loopback
        lastResync = Time.time;
    }

    string toBin(byte b)
    {
        var c = Convert.ToString(b, 2);
        return c.PadLeft(8, '0');
    }
    void DebugTablePack(byte[] bytes, int i)
    {
        var c = toBin(bytes[i]);
        c += "|" + toBin(bytes[i + 1]);
        c += "|" + toBin(bytes[i + 2]);
        c += "|" + toBin(bytes[i + 3]);
        Debug.Log($"table pack: {c}");
    }
}
