#define DEBUG
using System;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
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
    public LayerMask tenbouLayer;

    const int EAST = 0, SOUTH = 1, WEST = 2, NORTH = 3;

    // XXX avoid moving tiles until initial shuffle; this is implicitly synced
    // to 'true' as soon as the local client gets any packet related to the game, 
    // first by the table owner when they press the shuffle button.
    private bool gameStarted = false;

    // XXX while all of these parallel arrays could be moved to fields in the
    // RiichiTile behaviour, Udon doesn't let you see updates to the fields of
    // other behaviours until the next frame, so in practice it's easier to
    // maintain all these parallel arrays.

    // private bool sanmaMode = TODO maybe eventually

    // abstract game state:
    //public Transform centerThing;
    //private int roundWind = EAST;
    //private int roundNo = 0;
    //private int honba = 0;
    //// XXX the dealer's wind will be east despite being on a different
    //// side of the table; this just controls the rotation of the wind indicator
    //private int dealer = EAST;

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

    // game id, seat id, isShuffle packed into byte
    const int headerSize = 1;

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
    private float[] tableZRot = new float[136]; // if TABLE, Z euler axis rotation
    private bool[] tableUp = new bool[136]; // if TABLE, whether tile is face-up or down

    // if ARBITRARY, full precision pos/rot
    // local to the Tile root (center of the table)
    private Vector3[] arbitraryTilePositions = new Vector3[136]; 
    private Quaternion[] arbitraryTileRotations = new Quaternion[136];

    // network efficiency state
    // (owned) local tiles need to be broadcast if they were last moved after they were
    // last acked.
    private float[] lastMoved = new float[136];
    private float[] lastAcked = new float[136];

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

    // and since we have room in the byte indices we use for tiles,
    // also sync the tenbou sticks in basically the same way
    private Transform[] tenbou = new Transform[68];
    private Rigidbody[] tenbouRigidbodies = new Rigidbody[68];
    private object[] tenbouVrcPickups = new object[68];
    private int tenbouCursor;
    private Vector3[] tenbouLastKnownPos = new Vector3[68];
    private Quaternion[] tenbouLastKnownRot = new Quaternion[68];
    private float[] tenbouLastMoved = new float[68];
    private float[] tenbouLastAcked = new float[68];

    public Text debugText;
    private const int LOG_SIZE = 24;
    private string[] logLines = new string[LOG_SIZE];
    private int logLine = 0;
    float logWait;
    const float logInterval = 0.25f;

    // to cut down on Update() time, only check a subset of the tiles each frame
    private int localTileCursor;
    private const int checkedTilesPerUpdate = 1;

    private float sendWait;
    private const float sendInterval = 0.5f;

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

    // set to true on shuffle so we send the shuffle packet at least once
    // also true on resyncs
    private bool needShufflePacket = false;

    float disableWait = 0;

    float scoreWait = 0;
    const float scoreInterval = 1f;

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

            uprightXZ[i] = new Vector3(0, uprightY, 0);
            tableXZ[i] = new Vector3(0, tableY, 0);

            lastKnownPos[i] = Vector3.zero;
            lastKnownRot[i] = Quaternion.identity;
        }
        handTransforms = new Transform[4][];
        for (int i = 0; i < 4; ++i)
        {
            var handParent = transform.Find($"HandPlacements{i}");
            handTransforms[i] = new Transform[16];
            for (int j = 0; j < 16; ++j)
            {
                handTransforms[i][j] = handParent.GetChild(j);
            }
        }
        var tenbouParent = transform.Find("Tenbou");
        for (int i = 0; i < 68; ++i)
        {
            var t = tenbouParent.GetChild(i);
            tenbou[i] = t;
            tenbouVrcPickups[i] = (VRC_Pickup)t.gameObject.GetComponent(typeof(VRC_Pickup));
            tenbouRigidbodies[i] = t.gameObject.GetComponent<Rigidbody>();
        }

        // this behavior is running on a new player (in this map, not game), so request
        // that all clients start rebroadcasting their state for us.
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "DoResync");
    }

    #region Debug state

    private void WriteDebugState()
    {
        var s =  $@"
game: {gameId} gameStarted: {gameStarted}
shuffleHash: {shuffleHash()} 
unbroadcast local tiles: {unbroadcastCount()}
unbroadcast tenbou: {unbroadcastTenbou()}
sendWait: {sendWait}
isTableOwner: {IsTableOwner()} isSeated: {IsSeated()}
EAST  player: {getSeatOwner(EAST)}
SOUTH player: {getSeatOwner(SOUTH)}
WEST  player: {getSeatOwner(WEST)}
NORTH player: {getSeatOwner(NORTH)}
";
        var j = (logLine + 1) % LOG_SIZE;
        for (int i = 0; i < LOG_SIZE; ++i)
        {
            s += logLines[j++] + "\n";
            j %= LOG_SIZE;
        }
        debugText.text = s;
    }

    private int unbroadcastCount()
    {
        int n = 0;
        for (int i = 0; i < 136; ++i)
        {
            if (lastMoved[i] > lastAcked[i]) n++;
        }
        return n;
    }
    private int unbroadcastTenbou()
    {
        int n = 0;
        for (int i = 0; i < 68; ++i)
        {
            if (tenbouLastMoved[i] > tenbouLastAcked[i]) n++;
        }
        return n;
    }

    private int shuffleHash()
    {
        int h = 0;
        for (int i = 0; i < 136; ++i)
        {
            h = 31 * h + shuffleOrder[i];
        }
        return h;
    }
    private string getSeatOwner(int seat)
    {
        var s = seats[seat];
        var p = Networking.GetOwner(s.gameObject);
        if (p == null)
        {
            return "Editor";
        }
        return $"{p.displayName} seated: {s.playerSeated}";
    }

    void LogInternal(string s)
    {
        logLines[logLine++] = $"{Networking.GetNetworkDateTime()}: {s}";
        logLine %= LOG_SIZE;
    }

    #endregion

    private bool IsTableOwner()
    {
        return Networking.IsOwner(seats[EAST].gameObject) && seats[EAST].playerSeated;
    }
    
    // get seat we're currently in, kinda hacky
    private int GetSeat()
    {
        int seat = -1;
        for (int i = 0; i < 4; ++i)
        {
            if (Networking.IsOwner(seats[i].gameObject) && seats[i].playerSeated)
            {
                seat = i; break;
            }
        }
        return seat;
    }

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
            // packed float array of idx (int) and then the lastMoved time as sent
            var acked = (float[])bus.successfulAckedObjects[ackBufIdx];
            if (acked != null)
            {
                for (int i = 0; i < acked.Length; i += 2)
                {
                    var ack = Mathf.FloorToInt(acked[i]);
                    var ackTime = acked[i + 1];
                    if (ack == 255) break; // eof

                    // XXX tenbou are above 136
                    if (ack < 136)
                    {
                        var rt = riichiTiles[ack];
                        if (rt.IsCustomOwnedAndNotInDealPosition())
                        {
                            rt.SetBackColorOffset(Color.blue);
                        }
                        lastAcked[ack] = ackTime;
                    } else
                    {
                        tenbouLastAcked[ack - 136] = ackTime;
                    }
                }
            }
            ackBufIdx = (ackBufIdx + 1) % bus.recvBufferSize;
        }

        // skip other stuff if table is idle
        if (gameStarted)
        {
            if (IsSeated())
            {
                CheckMovedLocalTiles();
                CheckMovedTenbou();

                sendWait -= Time.deltaTime;
                // if bus not ready, or we put a packet there
                if (sendWait < 0 && bus.sendReady && !bus.sendBufferReady)
                {
                    sendWait = UnityEngine.Random.Range(.0f, .2f) + sendInterval;

                    if (needShufflePacket)
                    {
                        SendShuffleState();
                        needShufflePacket = false;
                    }
                    else
                    {
                        BroadcastTilesAndTenbou();
                    }
                }
            }
            else
            {
                // XXX if player isn't seated, dont let them touch tiles
                if ((disableWait -= Time.deltaTime) <= 0)
                {
                    disableWait = 5f;
                    DisableTiles();
                }
            }
        }
        if ((logWait -= Time.deltaTime) < 0)
        {
            logWait = logInterval;
            WriteDebugState();
        }
        if ((scoreWait -= Time.deltaTime) < 0)
        {
            scoreWait = scoreInterval;
            UpdateScores();
        }
    }

    private int[] scoreByTenbou = new int[] { 10000, 5000, 1000, 100 };

    void UpdateScores()
    {
        for (int i = 0; i < 4; ++i)
        {
            var rs = seats[i];
            var zone = rs.tenbouZone;
            var t = zone.transform;
            var halfExtents = zone.size / 2;
            var collide = Physics.OverlapBox(t.position, halfExtents, t.rotation, tenbouLayer);
            var len = collide.Length;
            var score = 0;
            for (int j = 0; j < len; ++j)
            {
                // parsing names is a useful kludge
                score += scoreByTenbou[int.Parse(collide[j].gameObject.name.Substring(0, 1))];
            }
            rs.UpdateScore(score);
        }

    }

    // shuffle packets are a kludge to be able to transmit the entire shuffle state in one packet, whereas
    // using a regular packet in the DEAL state would need some sort of runlength encoding to fit (naively,
    // 136 tile indices + 136 bytes for deal position.
    void SendShuffleState()
    {
        // only EAST needs to send these out. If we're master but not EAST, don't bother
        // XXX IsTableOwner very confusing, i know
        if (!Networking.IsOwner(seats[EAST].gameObject)) return;
        //Debug.Log($"yes we're table owner for shuffle");
        // if bus not ready, or we put a packet there
        var buf = bus.sendBuffer;

        // XXX kind of nasty ack object; we want to know both the index of the tile and its
        // lastMoved time that we're currently sending, so if a tile is still moving while
        // we're broadcasting, we know which move time we actually broadcast (and whether
        // we still need to broadcast more.
        float[] ackObj = new float[137 * 2];
        int ackI = 0;

        // shuffles only come from the east player
        WriteHeader(buf, EAST, true);

        // write bitmap for tiles still in DEAL state
        WriteDealBitmap(headerSize, buf);

        // write out entire shuffle order
        var n = headerSize + 17;
        var j = 0;
        for (int i = 0; i < 136; ++i)
        {
            buf[n++] = (byte)shuffleOrder[i];
            // and if DEAL tile, update ack obj
            if (tileState[j] == DEAL)
            {
                j++;
                ackObj[ackI++] = i;
                ackObj[ackI++] = lastMoved[i];
            }
        }

        ackObj[ackI] = 255; // EOF in ack object
        bus.sendAckObject = ackObj; 
        bus.sendBufferReady = true;

        LogInternal($"SEND shufl hash={shuffleHash()} tiles={j}");
    }
    
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

                if (p != lp || !rotEq(r, lr))
                {
                    //Debug.Log($"{gameId} found {n} tile moved locally");
                    lastKnownPos[n] = p;
                    lastKnownRot[n] = r;
                    lastMoved[n] = Time.time;

                    rt.SetBackColorOffset(new Color(0.5f, 0.1f, 0.5f));

                    UpdateInternalTileState(n);
                }
            }
        }
    }

    void CheckMovedTenbou()
    {
        var n = tenbouCursor++;
        tenbouCursor %= 68;

        var rb = tenbouRigidbodies[n];
        if (!rb.isKinematic)
        {
            var t = tenbou[n];
            var p = t.localPosition;
            var r = t.localRotation;
            var lp = tenbouLastKnownPos[n];
            var lr = tenbouLastKnownRot[n];

            if (p != lp || !rotEq(r, lr))
            {
                tenbouLastKnownPos[n] = p;
                tenbouLastKnownRot[n] = r;
                tenbouLastMoved[n] = Time.time;
                // no internal tile state here, don't think it's worth the bookkeeping
                // since we're only doing ARBITRARY
            }
        }
    }

    void BroadcastTilesAndTenbou()
    {
        var buf = bus.sendBuffer;

        int n = headerSize; 
        var limit = maxDataByteSize - 1; // 1 byte for EOF

        // XXX kind of nasty ack object; we want to know both the index of the tile and its
        // lastMoved time that we're currently sending, so if a tile is still moving while
        // we're broadcasting, we know which move time we actually broadcast (and whether
        // we still need to broadcast more.
        float[] ackObj = new float[256 * 2];
        int ackI = 0;
        int j = 0;

        for (int i = 0; i < 136; ++i)
        {
            if (lastAcked[i] >= lastMoved[i]) continue;
            var state = tileState[i];
            if (state == DEAL) continue; // skip deal packets, table owner will broadcast those

            var packSize = PACK_SIZE[state];
            // 1 for index
            if (n + 1 + packSize >= limit) break; // not enough room

            ackObj[ackI++] = i;
            ackObj[ackI++] = lastMoved[i];

            buf[n++] = (byte)i;
            //Debug.Log($"{gameId} wrote tile {i} state {state} at pos {n}");
            switch (state)
            {
                case HAND: PackHand(i, n, buf); break;
                case UPRIGHT: PackUpright(i, n, buf); break;
                case TABLE: PackTable(i, n, buf); break;
                case ARBITRARY: PackArbitrary(arbitraryTilePositions[i], arbitraryTileRotations[i], n, buf); break;
            }
            n += packSize;
            j++;
        }

        var k = 0;

        for (int i = 0; i < 68; ++i)
        {
            if (tenbouLastAcked[i] >= tenbouLastMoved[i]) continue;
            var rb = tenbouRigidbodies[i];
            if (rb.isKinematic) continue; // not ours

            // 1 for index, 9 for ARBITRARY
            if (n + 1 + 9 >= limit) break; // not enough room

            var t = tenbou[i];

            ackObj[ackI++] = i + 136;
            ackObj[ackI++] = tenbouLastMoved[i];

            buf[n++] = (byte)(i + 136);
            PackArbitrary(t.localPosition, t.localRotation, n, buf);

            n += 9;
            k++;
        }

        if (j == 0 && k == 0)
        {
            // actually nothing changed so leave bus how it is.
            return;
        }

        var seat = GetSeat();
        WriteHeader(buf, seat, false);

        buf[n] = 255; // EOF in packet
        ackObj[ackI] = 255; // EOF in ack object

        LogInternal($"SEND tiles seat={seat} tiles={j} tenbou={k} bytes={n}");

        bus.sendAckObject = ackObj; 
        bus.sendBufferReady = true;
    }

    void ReadPacket(byte[] packet)
    {
        var header = packet[0];
        var packetGameId = (header >> 6) & 3;
        //Debug.Log($"{gameId} read packet for {packetGameId} {Convert.ToString(header, 2).PadLeft(8, '0')}");

        if (packetGameId != gameId) return;

        var packetSeat = (header >> 4) & 3;
        var packetIsShuffle = (header & 1) == 1;

        // avoid checking local tiles in update if seated until first packet from table
        gameStarted = true;

        if (packetIsShuffle)
        {
            //DebugBytes($"{gameId} got shuffle packet ", packet, 182);
            var shufBitmap = ReadTileBitmap(headerSize, packet);
            var j = 0;
            var n = headerSize + 17;
            for (int i = 0; i < 136; ++i)
            {
                shuffleOrder[i] = packet[n++];
                var rt = riichiTiles[i];
                if (shufBitmap[i])
                {
                    tileState[i] = DEAL;
                    rt.ReleaseCustomOwnership();
                    rt.SetBackColorOffset(new Color(0, 0.1f, 0));
                    MoveLocally(i);
                    j++;
                }
            }
            LogInternal($"RECV shufl seat={packetSeat} hash={shuffleHash()}, tiles={j}");
        } else
        {
            //Debug.Log($"{gameId} got tile packet");
            //DebugBytes($"{gameId} got tile packet ", packet, 182);

            int n = headerSize;
            int idx = packet[n++];
            int j = 0, k = 0;
            while (idx != 255) // EOF
            {
                if (idx < 136)
                {
                    var rt = riichiTiles[idx];

                    // don't yank tiles out of a players hands, but otherwise
                    // assume the remote player took ownership.
                    var remoteTile = !rt.IsHeld();

                    n += ReadTile(idx, n, packet, remoteTile);

                    if (remoteTile)
                    {
                        rt.ReleaseCustomOwnership();
                        MoveLocally(idx);
                    }

                    j++;
                } else
                {
                    // tenbou
                    ReadArbitraryToTransform(tenbou[idx - 136], n, packet);
                    n += 9;
                    k++;
                }
                idx = packet[n++];
            }
            LogInternal($"RECV tiles seat={packetSeat} tiles={j} tenbou={k} bytes={n}");
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
                // not sure why Vector3.down instead of up, but the calculation in
                // UpdateInternalTileState from the right angle seems to be "reversed"
                t.localRotation = Quaternion.AngleAxis(uprightYRot[idx], Vector3.down);
                break;
            case TABLE:
                t.localPosition = tableXZ[idx];
                // same here. I just don't understand quaternions well enough to know why
                // first flip the tile up or down
                var up = tableUp[idx];
                t.localRotation = Quaternion.AngleAxis(up ? 270 : 90, Vector3.right)  *
                    // then rotate around its (local) Z which is world up
                    Quaternion.AngleAxis(tableZRot[idx], up ? Vector3.back : Vector3.forward);
                break;
            case ARBITRARY:
                t.localPosition = arbitraryTilePositions[idx];
                t.localRotation = arbitraryTileRotations[idx];
                break;
        }
        //Debug.Log($"{gameId} tile {idx} moved to remote position");
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
    // [2 bits seat]         
    // [3 bits leftover]
    // [1 bit isShuffle]
    // if isShuffle:
    // [17 bytes bitmap]      tiles that are in deal position
    // [136 bytes]            shuffleOrder
    // else:
    // variable length:
    // [1 byte tile idx]
    // [1] [1 bit up or down] [8 bits euler z] [11 bits x] [11 bits z] = 4 bytes, on table
    // [0] [1] [2 bits seat pos] [4 bits hand pos] = 1 byte, in hand
    // [0] [0] [1] [7 bits euler y] [11 bits x] [11 bits z] = 4 bytes, upright on table
    // [0] [0] [0] [1] [12 bits x] [12 bits y] [12 bits z] [2 bits largest component] [30 bits components] = 9 bytes
    //
    // since tiles will eventually get acked, regular tile states will be pretty short, 1 tile that's currently moving
    // 13 tiles on hand sort.
    //
    // kludge for tenbou sticks:, if the tile idx is 136-208, it's a tenbou index instead and synced as ARBITRARY
    #region bitpacking methods
    int ReadTile(int idx, int n, byte[] buf, bool remoteTile)
    {
        // XXX remoteTile checks to avoid clobbering our own tile state
        // and instead just returns how many bytes we would have read.
        // messy
        var first = buf[n];
        if ((first & 128) > 0) // table
        {
            if (remoteTile)
            {
                tileState[idx] = TABLE;
                ReadTable(idx, n, buf);
            }
            //Debug.Log($"read tile {idx} TABLE at {n}, {print(tableXZ[idx])} {tableZRot[idx]} {tableUp[idx]}");
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
    void WriteHeader(byte[] buf, int seat, bool isShuffle)
    {
        int header = gameId;
        header = (header << 2) + seat;
        // empty gap of 3 bits
        header = (header << 4) + (isShuffle ? 1 : 0);
        buf[0] = (byte)header;
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
 
    void ReadTable(int i, int n, byte[] buf)
    {
        //DebugTablePack(buf, n);
        uint p = ReadInt(n, buf);
        tableUp[i] = ((p >> 30) & 1) > 0;
        tableZRot[i] = UnpackFloat((p >> 22) & 255U, 0, 360, 255);
        var xz = tableXZ[i];
        xz.x = UnpackFloat((p >> 11) & 2047U, -1, 1, 2047);
        xz.z = UnpackFloat(p & 2047U, -1, 1, 2047);
        // XXX somehow the property assignment doesn't apply to array reference, but does to local var?
        tableXZ[i] = xz; 
    }

    void PackTable(int i, int n, byte[] buf)
    {
        uint p = 2U + (tableUp[i] ? 1U : 0U);
        p = (p << 8) + PackFloat(Mathf.Repeat(tableZRot[i], 360), 0, 360, 255);
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
    void ReadArbitraryToTransform(Transform t, int n, byte[] buf)
    {
        // 0001xxxx|xxxxxxxx|yyyyyyyy|yyyyzzzz|zzzzzzz
        uint px = (((uint)buf[n] & 15U) << 8) + (uint)buf[n + 1];
        uint py = ((uint)buf[n+2] << 4) + ((uint)(buf[n + 3] >> 4) & 15U);
        uint pz = (((uint)buf[n+3] & 15U) << 8) + (uint)buf[n + 4];

        t.localPosition = new Vector3(
            UnpackFloat(px, -2, 2, 4095),
            UnpackFloat(py, -0.3f, 3, 4095),
            UnpackFloat(pz, -2, 2, 4095));

        t.localRotation = UnpackQuaternion(buf, n + 5);
    }

    void ReadArbitrary(int i, int n, byte[] buf)
    {
        // 0001xxxx|xxxxxxxx|yyyyyyyy|yyyyzzzz|zzzzzzz
        uint px = (((uint)buf[n] & 15U) << 8) + (uint)buf[n + 1];
        uint py = ((uint)buf[n+2] << 4) + ((uint)(buf[n + 3] >> 4) & 15U);
        uint pz = (((uint)buf[n+3] & 15U) << 8) + (uint)buf[n + 4];

        arbitraryTilePositions[i] = new Vector3(
            UnpackFloat(px, -2, 2, 4095),
            UnpackFloat(py, -0.3f, 3, 4095),
            UnpackFloat(pz, -2, 2, 4095));

        arbitraryTileRotations[i] = UnpackQuaternion(buf, n + 5);
    }

    void PackArbitrary(Vector3 v, Quaternion q, int n, byte[] buf)
    {
        var px = PackFloat(v.x, -2, 2, 4095);
        var py = PackFloat(v.y, -0.3f, 3, 4095);
        var pz = PackFloat(v.z, -2, 2, 4095);
        // 0001xxxx|xxxxxxxx|yyyyyyyy|yyyyzzzz|zzzzzzz
        buf[n++] = (byte)(16 + ((px >> 8) & 15));
        buf[n++] = (byte)(px & 255);
        buf[n++] = (byte)((py >> 4) & 255);
        buf[n++] = (byte)(((py & 15) << 4) + ((pz >> 8) & 15));
        buf[n++] = (byte)(pz & 255);

        PackQuaternion(q, buf, n);
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
        // t.up is normalized local Y (in world coords), so tile is upright when it's mostly 1.
        if (ApproxEq(p.y, uprightY) && t.up.y > 0.9)
        {
            //Debug.Log($"tile {n} moved to UPRIGHT at {p.x} {p.y} {p.z}");
            tileState[n] = UPRIGHT;
            uprightXZ[n].x = p.x;
            uprightXZ[n].z = p.z;

            // I'm not entirely sure this is the best way to get this, but it seems to work okay
            // the Y rotation reconstructed from the xz angle from local +X axis in world coords
            var right = t.right;
            uprightYRot[n] = Mathf.Atan2(right.z, right.x) * Mathf.Rad2Deg;
        }
        // t.forward is straight up (or down) for on table tiles
        else if (ApproxEq(p.y, tableY) && Mathf.Abs(t.forward.y) > 0.9)
        {

            tileState[n] = TABLE;
            tableXZ[n].x = p.x;
            tableXZ[n].z = p.z;
            tableUp[n] = t.forward.y > 0;

            // same here; get the world Y rotation from the +X axis, which is the local Z rotation
            var right = t.right;
            tableZRot[n] = Mathf.Atan2(right.z, right.x) * Mathf.Rad2Deg;

            //Debug.Log($"tile {n} moved to TABLE at {p.x} {p.y} {p.z} {e.y} ({e}) isUp = {tableUp[n]}");
        }
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
        LogInternal($"local resync, invalidating all tiles");
        for (int i = 0; i < 136; ++i)
        {
            var rt = riichiTiles[i];
            if (rt.IsCustomOwnedAndNotInDealPosition())
            {
                lastMoved[i] = Time.time;
                UpdateInternalTileState(i);
                riichiTiles[i].SetBackColorOffset(new Color(0.5f, 0.1f, 0.5f));
            }
        }
        for (int i = 0; i < 68; ++i)
        {
            var rb = tenbouRigidbodies[i];
            if (!rb.isKinematic)
            {
                tenbouLastMoved[i] = Time.time;
            }
        }
        // for owner, also send shuffle state immediately
        if (IsTableOwner())
        {
            needShufflePacket = true;
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
            lastMoved[i] = Time.time;
        }

        needShufflePacket = true;
        LogInternal($"shuffled to hash {shuffleHash()}");
    }

    // XXX another kludge to avoid excessive disables
    private bool lastKnownTilesEnabled = false;

    public void EnableTiles()
    {
        LogInternal("enabling tile/tenbou pickup");
        for (int j = 0; j < 136; ++j)
        {
            tileBoxColliders[j].enabled = true;
        }
        for (int j = 0; j < 68; ++j)
        {
            ((VRC_Pickup)tenbouVrcPickups[j]).pickupable = true;
        }

        lastKnownTilesEnabled = true;
    }

    public void DisableTiles()
    {
        if (!lastKnownTilesEnabled) return;
        LogInternal("disabling tile/tenbou pickup");
        for (int j = 0; j < 136; ++j)
        {
            var rt = riichiTiles[j]; 
            rt.ReleaseCustomOwnership();
            tileBoxColliders[j].enabled = false;
        }
        for (int j = 0; j < 68; ++j)
        {
            ((VRC_Pickup)tenbouVrcPickups[j]).pickupable = false;
            tenbouRigidbodies[j].isKinematic = true;
        }
        lastKnownTilesEnabled = false;
    }

    void ToggleTenbouPhysics(int seat)
    {
        var rs = seats[seat];
        var zone = rs.tenbouZone;
        var t = zone.transform;
        var halfExtents = zone.size / 2;
        var collide = Physics.OverlapBox(t.position, halfExtents, t.rotation, tenbouLayer);
        var len = collide.Length;
        for (int i = 0; i < len; ++i)
        {
            var o = collide[i].gameObject;
            // linear scan, oh well
            for (int j = 0; j < 68; ++j)
            {
                if (tenbou[j].gameObject == o)
                {
                    var rb = tenbouRigidbodies[j];
                    if (rb.isKinematic)
                    {
                        rb.isKinematic = false;
                    }
                    break;
                }
            }
        }
    }

    public void SortHand(int seat)
    {
        // XXX odd place to do it, not sure yet where to put
        EnableTiles();

        // also kludge, enable "our" scoring sticks, could also sort scoring
        // sticks but probably will get messy.
        ToggleTenbouPhysics(seat);

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
                    lastKnownPos[j] = tile.localPosition;
                    lastKnownRot[j] = tile.localRotation;
                    // but transmit
                    lastMoved[j] = Time.time;
                    break;
                }
            }
        }
        LogInternal($"sorted {collide.Length} hand tiles");
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
