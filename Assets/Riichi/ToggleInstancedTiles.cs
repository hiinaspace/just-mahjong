
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ToggleInstancedTiles : UdonSharpBehaviour
{
    public Material onMaterial;
    public Material offMaterial;
    public Material tileMaterial;
    public Material nonInstancedMaterial;
    public Transform tileRoot;


    // XXX need to toggle discard camera too
    public Shader replacementShader;
    public Camera discardCamera;

    bool enableNonInstanced = false;

    void Start()
    {
    }

    void Interact()
    {
        enableNonInstanced = !enableNonInstanced;
        if (enableNonInstanced)
        {
            for (int i = 0; i < tileRoot.childCount; ++i)
            {
                var tile = tileRoot.GetChild(i);
                var mr = tile.gameObject.GetComponent<MeshRenderer>();
                // not sharedMaterial, which will create a bunch of material clones
                // and probably leak them
                mr.material = nonInstancedMaterial;
                mr.material.SetFloat("_Tile", int.Parse(tile.gameObject.name.Substring(0,2)));
            }
            GetComponent<MeshRenderer>().sharedMaterial = onMaterial;
            discardCamera.ResetReplacementShader();
        } else
        {
            for (int i = 0; i < tileRoot.childCount; ++i)
            {
                var tile = tileRoot.GetChild(i);
                tile.gameObject.GetComponent<MeshRenderer>().sharedMaterial = tileMaterial;
            }
            GetComponent<MeshRenderer>().sharedMaterial = offMaterial;

            discardCamera.SetReplacementShader(replacementShader, "RiichiTile");
        }
    }
}
