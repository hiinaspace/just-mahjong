
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// on interact, take ownership of tiles in hand area and sort them.
// also, this gameobject becomes owned by the player, so the player
// can start broadcasting out their local tile positions to others through
// the synced variables.
public class OwnAndSortTiles : UdonSharpBehaviour
{
    public Transform tileParent;

    Transform[] tiles;

    public Transform origin;
    public Vector3 halfExtents;
    public LayerMask tileLayer;

    // so you can have direct references to other UdonSharpBehaviors and read their
    // public variables.
    public Shuffle shuffler;

    // so we can check in this canonical order whether we're the first and
    // canonical owner, in the case of multiple of these owned by the same player
    public GameObject[] allOwnAndSortTiles;

    // XXX getting some really weird bug where an instance variable won't be updated
    // until a couple seconds after it's set; really really strange. I suspect it might
    // be because of the number of instance variables on here, so going to try to pack
    // some ints into a mega array variable. Gross, but maybe will fix.
    private int[] instanceInts = new int[32];
    private const int SELF_IDX = 0;
    //private int selfIdx;
    private const int SYNC_STATE_SIZE = 1;
    //private int syncStateSize = 0;
    private const int SEQ_NO = 2;
    //private int seqNo = 0;
    private const int NEXT_FRAG_TO_SEND = 3;
    //private int nextFragToSend;
    private const int LAST_SEQ_READ = 4;
    //private int lastSeqRead;
    private const int SEND_BUFFER_SIZE = 5;
    //private int[] sendBufferSize = new int[1]; // XXX testing

    private float lastWrite;
    private const float updateSpeed = 1f;// 0.2f;

    // oversize buffer for largest possible variable-length state
    private byte[] syncState = new byte[2048];

    private const int maxSyncedStringLen = 10;//105;
    // max ascii chars in the two strings - 5 for the header
    private const int maxFragSizeAscii = maxSyncedStringLen * 2 - 5;

    [UdonSynced] string syncState0 = "";
    [UdonSynced] string syncState1 = "";

    // table surface at 0.6, + half of tile z thickness
    private const float onTableY = 0.6f + 0.032f / 2f;
    // half of y height
    private const float inHandY = 0.6f + 0.05f / 2f;
    private float originInHandY;

    private const int inHandLimit = 16;
    private Vector3[] inHandPositions;

    private char[] sendBuffer = new char[2048];

    private char[] fragBuffer = new char[2048];
    private bool[] fragState = new bool[1];

    // XXX might be udonsharp bug, but variables not declared before all methods seem to turn into local vars

    void Start()
    {
        // references to the tiles in order
        tiles = new Transform[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tileParent.GetChild(i);
        }
        
        // kind of silly, dunno better way yet
        instanceInts[SELF_IDX] = -1;
        for (int i = 0; i < 4; ++i)
        {
            if (allOwnAndSortTiles[i] == gameObject)
            {
                instanceInts[SELF_IDX] = i;
            }
        }
        //Debug.Log($"{gameObject} is at idx {selfIdx} = {allOwnAndSortTiles[selfIdx]}");

        // XXX these aren't great hand positions, trying to reverse engineer the old lazy ones I made
        // could be done better, and actually matching the tile placements for deal.
        inHandPositions = new Vector3[inHandLimit];
        originInHandY = origin.InverseTransformPoint(new Vector3(0, inHandY, 0)).y;
        float z = -7.5f;
        for (int i = 0; i < inHandLimit; ++i)
        {
            inHandPositions[i] = origin.TransformPoint(new Vector3(0, originInHandY, z * 0.038f));
            //Debug.Log($"{gameObject.name} handPos[{i}] = {inHandPositions[i]}");
            z += 1;
        }
        // wat
        instanceInts[SEND_BUFFER_SIZE] = -1;
        instanceInts[NEXT_FRAG_TO_SEND] = -1;
    }

    bool floatEq(float a, float b)
    {
        return Mathf.Abs(a - b) < 0.005; // close enough
    }

