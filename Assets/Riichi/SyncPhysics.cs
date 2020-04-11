
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class SyncPhysics : UdonSharpBehaviour
{
    public Transform tileParent;

    Transform[] tiles;

    void Start()
    {
        tiles = new Transform[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tileParent.GetChild(i);
        }
    }

    void Interact()
    {
        if (!Networking.IsMaster) return;
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "DoForce");
    }

    void DoForce()
    {
        foreach (Transform t in tiles)
        {
            t.gameObject.GetComponent<Rigidbody>().AddForce(Vector3.up, ForceMode.Impulse);
        }
    }
}
