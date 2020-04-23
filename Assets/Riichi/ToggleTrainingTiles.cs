
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class ToggleTrainingTiles : UdonSharpBehaviour
{
    public Material onMaterial;
    public Material offMaterial;
    public Material tileMaterial;
    public Texture trainingTexture;
    public Texture regularTexture;
    public Transform tileRoot;

    bool enableTrainingMode = false;

    void Start()
    {

        //var names = tileMaterial.GetTexturePropertyNames();
        //foreach(var name in names)
        //{
        //    Debug.Log($"tex naem {name}");
        //}
    }

    void Interact()
    {
        enableTrainingMode = !enableTrainingMode;
        if (enableTrainingMode)
        {
            tileMaterial.SetTexture("_FaceTex", trainingTexture);
            GetComponent<MeshRenderer>().sharedMaterial = onMaterial;
        } else
        {
            tileMaterial.SetTexture("_FaceTex", regularTexture);
            GetComponent<MeshRenderer>().sharedMaterial = offMaterial;
        }

        // at least one person reported that all the tiles were stuck on 1-pei
        // which is the default. Weird, not sure why that would occur.
        ResetMaterialPropertyBlock();
    }

    void ResetMaterialPropertyBlock()
    {
        var props = new MaterialPropertyBlock();
        for (int i = 0; i < tileRoot.childCount; ++i)
        {
            var tile = tileRoot.GetChild(i);
            props.SetFloat("_Tile", int.Parse(tile.gameObject.name.Substring(0,2)));
            tile.gameObject.GetComponent<MeshRenderer>().SetPropertyBlock(props);
        }
    }

}
