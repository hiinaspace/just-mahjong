
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class TileScript : UdonSharpBehaviour
{
    [UdonSynced] private Vector3 pos;
    [UdonSynced] private Quaternion rot;

    private Rigidbody r;

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
        r = GetComponent<Rigidbody>();
        r.maxDepenetrationVelocity = 0.1f;
        //SendCustomEvent("DoTestEvent");
    }

    void OnPickup()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject); // already happens?
        r.isKinematic = false;
    }

    void OnDrop()
    {
    }

    public void DoCustomPhysicsSync()
    {
        if (Networking.IsOwner(gameObject))
        {
            Debug.Log($"I'm the owner of {gameObject.name}, syncing my position");
            // then update our position for the network
            pos = transform.position;
            rot = transform.rotation.normalized; 
        } else
        {
            Debug.Log($"not {gameObject.name} owner, getting position");
            // if we're not the tile owner, just keep it kinematic
            r.isKinematic = true;
            r.velocity = Vector3.zero;
            r.angularVelocity = Vector3.zero;
            // set it from the variables
            r.MovePosition(pos);
            r.MoveRotation(rot.normalized); // weird
        }
    }
}
