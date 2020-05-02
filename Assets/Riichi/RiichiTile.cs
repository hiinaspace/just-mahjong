
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class RiichiTile : UdonSharpBehaviour
{
    private Rigidbody r;

    void Start()
    {
        var props = new MaterialPropertyBlock();
        props.SetFloat("_Tile", int.Parse(name.Substring(0,2)));
        GetComponent<MeshRenderer>().SetPropertyBlock(props);

        r = GetComponent<Rigidbody>();
        r.maxDepenetrationVelocity = 0.1f;

        // disable pickup by default; when you hit a sort tile button you can
        // grab them. Prevents non-players from messing with tiles.
        //var pickup = (VRC_Pickup)(GetComponent(typeof(VRC_Pickup)));
        // XXX doesn't work pickup.enabled = false;
        GetComponent<BoxCollider>().enabled = false;
    }
    void OnPickup()
    {
        r.isKinematic = false;
    }

    void OnDrop()
    {
    }
}