    void Update()
    {
        if (Networking.IsOwner(gameObject)) // if we own this
        {
            lastWrite += Time.deltaTime;
            if (lastWrite > updateSpeed)
            {
                lastWrite = 0;
                bool updated = UpdateTileState();
                Debug.Log("in new update() loop");

                Debug.Log($"in update() loop, on frag {instanceInts[NEXT_FRAG_TO_SEND]}, sendBufsize {instanceInts[SEND_BUFFER_SIZE]}");
                // if we need to cycle through our fragments
                // or update any part if packet header + syncstate
                // exceeds packet size
                // TODO updated is always true for now anyway
                //if ((1 + syncStateSize) > maxFragSize || updated)
                //{
                //}
                Debug.Log("begin Serialize");
                Serialize(updated);
                Debug.Log("end Serialize");
                // testing
                Debug.Log("begin Deseralize");
                if (Deserialize())
                {
                    Debug.Log("end Deseralize, do UpdateLocalTiles");
                    UpdateLocalTiles();
                }
                Debug.Log($"end update() loop, on frag {instanceInts[NEXT_FRAG_TO_SEND]}, sendBufsize {instanceInts[SEND_BUFFER_SIZE]}");
            }
        } else
        {
            // on other client, try deserialize
            if (Deserialize())
            {
                UpdateLocalTiles();
            }
        }
    }

    bool isUp(Quaternion t)
    {
        // probably better way to do this
        var x = t.eulerAngles.x;
        //  -90,  270
        return floatEq(x, -90) || floatEq(x, 270);
    }
    bool isDown(Quaternion t)
    {
        // probably better way to do this
        var x = t.eulerAngles.x;
        //  -270,  90
        return floatEq(x, -270) || floatEq(x, 90);
    }

    bool isFlatOnTable(Transform t)
    {
        return floatEq(t.position.y, onTableY) && (isUp(t.rotation) || isDown(t.rotation));
    }
    int isInHand(Transform t)
    {
        if (!floatEq(t.position.y, inHandY))
        {
            //Debug.Log($"tile {t.gameObject.name} not in hand, {t.position.y} != {inHandY}");
            return -1;
        }
        var x = t.position.x;
        var z = t.position.z;
        for (int i = 0; i < inHandLimit; i++)
        {
            Vector3 v = inHandPositions[i];
            if (floatEq(x, v.x) & floatEq(z, v.z))
            {
                return i;
            }
        }
        //Debug.Log($"tile {t.gameObject.name} couldn't find {t.position.x} {t.position.z} in hand");
        return -1;
    }

