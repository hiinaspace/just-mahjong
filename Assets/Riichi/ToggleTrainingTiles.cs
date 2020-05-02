
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

    bool enableTrainingMode = false;

    void Start()
    {
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
    }

}
