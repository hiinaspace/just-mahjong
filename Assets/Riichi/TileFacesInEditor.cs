using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class TileFacesInEditor : MonoBehaviour
{
    void Awake()
    {
        var props = new MaterialPropertyBlock();
        foreach (Transform tile in transform)
        {
            props.SetFloat("_Tile", int.Parse(tile.gameObject.name.Substring(0,2)));
            tile.gameObject.GetComponent<MeshRenderer>().SetPropertyBlock(props);
        }
    }

}
