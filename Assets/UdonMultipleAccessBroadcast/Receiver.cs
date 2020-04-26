
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Serialization.OdinSerializer;

/// <summary>
/// Receiver polls the Busses and defragments any packets it finds into the
/// public recvBuffer, then notifies other behaviors with custom events that
/// there's new data to read.
/// </summary>
public class Receiver : UdonSharpBehaviour
{
    public Bus[] busses;

    public UdonBehaviour eventTarget;
    public string eventName;

    public const int busLen = 8;
    // XXX Bus.maxSyncedStringSize in udonsharp gets translated to a field access
    private const int maxSyncedStringSize = 105;
    private const int headerSize = 5;

    private const int maxUnfragmentedPacketSize = (int)(maxSyncedStringSize * 7f / 8f - headerSize);
    private const int maxPacketSize = busLen * maxUnfragmentedPacketSize;

    private char[][] charRecvBuffer;// = new char[busLen][];
    private int[] charRecvBufferSeqNo = new int[busLen];

    [HideInInspector]
    public byte[][] recvBuffer;// = new byte[busLen][];
    [HideInInspector]
    public int[] recvBufferSizes = new int[busLen];
    [HideInInspector]
    public int recvBufferLen = 0;

    private int[] lastReadSeqNo = new int[busLen];

    private float lastCheck = 0;
    // updates only happen every 200ms on other clients, so don't need to check every Update()
    private const float checkInterval = 0.05f;

    void Start()
    {
        // possible to receive up to N different packets simultaneously, though unlikely.
        for (int i = 0; i < busLen; ++i)
        {
            recvBuffer[i] = new byte[maxPacketSize];
            charRecvBuffer[i] = new char[maxSyncedStringSize * busLen];
            lastReadSeqNo[i] = -1;
        }
    }

    void Update()
    {
        lastCheck += Time.deltaTime;
        if (lastCheck > checkInterval)
        {
            lastCheck = 0;
            DoCheck();
        }
    }

    void DoCheck() {
        // clear out the recvBuffer seq no
        for (int i = 0; i < busLen; ++i)
        {
            charRecvBufferSeqNo[i] = -1;
        }

        char[] header = new char[headerSize];
        for (int i = 0; i < busLen; ++i)
        {
            var bus = busses[i];
            //bus.string0.CopyTo(0, header, 0, headerSize);
            int seqNo = header[0];
            seqNo = (seqNo << 7) + header[1];

            // already saw
            if (seqNo == lastReadSeqNo[i]) continue;

            Defragment(i, seqNo, header);

            lastReadSeqNo[i] = seqNo;
        }

        // now at charRecvBuffers are full where charRecvBufferSeqNo != -1
        // update number of full recvBuffers as well
        recvBufferLen = 0;
        while (charRecvBufferSeqNo[recvBufferLen] != 1)
        {
            Deserialize(recvBufferLen);
        }

        // notify new stuff
        eventTarget.SendCustomEvent(eventName);
    }

    void Defragment(int i, int seqNo, char[] header)
    {
        var bus = busses[i];

        int frag = header[4];

        // probe charRecvBuffers for the same seqNo or empty (-1)
        // XXX not very elegant
        int recvIdx = -1;
        char[] recvChars;
        int slotSeqNo;
        do
        {
            recvIdx++;
            recvChars = charRecvBuffer[recvIdx];
            slotSeqNo = charRecvBufferSeqNo[recvIdx];
        }
        while (slotSeqNo >= 0 && slotSeqNo != seqNo);
        // in case it wasn't updated already
        charRecvBufferSeqNo[recvIdx] = seqNo;

        recvBufferSizes[recvIdx] = header[2];
        recvBufferSizes[recvIdx] = (recvBufferSizes[recvIdx] << 7) + header[3];

        // copy into correct position for fragment
        var startFrag = frag * (maxSyncedStringSize * 2 - headerSize);
        //bus.string0.CopyTo(headerSize, recvChars, startFrag, maxSyncedStringSize - headerSize);
        //bus.string1.CopyTo(0, recvChars, startFrag + maxSyncedStringSize - headerSize, maxSyncedStringSize);
    }

    void Deserialize(int idx)
    {
        var chars = charRecvBuffer[idx];
        var recv = recvBuffer[idx];
        int size = recvBufferSizes[idx];
        int n = 0;
        for (int i = 0; i < size;)
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
            recv[i++] = (byte)((pack >> 48) & (ulong)255);
            recv[i++] = (byte)((pack >> 40) & (ulong)255);
            recv[i++] = (byte)((pack >> 32) & (ulong)255);
            recv[i++] = (byte)((pack >> 24) & (ulong)255);

            recv[i++] = (byte)((pack >> 16) & (ulong)255);
            recv[i++] = (byte)((pack >> 8)  & (ulong)255);
            recv[i++] = (byte)((pack >> 0)  & (ulong)255);
        }
    }
}
