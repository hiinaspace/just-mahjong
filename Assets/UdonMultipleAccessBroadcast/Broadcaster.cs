
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Serialization.OdinSerializer;

/// <summary>
/// Broadcaster takes arbitrary byte[] data and broadcasts it over (possibly)
/// multiple Busses as packed ascii data, which maximizes throughput through
/// udon's synced strings.
/// </summary>
public class Broadcaster : UdonSharpBehaviour
{
    public const int busLen = 8;
    // set in inspector, experimentally i think a max of 8 will work reliably
    public Bus[] busses;

    // XXX Bus.maxSyncedStringSize in udonsharp gets translated to a field access
    private const int maxSyncedStringSize = 105;

    // 14bit seqNo, 14bit length, 7bit fragNo
    private const int headerSize = 5;

    private const int dataSize = maxSyncedStringSize * 2 - headerSize;

    private int maxPacketSize;

    // Experimentally determined to be the minimum interval before udon stops
    // seeing some of the updated synced strings and you get "packet" loss.
    private const float sendIntervalSec = 0.2f;

    // other behaviors mutate this directly and flip `newPacket` to true to
    // trigger a send. Could be replaced with a queue or circular buffer
    // if multiple (local) objects need to send data without colliding.
    [HideInInspector]
    public byte[] sendBuffer;
    [HideInInspector]
    public int sendBufferSize = 0;
    [HideInInspector]
    public bool newPacket = false;

    private int seqNo = 0;

    private float lastSendCheck = 0;

    private char[] encodeBuffer;
    private int encodedBufferSize = 0;

    void Start()
    {
        maxPacketSize = busLen * ((int)(Mathf.Floor(maxSyncedStringSize * 7f / 8f)) - headerSize);
        sendBuffer = new byte[maxPacketSize];
        encodeBuffer = new char[busLen * maxSyncedStringSize];

        // try to space out seqNos between clients so we don't get collisions; 
        // if two fragmented packets have the same seqNo, then Receiver can erroneously defragment them.
        // Networking.LocalPlayer.playerId is worth testing too, I don't know if that's a small int or
        // some random 32 bit thing, which would take a lot of room (5 chars).
        // use two chars (14 bits).
        seqNo = UnityEngine.Random.Range(0, 16384); // exclusive top range
    }

    void Update()
    {
        lastSendCheck += Time.deltaTime;
        if (lastSendCheck > sendIntervalSec)
        {
            lastSendCheck = 0;
            DoSend();
        }
    }

    void DoSend()
    {
        if (!newPacket) return;
        
        if (sendBufferSize > maxPacketSize)
        {
            Debug.Log($"ERROR: can't send a packet of size {sendBufferSize}, max {maxPacketSize}");
            return;
        }

        int n = 0;
        // encode sendBuffer into encodedPacket in ascii
        for (int i = 0; i < sendBufferSize;)
        {
            // pack 7 bytes into 56 bits;
            ulong pack =         sendBuffer[i++];
            pack = (pack << 8) + sendBuffer[i++];
            pack = (pack << 8) + sendBuffer[i++];

            pack = (pack << 8) + sendBuffer[i++];
            pack = (pack << 8) + sendBuffer[i++];
            pack = (pack << 8) + sendBuffer[i++];
            pack = (pack << 8) + sendBuffer[i++];
            //DebugLong("packed: ", pack);

            // unpack into 8 7bit asciis
            encodeBuffer[n++] = (char)((pack >> 49) & (ulong)127);
            encodeBuffer[n++] = (char)((pack >> 42) & (ulong)127);
            encodeBuffer[n++] = (char)((pack >> 35) & (ulong)127);
            encodeBuffer[n++] = (char)((pack >> 28) & (ulong)127);

            encodeBuffer[n++] = (char)((pack >> 21) & (ulong)127);
            encodeBuffer[n++] = (char)((pack >> 14) & (ulong)127);
            encodeBuffer[n++] = (char)((pack >> 7)  & (ulong)127);
            encodeBuffer[n++] = (char)(pack         & (ulong)127);
            //DebugChars("chars: ", chars, n - 8);
        }
        encodedBufferSize = n;

        // spray chars into busses.
        // TODO need to test how robust this is and whether collision avoidance stuff is needed instead
        // of blindly spraying (local) busses. 
        char[] header = new char[headerSize];
        header[0] = (char)((seqNo >> 7) & 127);
        header[1] = (char)(seqNo & 127);
        header[2] = (char)((sendBufferSize >> 7) & 127);
        header[3] = (char)(sendBufferSize & 127);

        n = 0;
        for (int i = 0; i < busLen; ++i)
        {
            header[4] = (char)i; // fragment no
            int fragmentStart = i * dataSize;
            
            var b = busses[i];
            Networking.SetOwner(Networking.LocalPlayer, b.gameObject);
            // TODO will this work immediately after setting ownership?

            //b.string0 = (new string(header)) + 
            //    new string(encodeBuffer, fragmentStart, maxSyncedStringSize - headerSize);
            //b.string1 = new string(encodeBuffer, fragmentStart + maxSyncedStringSize - headerSize, maxSyncedStringSize);

            n += dataSize;

            // don't need any more busses after this
            if (n >= encodedBufferSize) break;
        }

        seqNo = (seqNo + 1) % 16384;
        newPacket = false;
    }
}