    // returns if state was modified
    bool UpdateTileState()
    {
        // if we're the _first_ in the list that has this particular owner
        // then take priority and do the update. Otherwise, don't check individual tiles and write an empty state;
        // the other, first OwnAndSort tile will do it.
        // prevents the case where 1) all the OwnAndSortTiles are owned by master initially
        // or 2) a player goes and interacts with all of them.
        if (!isFirstOwner()) return false;
        bool changed = true; // TODO actually check for last state of tile. for now assume changed.
        int n = 0;
        for (int i = 0; i < 136; ++i)
        {
            var t = tiles[i];
            if (Networking.IsOwner(t.gameObject))
            {
                // if we're owner of the tile, we should sync it;
                // except: if we're also master, and the tile is still in the Shuffle bitmap.
                // this is tricky; we don't need to duplicate the Shuffle state here. 
                // if we're owner and we're _not_ master, then it's safe to proceed
                // if we're owner and also master, then
                //   if this tile isn't in Shuffle.bitmap,
                //      this tile is safe to proceed
                //   otherwise, it's still controlled by the shuffler, so skip it.
                // when we (the master) move the tile locally, by either hitting the button to 
                // sort tiles or grabbing it, the Shuffler will (locally) remove it from the bitmap,
                // and this function will pick up that change, and start adding it to this state.
                if (Networking.IsMaster && shuffler.dealtTiles[i])
                {
                    continue;
                }

                // if tile is in hand position from sorting
                int inHandPos = isInHand(t);
                if (inHandPos >= 0)
                {

                    // [0] [0] [1] [5 bytes hand pos] = 1 byte, in hand
                    syncState[n++] = (byte)((0b001 << 5) + inHandPos);
                    //Debug.Log($"{t.gameObject.name} is owned by {gameObject.name}, in hand at {inHandPos}");

                } else if (isFlatOnTable(t))
                {
                    // [1] [1 bit up or down] [8 bits euler y] [11 bits x] [11 bits z] = 4 bytes, on table

                    var r = t.rotation;
                    var y = r.eulerAngles.y;
                    var x = t.position.x;
                    var z = t.position.z;

                    var py = PackYRot(y);
                    var px = PackPosComponent(x);
                    var pz = PackPosComponent(z);

                    //Debug.Log($"{y} {py} {UnpackYRot(py)}");
                    //Debug.Log($"{x} {px} {UnpackPosComponent(px)}");
                    //Debug.Log($"{z} {pz} {UnpackPosComponent(pz)}");

                    uint pack = 1;
                    pack = (pack << 1) + (uint)(isUp(t.rotation) ? 1 : 0);
                    pack = (pack << 8) + py;
                    pack = (pack << 11) + px;
                    pack = (pack << 11) + pz;
                    syncState[n++] = (byte)((pack >> 24) & 255);
                    syncState[n++] = (byte)((pack >> 16) & 255);
                    syncState[n++] = (byte)((pack >> 8)  & 255);
                    syncState[n++] = (byte)(pack         & 255);
                    //Debug.Log($"{t.gameObject.name} is owned by {gameObject.name}, flat on table at {t.position}");
                    //DebugTablePack(syncState, n - 4);
                } else
                {
                    // [[0] [1] [13 bits x] [12 bits y] [13 bits z]] [[2 bits largest component] [30 bits components]] = 9 bytes
                    var px = PackBigPosComponent(t.position.x); // 13 bits
                    var py = PackMedPosComponent(t.position.y); // 12 bits
                    var pz = PackBigPosComponent(t.position.z); // 13
                    //DebugUnpack($"x {t.position.x} {UnpackBigPosComponent(px)}", px, 13);
                    //DebugUnpack($"y {t.position.y} {UnpackMedPosComponent(py)}", py, 12);
                    //DebugUnpack($"z {t.position.z} {UnpackBigPosComponent(pz)}", pz, 13);
                    syncState[n++] = (byte)((0b01 << 6) + ((px >> 7) & 63)); // last 6 bits
                    syncState[n++] = (byte)(((px & 127) << 1) + ((py >> 11) & 1)); // first 7 bits, last 1 bit
                    // 0|000 0000 0|000
                    syncState[n++] = (byte)((py >> 3) & 255); // middle 8 bits
                    // 0000 0|000 0000 0
                    syncState[n++] = (byte)(((py & 7) << 5) + ((pz >> 8) & 31)); // first 3 bits, last 5 bits
                    syncState[n++] = (byte)(pz & 255); // first 8 bits

                    packQuaternion(t.rotation, syncState, n);
                    n += 4;
                    //Debug.Log($"{t.gameObject.name} is owned by {gameObject.name}, arbitrary at {t.position} {t.rotation}");
                    //DebugFullPack(syncState, n - 9);
                }
            }
        }
        instanceInts[SYNC_STATE_SIZE] = n;
        return changed;
    }

    string toBin(byte b)
    {
        var c = Convert.ToString(b, 2);
        return c.PadLeft(8, '0');
    }

