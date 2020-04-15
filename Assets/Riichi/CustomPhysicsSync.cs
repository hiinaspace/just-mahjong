
using Amazon.CognitoIdentity.Model;
using System;
using UdonSharp;
using UnityEngine;
using UnityEngine.Analytics;
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

    private Component[] tiles;
    private float lastSync = 0;

    public int tilesPerStripe = 4;
    private int stripe;

    private UInt16 seqNo = 0;

    // master will have full control now.
    [UdonSynced] public string state;
    // let's try this
    [UdonSynced] public string state1;
    [UdonSynced] public string state2;
    [UdonSynced] public string state3;

    void Start()
    {
        // GetComponentsInChildren includes "this", annoying
        var tilesAndThis = transform.GetComponentsInChildren(typeof(UdonBehaviour));
        tiles = new Component[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tilesAndThis[i + 1];
        }
        //Debug.Log($"my player id: {VRCPlayerApi.GetPlayerId(Networking.LocalPlayer)}");
    }

    void FixedUpdate()
    {
        lastSync += Time.deltaTime;
        if (lastSync > syncIntervalSecs)
        {
            lastSync = 0;
            if (Networking.IsMaster)
            {
                // I think sending an event makes the assembly for FixedUpdate more efficient
                // compared to actually calling the method. dunno for sure
                SendCustomEvent("DoSync");
            } else
            {
                SendCustomEvent("ReadSync");
            }
        }
    }

    public void DoSync()
    {
        // 1 char idx into tiles array, 3 chars position, 2 chars rotation = 7 chars each tile;
        // total of 136 * 7 = 952 chars of state.
        // synced string has less than 56 chars, maybe it's 32?
        // you can stripe across a single synced string to a degree; unknown how many synced strings
        // are possible
        state = new string(writePacket());
        state1 = new string(writePacket());
        state2 = new string(writePacket());
        state3 = new string(writePacket());
    }
    char[] writePacket()
    {
        // add a seqNo to the beginning, so 29 chars;
        char[] pack = new char[29];
        pack[0] = Convert.ToChar(seqNo++); // dumb work avoiding thing
        int n = 1;
        for (int j = 0; j < tilesPerStripe; ++j)
        {
            pack[n] = Convert.ToChar(stripe);
            n += 1;
            packPosition(tiles[stripe].transform.position, pack, n);
            n += 3;
            packQuaternion(tiles[stripe].transform.rotation, pack, n);
            n += 2;
            // wrap around if we're done
            stripe = (stripe + 1) % 136;
        }
        return pack;
    }

    public void ReadSync()
    {
        readPacket(state);
        readPacket(state1);
        readPacket(state2);
        readPacket(state3);
    }

    void readPacket(string pkt)
    {
        if (pkt.Length == 0) return;
        char[] pack = pkt.ToCharArray();
        char seq = pack[0];
        if (seqNo == seq)
        {
            // already read it
            Debug.Log($"already read seq {seqNo}");
        }
        int n = 1;
        for (int j = 0; j < tilesPerStripe; ++j)
        {
            int i = Convert.ToInt32(pack[n]);
            n += 1;
            var p = unpackPos(pack, n);
            n += 3;
            var r = unpackQuaternion(pack, n);
            n += 2;
            tiles[i].transform.position = p;
            tiles[i].transform.rotation = r;
        }
        seqNo = seq; // ok we read it
    }

    string Pack(Transform t)
    {
        char[] bytes = new char[64];
        byte a = (byte)(0 ^ 0xff & 0xaa << 3);
        Convert.ToChar(a);
        var thing = Convert.ToInt32(bytes[0]);
        return new string(bytes);
    }

    void packPosition(Vector3 p, char[] array, int idx)
    {
        // shorts maybe overkill, oh well
        var x = PackPosComponent(p.x);
        var y = PackPosComponent(p.y);
        var z = PackPosComponent(p.z);
        array[idx] = Convert.ToChar(x);
        array[idx+1] = Convert.ToChar(y);
        array[idx+2] = Convert.ToChar(z);
    }
    Vector3 unpackPos(char[] array, int idx)
    {
        var ox = (uint)(array[idx]);
        var oy = (uint)(array[idx + 1]);
        var oz = (uint)(array[idx + 2]);
        var x = UnpackPosComponent(ox);
        var y = UnpackPosComponent(oy);
        var z = UnpackPosComponent(oz);
        return new Vector3(x, y, z);
    }

    uint PackPosComponent(float f)
    {
        // [-3, 3] to [0, 65535]
        if (f < -3) f = -3;
        if (f > 3) f = 3;
        return Convert.ToUInt32(Mathf.Floor(0.5f * (f / 3 + 1f) * 65535f + 0.5f));
    }
    float UnpackPosComponent(uint i)
    {
        return (i - 32768) * (1.0f / 65535f) * 6f;
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
