
using System;
using System.Collections;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Bus broadcasts "frames" of char[] as the smallest atomically broadcastable
/// unit in Udon, using multiple Channels for maximum throughput and concurrency
/// with multiple broadcasters.
/// 
/// Similar to radio, Bus uses a 'carrier-sense' collision avoidance protocol.
/// In order to set a UdonSynced variable and have it broadcasted to other clients,
/// the local player must own the gameObject containing the UdonBehavior. Whatever
/// internal network loop that picks up ownership changes and synced variable changes
/// appears to run about every 200ms locally. If two clients try to take ownership
/// and change the synced variables simultaneously, Bus assumes that the server will
/// choose a winner and sync back the winning owner/values in about another 200ms.
/// 
/// So, Bus uses the following protocol:
/// 
/// 1. starting at a random Channel, probe for a Channel with empty ("") synced variables
/// as a sentinel for 'idle Channel'.
/// 2. Take ownership of the idle channel and set our frame in the variables.
/// 3. wait ~400ms.
/// 4. If the same channel is still owned by the local player and its synced variables
/// are still equal to whatever we put there, assume that no other client tried to
/// use the Channel in the meantime, and that all other clients also received the frame.
/// Set the Channel variables back to "" to put the Channel back to idle.
/// 4.1. If not, assume some other client got to the channel before us. Wait a random
/// interval (possibly with exponential backoff) and try to send the frame again.
/// 
/// I don't know if it's entirely necesssary, or if a "fire and forget" protocol would
/// also work, if the vrchat servers will see both ownership changes and synced variables,
/// choose an arbitrary order, then broadcast both, leaving other clients with a view of both
/// frames (however briefly). I think waiting for a confirmation in the form of "yeah it's 
/// still our Channel" is at least safe though, if slower.
/// </summary>
public class Bus : UdonSharpBehaviour
{
    private const int maxSyncedStringSize = 105;
    private const int maxPacketCharSize = maxSyncedStringSize * 2;
    // 14 bits leftover to do something with
    // possibly packing player id + seqNo to monitor per-player packet loss.
    private const int headerCharSize = 2; 
    // for simplicity of the byte -> 7bit packing, which packs 7 bytes to 8 chars, 56 bits at a time
    // header size at 2 makes this a nice round 208 chars or 182 bytes
    private const int maxDataCharSize = (int)((maxPacketCharSize - headerCharSize) / 8f) * 8;
    private const int maxDataByteSize = maxDataCharSize / 8 * 7;

    private const int channelCount = 8;

    public Channel[] channels;
    public MeshRenderer[] blinkenlights;
    public MeshRenderer blinkenlightSend;
    public Material idleMat;
    public Material sendWaitMat;
    public Material ownerWaitMat;
    public Material cooldownWaitMat;

    public MeshRenderer blinkenlightRecv;
    public Material recvMat;

    public Material sendMat;
    public Material ackMat;

    [HideInInspector]
    public byte[] sendBuffer = new byte[maxDataByteSize];
    [HideInInspector]
    public object sendAckObject;
    [HideInInspector]
    public bool sendBufferReady = false;
    [HideInInspector]
    public bool sendReady = true;

    // udon behaviors have problems with quick instance variable updates
    // being visible to other behaviors on the same Update() frame. Thus,
    // keep received packets in a little ring buffer and have receivers
    // keep track of their own pointers.
    public int recvBufferSize = 16;
    [HideInInspector]
    public byte[][] recvBuffer;
    [HideInInspector]
    public int recvBufferHead = 0;

    // same circular buffer
    [HideInInspector]
    public object[] successfulAckedObjects;
    [HideInInspector]
    public int successfulAckedHead = 0;
    
    public float successfulBroadcastInterval = 2f;
    public float minContentionWait = 0.3f;
    public float maxContentionWait = 0.5f;
    public float minCooldownWait = 1f;
    public float maxCooldownWait = 1.5f;

    public Text cooldown;
    public Text ackWait;

    public void AckWaitUp()
    {
        successfulBroadcastInterval += 0.1f;
    }
    public void AckWaitDown()
    {
        successfulBroadcastInterval -= 0.1f;
    }
    public void CooldownUp()
    {
        maxCooldownWait += 0.1f;
        minCooldownWait += 0.1f;
    }
    public void CooldownDown()
    {
        maxCooldownWait -= 0.1f;
        minCooldownWait -= 0.1f;
    }