    /*
    void DebugFullPack(byte[] bytes, int i)
    {
        var c = toBin(bytes[i]);
        c += "|" + toBin(bytes[i + 1]);
        c += "|" + toBin(bytes[i + 2]);
        c += "|" + toBin(bytes[i + 3]);
        c += "|" + toBin(bytes[i + 4]);
        c += "|" + toBin(bytes[i + 5]);
        c += "|" + toBin(bytes[i + 6]);
        c += "|" + toBin(bytes[i + 7]);
        c += "|" + toBin(bytes[i + 8]);
        Debug.Log($"full pack: {c}");
    }
    void DebugTablePack(byte[] bytes, int i)
    {
        var c = toBin(bytes[i]);
        c += "|" + toBin(bytes[i + 1]);
        c += "|" + toBin(bytes[i + 2]);
        c += "|" + toBin(bytes[i + 3]);
        Debug.Log($"table pack: {c}");
    }
    */
    uint PackYRot(float f)
    {
        // [0, 360] to [0, 255]
        f = Mathf.Repeat(f, 360); // aka modulus
        var r = Convert.ToUInt32(Mathf.Floor((float)(f / 360 * 255 + 0.5)));
        return r;
    }
    float UnpackYRot(long i)
    {
        return i / 255f * 360f;
    }
    uint PackPosComponent(float f)
    {
        // [-1, 1] to [0, 2047]
        f = Mathf.Clamp(f, -1, 1);
        double s = (0.5 * (f + 1) * 2047 + 0.5);
        var r = Convert.ToUInt32(Mathf.Floor((float)s));
        return r;
    }
    float UnpackPosComponent(long i)
    {
        return (float)((i - 1024) / 2047.0 * 2.0);
    }
    uint PackBigPosComponent(float f)
    {
        // [-2, 2] to [0, 8191], 13 bits
        f = Mathf.Clamp(f, -2, 2);
        double s = (0.5 * (f / 2 + 1) * 8191 + 0.5);
        var r = Convert.ToUInt32(Mathf.Floor((float)s));
        return r;
    }
    float UnpackBigPosComponent(long i)
    {
        return (i - 4096) * (1.0f / 8191) * 4f;
    }
    uint PackMedPosComponent(float f)
    {
        // [0, 3] to [0, 4095], 12 bits
        f = Mathf.Clamp(f, 0, 3);
        double s = (f / 3 * 4095 + 0.5);
        var r = Convert.ToUInt32(Mathf.Floor((float)s));
        return r;
    }
    float UnpackMedPosComponent(long i)
    {
        return (float)(i * (1.0 / 4095) * 3);
    }
    void packQuaternion(Quaternion q, byte[] array, int idx)
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

