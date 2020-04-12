
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

    //// ultra jank: try to make each client individually snap tiles into place.
    //// using a string encoding of "{gameobject.name}:{n:D2}:" of the entire shuffle state here
    //[UdonSynced] public string shuffleState;

    public float shuffleRateSec = 0.01f; 
    [UdonSynced] bool shuffling = false;
    private float lastShuffle = 0;
    private int shuffleNo;

    public Material onMaterial;
    public Material offMaterial;

    void Start()
    {
        //lastShuffleTime = float.NegativeInfinity;
        tiles = new Transform[136];
        placements = new Transform[136];
        //shuffleState = "";
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tileParent.GetChild(i);
            placements[i] = placementParent.GetChild(i);
        }
        //DoShuffle();
    }
    void Interact()
    {
        DoShuffle();
    }

    void FixedUpdate()
    {
        if (shuffling)
        {
            if (!Networking.IsMaster) return;
            DoSlowShuffle();
        }
    }

    void DoSlowShuffle()
    {
        lastShuffle += Time.deltaTime;
        // shuffle rate
        if (lastShuffle < shuffleRateSec) return;

        // it seems like udon's network sync will only hard-teleport (instant) rigidbodies
        // that are some set distance away. So, first try to teleport all the tiles to the moon
        // and then slowly teleport them back. hacks upon hacks.

        // honestly don't think the slow shuffle helps at all, nor the moon teleport.
        // it seems arbitrary whether the sync does some incredibly slow lerp or instantly snaps
        // the tile into position.

        lastShuffle = 0;

        var tile = tiles[shuffleNo];
        Networking.SetOwner(Networking.LocalPlayer, tile.gameObject); // already happens? dunno
        var placement = placements[shuffleNo];
        var rb = tile.gameObject.GetComponent<Rigidbody>();
        //// not sure which is better yet
        //rb.MovePosition(placement.position);
        //rb.MoveRotation(placement.rotation);
        // apparently "faster" than modifying the transform.
        // I dunno if it matters much over the network sync though.
        rb.position = placement.position;
        rb.rotation = placement.rotation;
        rb.WakeUp(); // maybe this gets synced? dunno

        shuffleNo++;
        if (shuffleNo == 136)
        {
            // done
            shuffling = false;
            shuffleNo = 0;
            GetComponent<MeshRenderer>().sharedMaterial = onMaterial;
        }
    }

    void DoShuffle()
    {
        if (!Networking.IsMaster) return;
        if (shuffling) return;
        Transform swap;
        Debug.Log("attempting shuffle");

        for (int i = tiles.Length - 1; i >= 1; --i)
        {
            var j = Random.Range(0, i + 1); // range max is exclusive
            swap = tiles[j];
            tiles[j] = tiles[i];
            tiles[i] = swap;
        }

        // it seems like udon's network sync will only hard-teleport (instant) rigidbodies
        // that are some set distance away. So, first try to teleport all the tiles to the moon
        // and then slowly teleport them back. hacks upon hacks.
        // obviously turn collisions off first.
        var moon = new Vector3(0, 20, 0);
        for (int i = 0; i < tiles.Length; ++i)
        {
            tiles[i].position = moon;
            tiles[i].rotation = Quaternion.identity;
        }

        // enable slow shuffle loop in FixedUpdate;
        shuffling = true;
        lastShuffle = 0;
        GetComponent<MeshRenderer>().sharedMaterial = offMaterial;
    }
}
