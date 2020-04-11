
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class SetMaterialPropertyBlock : UdonSharpBehaviour
{
    public int tile;

    void Start()
    {
        var props = new MaterialPropertyBlock();
        props.SetFloat("_Tile", Mathf.Floor(float.Parse(name)));
        GetComponent<MeshRenderer>().SetPropertyBlock(props);

        // try to stop exploding tiles
        GetComponent<Rigidbody>().maxDepenetrationVelocity = 0.01f;
    }

    void OnPickup()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject); // already happens?
        GetComponent<Rigidbody>().isKinematic = false;
    }
}