        array[idx]   = (byte)((pack >> 24) & 255);
        array[idx+1] = (byte)((pack >> 16) & 255);
        array[idx+2] = (byte)((pack >> 8)  & 255);
        array[idx+3] = (byte)(pack         & 255);
    }
    Quaternion unpackQuaternion(byte[] array, int idx)
    {
        uint pack =          Convert.ToUInt32(array[idx]);
        pack = (pack << 8) + Convert.ToUInt32(array[idx+1]);
        pack = (pack << 8) + Convert.ToUInt32(array[idx+2]);
        pack = (pack << 8) + Convert.ToUInt32(array[idx+3]);

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

    bool isFirstOwner()
    {
        // if we're the _first_ in the list that has this particular owner
        // then take priority and do the update. Otherwise, don't check individual tiles and write an empty state;
        // the other, first OwnAndSort tile will do it.
        // prevents the case where 1) all the OwnAndSortTiles are owned by master initially
        // or 2) a player goes and interacts with all of them.
        var owner = Networking.GetOwner(gameObject);
        for (int i = 0; i < instanceInts[SELF_IDX]; ++i)
        {
            var o = Networking.GetOwner(allOwnAndSortTiles[i]);
            if (o == owner) return false;
        }
        return true;
    }

    void UpdateLocalTiles()
    {
        if (!isFirstOwner()) return;
        var owner = Networking.GetOwner(gameObject); // owner of this OwnAndSortTiles thing
        int n = 0;
        for (int i = 0; i < 136; ++i)
        {
            var t = tiles[i];
            // if the tile is owned (and thus controlled) by this syncState
            // TODO but what if GetOwner sync diverges from the state? could have misindexed tiles.
            // might need 17 bytes of a bitmap after all.
            if (Networking.GetOwner(t.gameObject) == owner)
            {
                // except: if the tile is still in the Shuffle bitmap
                // it'll still be owned by master in that case, who could also own this
                // confusing to think about.
                if (shuffler.dealtTiles[i])
                {
                    continue;
                }

                var isTable = ((syncState[n] >> 7) & 1) == 1;
                var isFull = ((syncState[n] >> 6) & 1) == 1;
                if (isTable)
                {
                    // [1] [1 bit up or down] [8 bits euler y] [11 bits x] [11 bits z] = 4 bytes, on table
                    uint pack = syncState[n];
                    pack = (pack << 8) + syncState[n + 1];
                    pack = (pack << 8) + syncState[n + 2];
                    pack = (pack << 8) + syncState[n + 3];

                    var isUp = ((pack >> 30) & 1) == 1;

                    var y = (pack >> 22) & 255;
                    //DebugUnpack("y", y, 8);
                    var yRot = UnpackYRot(y);

                    var x = (pack >> 11) & 2047;
                    //DebugUnpack("x", x, 11);
                    var xPos = UnpackPosComponent(x);

                    var z = pack & 2047;
                    //DebugUnpack("z", z, 11);
                    var zPos = UnpackPosComponent(z);

                    t.position = new Vector3(xPos, onTableY, zPos);
                    t.rotation = Quaternion.Euler(isUp ? 270 : 90, yRot, 0);

                    //Debug.Log($"found {t.gameObject.name} , on table at {xPos} {zPos}, yrot {yRot}, isUp {isUp}");
                    n += 4;
                } else if (isFull)
                {
                    // [0] [1] [13 bits x] [12 bits y] [13 bits z] [2 bits largest component] [30 bits components] = 9 bytes
                    // 01 [6 bits x] | [7 bits x] [1 bit y] | [8 bits y] | [3 bits y] | [5 bits z] | [8 bits z] // quaternion stuff;
                    uint px = (uint)(syncState[n] & 63);
                    //DebugUnpack("x", px, 13);
                    px = (uint)(px << 7) + (uint)(((uint)syncState[n+1] >> 1) & 127);
                    //DebugUnpack("x", px, 13);

                    uint py = (uint)(syncState[n + 1] & 1);
                    //DebugUnpack("y", py, 12);
                    py = (py << 8) + (uint)(syncState[n + 2]);
                    //DebugUnpack("y", py, 12);
                    py = (py << 3) + (uint)((uint)(syncState[n + 3] >> 5) & 7);
                    //DebugUnpack("y", py, 12);

                    uint pz = (uint)(syncState[n + 3] & 31);
                    //DebugUnpack("z", pz, 13);
                    pz = (pz << 8) + (syncState[n + 4]);
                    //DebugUnpack("z", pz, 13);

                    float x = UnpackBigPosComponent(px);
                    float y = UnpackMedPosComponent(py);
                    float z = UnpackBigPosComponent(pz);

                    t.position = new Vector3(x, y, z);
                    var q = unpackQuaternion(syncState, n + 5);
                    t.rotation = q;

                    //Debug.Log($"found {t.gameObject.name} , full position {x} {y} {z}");
                    n += 9;
                } else
                {
                    // [0] [0] [1] [5 bytes hand pos] = 1 byte, in hand
                    var p = inHandPositions[syncState[n] & 31];
                    t.position = p;
                    // I think this is right for local?
                    t.rotation = Quaternion.Inverse(origin.rotation) * Quaternion.Euler(0, 90, 0);

                    n += 1;
                }

            }
        }
    }

    /*
    void DebugUnpack(string s, long thing, int bits)
    {
        Debug.Log($"{s} {Convert.ToString(thing, 2).PadLeft(bits, '0')}");
    }
    */

    // `actuallyUpdated` if syncState changed since last seqNo
    void Serialize(bool actuallyUpdated)
    {
        // if we have fragmentation currently, finish the current cycle
        // before restting again; since the actuallyUpdated is always true for now;
        // otherwise it totally starves the receiving end
        // TODO make actuallyUpdated work
        // XXX currentFragCnt keeps getting reset somehow, as does nextfragto send
        var currentFragCnt = instanceInts[SEND_BUFFER_SIZE]/ maxFragSizeAscii + 1;
        Debug.Log($"currentFragCnt {currentFragCnt}, on frag {instanceInts[NEXT_FRAG_TO_SEND]}, sendBufferSize: {instanceInts[SEND_BUFFER_SIZE]}");
        if (currentFragCnt > 1 && instanceInts[NEXT_FRAG_TO_SEND] != 0)
        {
            // need to send next frag, don't update 
            Debug.Log($"Still sending {instanceInts[SEQ_NO]}, on frag {instanceInts[NEXT_FRAG_TO_SEND]}");
        } else
        {
            // need to cycle
            instanceInts[SEQ_NO] = (instanceInts[SEQ_NO] + 1) % 127;
            // and reset frags
            instanceInts[NEXT_FRAG_TO_SEND] = 0;

            // actually dump the entire state into the buffer
            int n = 0;
            for (int i = 0; i < instanceInts[SYNC_STATE_SIZE];)
            {
                // pack 7 bytes into 56 bits;
                ulong pack =         syncState[i++];
                pack = (pack << 8) + syncState[i++];
                pack = (pack << 8) + syncState[i++];

                pack = (pack << 8) + syncState[i++];
                pack = (pack << 8) + syncState[i++];
                pack = (pack << 8) + syncState[i++];
                pack = (pack << 8) + syncState[i++];
                //DebugLong("packed: ", pack);

                // unpack into 8 7bit asciis
                sendBuffer[n++] = (char)((pack >> 49) & (ulong)127);
                sendBuffer[n++] = (char)((pack >> 42) & (ulong)127);
                sendBuffer[n++] = (char)((pack >> 35) & (ulong)127);
                sendBuffer[n++] = (char)((pack >> 28) & (ulong)127);

                sendBuffer[n++] = (char)((pack >> 21) & (ulong)127);
                sendBuffer[n++] = (char)((pack >> 14) & (ulong)127);
                sendBuffer[n++] = (char)((pack >> 7)  & (ulong)127);
                sendBuffer[n++] = (char)(pack         & (ulong)127);
                //DebugChars("chars: ", sendBuffer, n - 8);
            }
            instanceInts[SEND_BUFFER_SIZE] = n;

            currentFragCnt = instanceInts[SEND_BUFFER_SIZE] / maxFragSizeAscii + 1;

            Debug.Log($"actually updated, dumped state for seq {instanceInts[SEQ_NO]} in {currentFragCnt} frags, {instanceInts[SYNC_STATE_SIZE]} bytes, {n} chars");
        }

        char[] chars = new char[maxFragSizeAscii + 5];

        // header
        chars[0] = Convert.ToChar(instanceInts[SEQ_NO]);
        chars[1] = Convert.ToChar(instanceInts[NEXT_FRAG_TO_SEND]);
        chars[2] = Convert.ToChar(currentFragCnt);
        chars[3] = Convert.ToChar((instanceInts[SYNC_STATE_SIZE] >> 7) & 127);
        chars[4] = Convert.ToChar(instanceInts[SYNC_STATE_SIZE] & 127);

        // XXX not in udon
        //Array.Copy(sendBuffer, lastFragSent * maxFragSize, chars, 5, maxFragSize);
        int m = instanceInts[NEXT_FRAG_TO_SEND] * maxFragSizeAscii; // start offset
        for (int i = 5; i < chars.Length; ++i)
        {
            chars[i] = sendBuffer[m++];
        }

        // always do max length strings
        var s = new string(chars);
        syncState0 = s.Substring(0, maxSyncedStringLen);
        syncState1 = s.Substring(maxSyncedStringLen, maxSyncedStringLen);

        Debug.Log($"write frag {instanceInts[NEXT_FRAG_TO_SEND]} of {currentFragCnt}, total {instanceInts[SYNC_STATE_SIZE]} bytes to syncState0,1");

        // increment, so we'll cycle through them next time
        instanceInts[NEXT_FRAG_TO_SEND] = (instanceInts[NEXT_FRAG_TO_SEND] + 1) % currentFragCnt;
        Debug.Log($"next frag to send is {instanceInts[NEXT_FRAG_TO_SEND]}, currentFragCnt is {currentFragCnt}");
    }
    bool Deserialize()
    {
        // on client
        // read first byte, check seqNo and fragment;
        // if we've received new seqNo and read all the fragments, then true
        // read fragment from syncState{0,1} into local syncState

        // else if fragmented, then read this chunk into the fragment buffer. If
        // our fragment buffer has all the fragments for this seq, _then_ emit true.
        // if we start getting fragments for a different seq, then abandon all the current
        // fragments.

        // nothing happened yet
        if (syncState0.Length == 0) return false;

        // XXX udon can't do string[idx] yet
        // just plain asciis to avoid unpacking
        var header = syncState0.Substring(0, 5).ToCharArray();
        var seq = Convert.ToInt32(header[0]);
        var frag = Convert.ToInt32(header[1]);
        var fragCnt = Convert.ToInt32(header[2]);

        var len = Convert.ToInt32(header[3]);
        len = (len << 7) + Convert.ToInt32(header[4]);

        Debug.Log($"read seq {seq} frag {frag} fragcnt {fragCnt} len {len}");

        var offset = 0;
        if (fragCnt > 1)
        {
            // reading a fragmented packet.
            if (seq != instanceInts[LAST_SEQ_READ])
            {
                // whole new packet
                // TODO could starve clients if never receive full packet before actuallyUpdated triggers
                instanceInts[LAST_SEQ_READ] = seq;
                Debug.Log($"whole new packet, resetting fragState");
                fragState = new bool[fragCnt];
            }

            // we now have this fragment
            fragState[frag] = true;
            Debug.Log($"got frag {frag} of seq {seq}");

            // skip forward in array
            offset += maxFragSizeAscii * frag;

            // copy into buffer
            // XXX skip header with substring
            syncState0.Substring(5).ToCharArray().CopyTo(fragBuffer, offset);
            syncState1.ToCharArray().CopyTo(fragBuffer, offset + 100);

            // wait until all fragments are in
            for (int i = 0; i < fragCnt; ++i)
            {
                Debug.Log($"fragState[{i}] = {fragState[i]}");
                if (!fragState[i]) return false;
            }
        } else
        {
            if (seq == instanceInts[LAST_SEQ_READ])
            {
                // we already saw this
                return false;
            }

            instanceInts[LAST_SEQ_READ] = seq;
            Debug.Log($"have only {fragCnt} frag, copying directly");
            syncState0.Substring(5).ToCharArray().CopyTo(fragBuffer, offset);
            syncState1.ToCharArray().CopyTo(fragBuffer, offset + 100);
        }

        Debug.Log($"reading whole fragBuffer in, {len} bytes");
        // okay, now fragBuffer is complete, (either full packet, or all fragments in)
        // has no header prepended.
        int n = 0; 
        for (int i = 0; i < len;)
        {
            //DebugChars("deser: ", fragBuffer, n);
            // pack 8 asciis into 56 bits;
            ulong pack =         fragBuffer[n++];
            pack = (pack << 7) + fragBuffer[n++];
            pack = (pack << 7) + fragBuffer[n++];
            pack = (pack << 7) + fragBuffer[n++];
            
            pack = (pack << 7) + fragBuffer[n++];
            pack = (pack << 7) + fragBuffer[n++];
            pack = (pack << 7) + fragBuffer[n++];
            pack = (pack << 7) + fragBuffer[n++];
            //DebugLong("unpacked: ", pack);

            // unpack into 7 bytes
            syncState[i++] = (byte)((pack >> 48) & (ulong)255);
            syncState[i++] = (byte)((pack >> 40) & (ulong)255);
            syncState[i++] = (byte)((pack >> 32) & (ulong)255);
            syncState[i++] = (byte)((pack >> 24) & (ulong)255);

            syncState[i++] = (byte)((pack >> 16) & (ulong)255);
            syncState[i++] = (byte)((pack >> 8)  & (ulong)255);
            syncState[i++] = (byte)((pack >> 0)  & (ulong)255);
        }
        //DebugBytes("after: ", syncState, len);

        return true;
    }

    public override void Interact()
    {
        // own this
        Networking.SetOwner(Networking.LocalPlayer, gameObject);

        var tiles = Physics.OverlapBox(origin.position, halfExtents, origin.rotation, tileLayer);
        //Debug.Log($"tiles: {tiles.Length}");
        Sort(tiles);
        float z = -7.5f;
        int n = 0;
        foreach (Collider t in tiles)
        {
            var obj = t.gameObject;
            Debug.Log($"tile {obj.name}");
            Networking.SetOwner(Networking.LocalPlayer, obj);

            obj.transform.position = inHandPositions[n++];
            obj.transform.rotation = origin.rotation * Quaternion.Euler(0, 90, 0);

            var r = obj.GetComponent<Rigidbody>();
            //r.isKinematic = false; // allow movement locally

            z += 1f;
            if (n >= inHandLimit) break;
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
                // gameobject names are sorted by tile value and suit
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
    
    /*
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

    void DebugBytes(string s, byte[] bytes, int len)
    {
        for (int i = 0; i < len; ++i)
        {
            s += $"{Convert.ToInt32(bytes[i]):D3}|";
        }
        Debug.Log(s);
    }
    */

}
