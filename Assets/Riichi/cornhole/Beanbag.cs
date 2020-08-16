
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class Beanbag : UdonSharpBehaviour
{
    public int face;
    void Start()
    {
        // just set tile face
        var renderer = transform.GetChild(0).GetComponent<MeshRenderer>();
        var props = new MaterialPropertyBlock();
        props.SetFloat("_Tile", face);
        renderer.SetPropertyBlock(props);
    }
}