    void Start()
    {
        recvBuffer = new byte[recvBufferSize][];
        successfulAckedObjects = new object[recvBufferSize];
    }

    void Update()
    {
        SendFrame();
        ReleaseChannels();
        RecvFrame();
        //SimulateSend();
        cooldown.text = $"CD {maxCooldownWait}";
        ackWait.text = $"ack {successfulBroadcastInterval}";
    }

    float simulateSendWaitTime = 0;

    int simulateSeqNo = 0;

    void SimulateSend()
    {
        simulateSendWaitTime -= Time.deltaTime;
        if (simulateSendWaitTime > 0) return;
        simulateSendWaitTime = UnityEngine.Random.Range(0.0f, 0.02f);

        // start random to avoid contention
        var startIdx = UnityEngine.Random.Range(0, channelCount);
        var idx = startIdx;
        do
        {
            var chan = channels[idx];
            // if idle
            if (chan.string0.Length == 0)
            {
                var buf = new byte[maxDataByteSize];
                buf[0] = (byte)simulateSeqNo;
                var frame = SerializeFrame(buf);
                chan.string0 = new string(frame, 0, maxSyncedStringSize);
                chan.string1 = new string(frame, maxSyncedStringSize, maxSyncedStringSize);
                chan.lastLocalSend = Time.time;
                chan.string1 = chan.string0;
                simulateSeqNo = (simulateSeqNo + 1) % 256;
                return;
            }
            idx = (idx + 1) % channelCount;
        } while (idx != startIdx);
    }

    float sendWaitTime = 0;
    Channel activeSendChannel = null;
    float ownerWaitTime = 0;

    void SendFrame()
    {
        sendWaitTime -= Time.deltaTime;
        if (sendWaitTime > 0) return;

        blinkenlightSend.sharedMaterial = idleMat;

        // if not actively sending
        // XXX very messy state machine
        if (activeSendChannel == null)
        {
            sendReady = true;
        }

        if (!sendBufferReady) return;

        // if we haven't already selected a channel
        if (activeSendChannel == null)
        {
            var chan = ProbeIdleChannel();
            if (chan == null)
            {
                // no idle channels, wait random
                sendReady = false;
                sendWaitTime = UnityEngine.Random.Range(minContentionWait, maxContentionWait);
                blinkenlightSend.sharedMaterial = sendWaitMat;
                return;
            }
            activeSendChannel = chan;

            // XXX ownership change is not atomic with setting synced strings; every so often
            // setting the string0/1 on chan has no effect, presumably because udon's rejection of
            // non-owner updates races with SetOwner.
            // thus, try to get ownership a few times before proceeding. XXX weird state machine
            Networking.SetOwner(Networking.LocalPlayer, activeSendChannel.gameObject);
            ownerWaitTime = 0.2f;
            blinkenlightSend.sharedMaterial = ownerWaitMat;
            return; // wait for owner
        }

        sendReady = false;

        // waiting for owner.
        ownerWaitTime -= Time.deltaTime;
        if (ownerWaitTime > 0) return;

        // attempt to send

        // probably not necessary to spam this, but worth a try.

        if (!Networking.IsOwner(activeSendChannel.gameObject))
        {
            Debug.Log($"couldn't retain ownership of {activeSendChannel.gameObject.name}, retrying");
            sendWaitTime = UnityEngine.Random.Range(minContentionWait, maxContentionWait);
            blinkenlightSend.sharedMaterial = sendWaitMat;
            sendReady = false;
            activeSendChannel = null;
            return;
        }

        // TODO no header
        var frame = SerializeFrame(sendBuffer);
        activeSendChannel.string0 = new string(frame, 0, maxSyncedStringSize);
        activeSendChannel.string1 = new string(frame, maxSyncedStringSize, maxSyncedStringSize);

        // bookkeeping state;
        activeSendChannel.lastLocalSend = Time.time;
        activeSendChannel.lastLocalString0 = activeSendChannel.string0;
        activeSendChannel.localAckObject = sendAckObject;

        // flush packet.
        sendBufferReady = false;
        sendBuffer = new byte[maxDataByteSize];
        sendAckObject = null;

        activeSendChannel = null;

        // wait before attempting next send
        sendReady = false;
        sendWaitTime = UnityEngine.Random.Range(minCooldownWait, maxCooldownWait);
        blinkenlightSend.sharedMaterial = cooldownWaitMat;
    }

