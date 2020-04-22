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
            props.SetFloat("_Tile", Mathf.Floor(float.Parse(tile.gameObject.name)));
            tile.gameObject.GetComponent<MeshRenderer>().SetPropertyBlock(props);
        }
    }

}
