
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class TileScript : UdonSharpBehaviour
{
    //public Transform placementParent;
    //public Shuffle shuffler;
    //private float lastTimeChecked;
    //private Transform[] placements;


    //// ultra jank synced shuffling. Since udon position sync for
    //// rigidbodies is so slow, try to snap everything locally as well
    //// when a shuffle is active, by reading the synced shuffler state from the
    //// master.

    //void FixedUpdate()
    //{
    //    var t = Time.time;
    //    if ((t - lastTimeChecked) > 4)
    //    {
    //        lastTimeChecked = t;
    //        DoMove();
    //    }
    //}

    //void DoMove()
    //{
    //    // read position out of shuffler;
    //    var state = shuffler.shuffleState;
    //    if (state.Length == 0) return; // no shuffle currently
    //    // find our position
    //    var posStart = state.IndexOf(gameObject.name) + 5; // name is 4 chars, followed by : and 3 char idx into placements
    //    var p = int.Parse(state.Substring(posStart, 3));
    //    Debug.Log($"moving {gameObject.name} to pos {p}");

    //    transform.position = placements[p].position; 
    //    transform.rotation = placements[p].rotation;
    //}

    void Start()
    {

        //placementParent = GameObject.Find("Placements").transform;
        //placements = new Transform[136];
        //for (int i = 0; i < 136; ++i)
        //{
        //    placements[i] = placementParent.GetChild(i);
        //}

        var props = new MaterialPropertyBlock();
        props.SetFloat("_Tile", Mathf.Floor(float.Parse(name)));
        GetComponent<MeshRenderer>().SetPropertyBlock(props);

        // try to stop exploding tiles
        var r = GetComponent<Rigidbody>();
        r.maxDepenetrationVelocity = 0.1f;
        r.solverIterations = 30;
    }

    void OnPickup()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject); // already happens?
        var r = GetComponent<Rigidbody>();
        //Debug.Log($"original on pickup tile ${gameObject.name}, kinematic: {r.isKinematic}, gravity {r.useGravity}, detectCollisions: {r.detectCollisions}, mass {r.mass}");
        //r.isKinematic = false ;  // doesn't work at all
        GetComponent<ConstantForce>().enabled = false;
        Debug.Log($"on pickup tile ${gameObject.name}, kinematic: {r.isKinematic}, gravity {r.useGravity}, detectCollisions: {r.detectCollisions}, mass {r.mass}");
    }

    void OnDrop()
    {
        var r = GetComponent<Rigidbody>();
        Debug.Log($"original on drop ${gameObject.name}, kinematic: {r.isKinematic}, gravity {r.useGravity}, detectCollisions: {r.detectCollisions}, mass {r.mass}");
        //r.isKinematic = true; // does this event work at all
        //Debug.Log($"on drop tile ${gameObject.name}, kinematic: {r.isKinematic}, gravity {r.useGravity}, detectCollisions: {r.detectCollisions}, mass {r.mass}");
    }
}
