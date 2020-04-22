
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class TileScript : UdonSharpBehaviour
{
    private Rigidbody r;

    void Start()
    {
        var props = new MaterialPropertyBlock();
        props.SetFloat("_Tile", Mathf.Floor(float.Parse(name)));
        GetComponent<MeshRenderer>().SetPropertyBlock(props);

        // try to stop exploding tiles
        r = GetComponent<Rigidbody>();
        r.maxDepenetrationVelocity = 0.1f;
        //SendCustomEvent("DoTestEvent");

        // disable pickup by default; when you hit a sort tile button you can
        // grab them. Prevents non-players from messing with tiles.
        //var pickup = (VRC_Pickup)(GetComponent(typeof(VRC_Pickup)));
        // XXX doesn't work pickup.enabled = false;
        GetComponent<BoxCollider>().enabled = false;
    }
    void OnPickup()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject); // already happens?
        r.isKinematic = false;
    }

    void OnDrop()
    {
    }
}
