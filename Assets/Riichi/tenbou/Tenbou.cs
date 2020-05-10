
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Similar to RiichiTile for the Tenbou sticks so i can use the instanced material
/// and "own" objects through isKinematic on pickup.
/// </summary>
public class Tenbou : UdonSharpBehaviour
{
    Rigidbody rigidbody;
    void Start()
    {
        var renderer = GetComponent<MeshRenderer>();

        var props = new MaterialPropertyBlock();
        props.SetFloat("_Face", int.Parse(name.Substring(0, 1)));
        renderer.SetPropertyBlock(props);

        rigidbody = GetComponent<Rigidbody>();
        rigidbody.maxDepenetrationVelocity = 0.1f;

        // by default don't let players grab the tiles. when seated, the pickup
        // gets enabled.
        var pickup = (VRC_Pickup)(GetComponent(typeof(VRC_Pickup)));
        pickup.pickupable = false;
    }

    void OnPickup()
    {
        rigidbody.isKinematic = false;
    }
}
