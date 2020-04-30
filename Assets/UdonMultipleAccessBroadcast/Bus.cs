//#define DEBUG
#undef DEBUG

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
/// The actual protocol source of truth is the code. It's roughly vanila
/// CSMA-CA, but with more complications to the additional difficulty of the
/// local race between ownership and setting synced variables.
/// 
/// Also, since we have multiple channels (generally), and transmitting on an
/// already owned channel is pretty reliable, generally clients will stick to
/// their own channel.
/// 
/// Experimentally, this works well until there are less channels than clients
/// actively wanting to transmit (tested with 2 clients, 1 channel). The ack
/// check (channel is still owned a while after setting variables) is an
/// unreliable indictator that the other client actually recieved the frame; it
/// does still work about 90% of the time, but 10% loss is rough for some use
/// cases.
///
/// My main concern is that the pretty gross state machines in here have some
/// bugs that will crash or starve a client completely, so there's also a bunch
/// of visual debugging materials and blinkenlights. I think making that
/// available in a world should at least give some feedback when things are
/// totally broken. For # of clients less than number of channels, I think
/// it should be pretty robust.
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

    private int channelCount = 8;

    public Channel[] channels;
#if (DEBUG)
    public MeshRenderer[] blinkenlights;
    public MeshRenderer blinkenlightSend;
    public Material idleMat;
    public Material idleOursMat;
    public Material sendWaitMat;
    public Material ownerWaitMat;
    public Material cooldownWaitMat;

    public MeshRenderer blinkenlightRecv;
    public Material recvMat;

    public Material sendMat;
    public Material ackMat;
#endif

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
    
    public float ackWait = 0.2f;
    public float minContentionWait = 0.3f;
    public float maxContentionWait = 0.5f;
    public float minCooldownWait = 0.2f;
    public float maxCooldownWait = 0.4f;

#if (DEBUG)
    public Text cooldown;
    public Text ackWaitText;
    public Text numChans;

    public void NumChanDown()
    {
        channelCount = Mathf.Max(1, channelCount - 1);
        numChans.text = $"numChans: {channelCount}";
    }
    public void NumChanUp()
    {
        channelCount = Mathf.Min(8, channelCount + 1);
        numChans.text = $"numChans: {channelCount}";
    }

    public void AckWaitUp()
    {
        ackWait += 0.1f;
        ackWaitText.text = $"ack {ackWait}";
    }
    public void AckWaitDown()
    {
        ackWait -= 0.1f;
        ackWaitText.text = $"ack {ackWait}";
    }
    public void CooldownUp()
    {
        maxCooldownWait += 0.1f;
        minCooldownWait += 0.1f;
        cooldown.text = $"CD {maxCooldownWait}";
    }
    public void CooldownDown()
    {
        maxCooldownWait -= 0.1f;
        minCooldownWait -= 0.1f;
        cooldown.text = $"CD {maxCooldownWait}";
    }

#endif

    void Start()
    {
        recvBuffer = new byte[recvBufferSize][];
        successfulAckedObjects = new object[recvBufferSize];
#if (DEBUG)
        numChans.text = $"numChans: {channelCount}";
        cooldown.text = $"CD {maxCooldownWait}";
        ackWaitText.text = $"ack {ackWait}";
#endif
    }

    void Update()
    {
        SendFrame();
        ReleaseChannels();
        RecvFrame();
        //SimulateSend();
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

#if DEBUG
        blinkenlightSend.sharedMaterial = idleMat;
#endif

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

#if DEBUG
                blinkenlightSend.sharedMaterial = sendWaitMat;
#endif
                return;
            }
            activeSendChannel = chan;

            // XXX ownership change is not atomic with setting synced strings; every so often
            // setting the string0/1 on chan has no effect, presumably because udon's rejection of
            // non-owner updates races with SetOwner.
            // thus, try to get ownership a few times before proceeding. XXX weird state machine
            Networking.SetOwner(Networking.LocalPlayer, activeSendChannel.gameObject);
            ownerWaitTime = 0.2f;
#if DEBUG
            blinkenlightSend.sharedMaterial = ownerWaitMat;
#endif
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
#if DEBUG
            blinkenlightSend.sharedMaterial = sendWaitMat;
#endif
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
#if DEBUG
        blinkenlightSend.sharedMaterial = cooldownWaitMat;
#endif
    }

    Channel ProbeIdleChannel()
    {
        // start random to avoid contention
        var startIdx = UnityEngine.Random.Range(0, channelCount);
        var idx = startIdx;
        var idleIdx = -1;
        Channel idleChan = null;
        do
        {
            var chan = channels[idx];
            if (chan.string0.Length == 0)
            {
                idleChan = chan;
                idleIdx = idx;
                if (Networking.IsOwner(chan.gameObject))
                {
                    // bias towards already owned channels; if all clients do
                    // this and the send cooldown is greater than the ack wait,
                    // then up to N clients should basically settle on a single
                    // random channel and retain ownership, which is pretty reliable;
                    // for a new client that doesn't own any channels, I think they'll
                    // still have a small window of (sendCooldown - ack wait + ownerWait)
                    // to race and take over the channel; Need to do some simulations with
                    // 1 channel and two clients to test.
                    // XXX not very clean logic for this
                    break;
                }
            }
            idx = (idx + 1) % channelCount;
        } while (idx != startIdx);

#if DEBUG
        if (idleChan != null)
        {
            blinkenlights[idleIdx].sharedMaterial = sendMat;
            Debug.Log($"sending on chan {idleChan.gameObject.name}");
        }
#endif
        return idleChan;
    }

    void ReleaseChannels()
    {
        for (int i = 0; i < channels.Length; i++)
        {
            Channel chan = channels[i];
            if (chan == activeSendChannel) continue;

            if (chan.string0.Length > 0 && Networking.IsOwner(chan.gameObject)
                && (Time.time - chan.lastLocalSend) > ackWait)
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

#if DEBUG
                blinkenlights[i].sharedMaterial = ackMat;
            } else if (chan.string0.Length == 0)
            {
                if (Networking.IsOwner(blinkenlights[i].gameObject))
                {
                    blinkenlights[i].sharedMaterial = idleOursMat;
                } else
                {
                    blinkenlights[i].sharedMaterial = idleMat;
                }
#endif
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
#if DEBUG
                blinkenlights[i].sharedMaterial = recvMat;
                blinkenlightRecv.sharedMaterial = recvMat;
                Debug.Log($"Received on chan {i}");
                recv = true;
#endif
            }
        }
#if DEBUG
        if (!recv)
            blinkenlightRecv.sharedMaterial = idleMat;
#endif
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
