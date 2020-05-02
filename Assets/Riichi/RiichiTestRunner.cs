using UdonSharp;
using UnityEngine;
using UnityEngine.Tilemaps;
using VRC.Udon;

public class RiichiTestRunner : UdonSharpBehaviour
{
    public RiichiGame game;
    public RiichiSeat seat0;
    public Bus bus;
    public TimerWheel wheel;

    public Transform tileRoot;

    const int INIT = 0, SHUFFLED = 1, SEATED = 2, SORTED = 4, DONE = 3;
    int state = INIT;

    bool captured = false;
    byte[] sendCapture;
    object ackCapture;

    UdonBehaviour self;

    void Start()
    {
        self = (UdonBehaviour)GetComponent(typeof(UdonBehaviour));

        wheel.Delay(0.2f, self, "DoTest");
        wheel.Repeat(0.2f, self, "CapturePacket");
    }

    public void DoTest()
    {
        switch (state)
        {
            case INIT:
                game.Shuffle();
                state = SHUFFLED;
                wheel.Delay(0.2f, self, "DoTest");
                break;
            case SHUFFLED:
                seat0.TakeSeat();
                state = SEATED;
                wheel.Delay(0.2f, self, "DoTest");
                break;
            case SEATED:
                seat0.SortHand();
                state = SORTED;
                wheel.Delay(1.2f, self, "DoTest");
                break;
            case SORTED:
                var tile = tileRoot.GetChild(Random.Range(0, 136)).gameObject;
                tile.transform.localPosition = new Vector3(Random.Range(-0.6f, 0.6f), 0.15f, Random.Range(-0.6f,0.6f));
                tile.transform.localRotation = Quaternion.Euler(Random.Range(0,360), Random.Range(0,360), Random.Range(0,360));
                tile.GetComponent<Rigidbody>().isKinematic = false;
                wheel.Delay(1.0f, self, "DoTest");
                break;
        }
    }

    void Update() {
        if (bus.sendBufferReady && !captured)
        {
            sendCapture = new byte[182];
            bus.sendBuffer.CopyTo(sendCapture, 0);
            ackCapture = bus.sendAckObject;
            captured = true;
        }
    }

    public void CapturePacket() { 
        if (captured)
        {
            // echo back into recv buffer, different game
            int header = 64 + (sendCapture[0] & 63);
            sendCapture[0] = (byte)header;
            //Debug.Log($"echoing packet with header {header}");
            bus.recvBuffer[bus.recvBufferHead] = sendCapture;
            bus.recvBufferHead = (bus.recvBufferHead + 1) % bus.recvBufferSize;
            bus.successfulAckedObjects[bus.successfulAckedHead] = ackCapture;
            bus.successfulAckedHead = (bus.successfulAckedHead + 1) % bus.recvBufferSize;
            captured = false;
        }
    }
}
