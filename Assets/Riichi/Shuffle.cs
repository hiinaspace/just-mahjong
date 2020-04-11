
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class Shuffle : UdonSharpBehaviour
{
    public Transform tileParent;
    public Transform placementParent;

    Transform[] tiles;
    Transform[] placements;

    void Start()
    {
        tiles = new Transform[136];
        placements = new Transform[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tileParent.GetChild(i);
            placements[i] = placementParent.GetChild(i);
        }
        DoShuffle();
    }
    void Interact()
    {
        DoShuffle();
    }

    void DoShuffle()
    {
        TryFreeze();
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "TryFreeze");
        if (!Networking.IsMaster) return;
        Transform swap;
        for (int i = tiles.Length - 1; i >= 1; --i)
        {
            var j = Random.Range(0, i + 1); // range max is exclusive
            swap = tiles[j];
            tiles[j] = tiles[i];
            tiles[i] = swap;
        }
        for (int i = 0; i < 136; ++i)
        {
            Networking.SetOwner(Networking.LocalPlayer, tiles[i].gameObject);
            var rigidbody = tiles[i].GetComponent<Rigidbody>();
            rigidbody.velocity = Vector3.zero;
            rigidbody.isKinematic = true;
            rigidbody.Sleep();
            tiles[i].parent = tileParent;
            tiles[i].position = placements[i].position;
            tiles[i].rotation = placements[i].rotation;
        }
    }
    public void TryFreeze()
    {
        for (int i = 0; i < 136; ++i)
        {
            Debug.Log($"attempting to freeze tile {i}");
            tiles[i].parent = tileParent;
            var rigidbody = tiles[i].GetComponent<Rigidbody>();
            rigidbody.velocity = Vector3.zero;
            rigidbody.isKinematic = true;
            rigidbody.Sleep();
        }
    }
}
