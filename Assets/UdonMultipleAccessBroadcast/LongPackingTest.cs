
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

public class LongPackingTest : UdonSharpBehaviour
{
    public Text text;
    public Text acksText;
    public Text recvsText;
    public Bus bus;

    private int recvBufIdx = 0;
    private int ackBufIdx = 0;

    private int lines;

    private char[] acks = new char[256];
    private char[] recvs = new char[256];

    void Start()
    {
        ResetThings();
    }

    private bool sending = true;
    public void ToggleSend()
    {
        sending = !sending;
    }

    private float sendWait = 0;

    public void ResetThings()
    {
        for (int i = 0; i < 256; ++i)
        {
            acks[i] = 'N';
            recvs[i] = 'N';
        }
    }

    void Update()
    {
        if (bus.sendReady && sending)
        {
            sendWait -= Time.deltaTime;
            if (sendWait <= 0)
            {
                for (int i = 0; i < 256; ++i)
                {
                    if (acks[i] == 'Y') continue;

                    bus.sendBuffer[0] = (byte)i;
                    bus.sendAckObject = i;
                    bus.sendBufferReady = true;
                    Debug($"sent packet {i}");
                    break;
                }

                sendWait = 0.2f;
            }
        }
        RecvPacket();
        CheckAcks();
        acksText.text = "A"+new string(acks);
        recvsText.text = "R"+new string(recvs);
    }

    public void CheckAcks()
    {
        // new acks available
        while (bus.successfulAckedHead != ackBufIdx)
        {
            var seq = (int)bus.successfulAckedObjects[ackBufIdx];
            if (acks[seq] == 'N')
            {
                Debug($"Ackd packet {seq}");
                acks[seq] = 'Y';
            }

            ackBufIdx = (ackBufIdx + 1) % bus.recvBufferSize;
        }
    }

    public void RecvPacket()
    {
        // new packets available
        while (bus.recvBufferHead != recvBufIdx)
        {
            var seq = bus.recvBuffer[recvBufIdx][0];
            if (recvs[seq] == 'N')
            {
                Debug($"Received packet {seq}");
                recvs[seq] = 'Y';
            }

            recvBufIdx = (recvBufIdx + 1) % bus.recvBufferSize;
        }
    }

    void Debug(string t)
    {
        if (lines++ > 10)
        {
            text.text = text.text.Remove(0, text.text.IndexOf('\n') + 1);
        }
        text.text += $"{Time.time}: " + t + "\n";
    }
}