    Channel ProbeIdleChannel()
    {
        // start random to avoid contention
        var startIdx = UnityEngine.Random.Range(0, channelCount);
        var idx = startIdx;
        do
        {
            var chan = channels[idx];
            // if idle
            if (chan.string0.Length == 0)
            {
                blinkenlights[idx].sharedMaterial = sendMat;
                Debug.Log($"sending on chan {idx}");
                return chan;
            }
            idx = (idx + 1) % channelCount;
        } while (idx != startIdx);
        return null;
    }

    void ReleaseChannels()
    {
        for (int i = 0; i < channels.Length; i++)
        {
            Channel chan = channels[i];
            if (chan == activeSendChannel) continue;

            if (chan.string0.Length > 0 && Networking.IsOwner(chan.gameObject)
                && (Time.time - chan.lastLocalSend) > successfulBroadcastInterval)
            {
                Debug.Log($"releasing chan {i}, last used at {chan.lastLocalSend}, now {Time.time}");
                // mark channel idle
                chan.string0 = "";
                chan.lastLocalString0 = "";

                // give upstream the last ack
                // TODO need buffer of these
                successfulAckedObjects[successfulAckedHead] = chan.localAckObject;
                chan.localAckObject = null;
                successfulAckedHead = (successfulAckedHead + 1) % recvBufferSize;

                blinkenlights[i].sharedMaterial = ackMat;
            } else if (chan.string0.Length == 0)
            {
                blinkenlights[i].sharedMaterial = idleMat;
            }
        }
    }

    void RecvFrame()
    {
        bool recv = false;
        for (int i = 0; i < channels.Length; i++)
        {
            Channel chan = channels[i];
            // if not idle and we didn't send it
            if (chan.string0 != "" && chan.string0 != chan.lastLocalString0)
            {
                chan.lastLocalString0 = chan.string0;
                var frame = new char[maxPacketCharSize];
                // TODO no header
                chan.string0.CopyTo(0, frame, 0, maxSyncedStringSize);
                chan.string1.CopyTo(0, frame, chan.string0.Length, maxSyncedStringSize);

                recvBuffer[recvBufferHead] = DeserializeFrame(frame);
                recvBufferHead = (recvBufferHead + 1) % recvBufferSize;

                blinkenlights[i].sharedMaterial = recvMat;
                blinkenlightRecv.sharedMaterial = recvMat;
                Debug.Log($"Received on chan {i}");
                recv = true;
            }
        }
        if (!recv)
            blinkenlightRecv.sharedMaterial = idleMat;
    }

    private char[] SerializeFrame(byte[] buf)
    {
        var frame = new char[maxPacketCharSize];
        int n = 0;
        for (int i = 0; i < maxDataByteSize;)
        {
            // pack 7 bytes into 56 bits;
            ulong pack =         buf[i++];
            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];

            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];
            //DebugLong("packed: ", pack);

            // unpack into 8 7bit asciis
            frame[n++] = (char)((pack >> 49) & (ulong)127);
            frame[n++] = (char)((pack >> 42) & (ulong)127);
            frame[n++] = (char)((pack >> 35) & (ulong)127);
            frame[n++] = (char)((pack >> 28) & (ulong)127);

            frame[n++] = (char)((pack >> 21) & (ulong)127);
            frame[n++] = (char)((pack >> 14) & (ulong)127);
            frame[n++] = (char)((pack >> 7)  & (ulong)127);
            frame[n++] = (char)(pack         & (ulong)127);
            //DebugChars("chars: ", chars, n - 8);
        }
        return frame;
    }

    private byte[] DeserializeFrame(char[] frame)
    {
        var packet = new byte[maxDataByteSize];
        int n = 0;
        for (int i = 0; i < maxDataByteSize;)
        {
            //DebugChars("deser: ", chars, n);
            // pack 8 asciis into 56 bits;
            ulong pack =         frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            //DebugLong("unpacked: ", pack);

            // unpack into 7 bytes
            packet[i++] = (byte)((pack >> 48) & (ulong)255);
            packet[i++] = (byte)((pack >> 40) & (ulong)255);
            packet[i++] = (byte)((pack >> 32) & (ulong)255);
            packet[i++] = (byte)((pack >> 24) & (ulong)255);

            packet[i++] = (byte)((pack >> 16) & (ulong)255);
            packet[i++] = (byte)((pack >> 8)  & (ulong)255);
            packet[i++] = (byte)((pack >> 0)  & (ulong)255);
        }
        return packet;
    }
        
}
