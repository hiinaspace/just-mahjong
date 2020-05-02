
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class RiichiTile : UdonSharpBehaviour
{
    private Rigidbody r;
    private MaterialPropertyBlock props;
    private MeshRenderer renderer;
    private BoxCollider collider;
    private VRC_Pickup pickup;

    void Start()
    {
        renderer = GetComponent<MeshRenderer>();

        props = new MaterialPropertyBlock();
        props.SetFloat("_Tile", int.Parse(name.Substring(0,2)));

        SetBackColorOffset(Color.black);

        r = GetComponent<Rigidbody>();
        r.maxDepenetrationVelocity = 0.1f;

        // disable pickup by default; when you hit a sort tile button you can
        // grab them. Prevents non-players from messing with tiles.
        pickup = (VRC_Pickup)(GetComponent(typeof(VRC_Pickup)));
        // XXX doesn't work pickup.enabled = false;
        collider = GetComponent<BoxCollider>();
        collider.enabled = false;
    }
    void OnPickup()
    {
        r.isKinematic = false;
    }

    void OnDrop()
    {
    }
    
    public bool IsHeld()
    {
        return pickup.IsHeld;
    }

    public void SetBackColorOffset(Color color)
    {
        props.SetColor("_BackColorOffset", color);
        renderer.SetPropertyBlock(props);
    }
}
