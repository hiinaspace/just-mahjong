
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class MaskTiles : UdonSharpBehaviour
{
    public Transform tileParent;

    public Material onMat;
    public Material offMat;

    MeshRenderer[] tiles;

    // SEndCustomNetworkEvent is scuffed, so do the hacky broadcast event instead
    [UdonSynced] bool syncedMask = false;
    public bool localMask = false;

    void Start()
    {
        tiles = new MeshRenderer[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tileParent.GetChild(i).gameObject.GetComponent<MeshRenderer>();
        }
    }

    void FixedUpdate()
    {
        // if master changed syncedMask, do the thing
        if (syncedMask != localMask)
        {
            localMask = syncedMask;
            if (syncedMask)
            {
                DoMask();
            } else
            {
                DoUnMask();
            }
        }

    }

    void Interact()
    {
        Debug.Log("trying to toggle mask");
        if (!Networking.IsMaster) return;
        syncedMask = !syncedMask;
    }

    void DoMask()
    {
        Debug.Log("masking all tiles");
        GetComponent<MeshRenderer>().sharedMaterial = offMat;
        var mpb = new MaterialPropertyBlock();
        mpb.SetFloat("_Tile", 37); // empty spot in tile atlas
        foreach (MeshRenderer e in tiles)
        {
            e.SetPropertyBlock(mpb);
        }
    }
    void DoUnMask()
    {
        Debug.Log("unmasking all tiles");
        GetComponent<MeshRenderer>().sharedMaterial = onMat;
        var mpb = new MaterialPropertyBlock();
        foreach (MeshRenderer e in tiles)
        {
            // reset
            mpb.SetFloat("_Tile", Mathf.Floor(float.Parse(e.gameObject.name)));
            e.SetPropertyBlock(mpb);
        }
    }
}
