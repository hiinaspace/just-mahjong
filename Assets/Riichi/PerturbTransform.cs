
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class PerturbTransform : UdonSharpBehaviour
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
        Perturb();
        if (!Networking.IsMaster) return;
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "Perturb");
    }

    void Perturb()
    {
        var slightly = new Vector3(0, 0.01f, 0);
        Debug.Log("bumping all tiles slightly");
        foreach (Transform t in tiles)
        {
            var r = t.gameObject.GetComponent<Rigidbody>();
            r.MovePosition(t.position + slightly);
            r.AddForce(Vector3.up, ForceMode.Impulse);
            r.WakeUp();
            //Debug.Log($"r.isKinematic {r.isKinematic}, r.useGravity {r.useGravity}, detectCollisions: {r.detectCollisions}, mass {r.mass}");
        }
    }
}
