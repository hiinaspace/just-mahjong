using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class TenbouFacesInEditor : MonoBehaviour
{
    void Awake()
    {
        var props = new MaterialPropertyBlock();
        foreach (Transform tile in transform)
        {
            props.SetFloat("_Face", int.Parse(tile.gameObject.name.Substring(0, 1)));
            tile.gameObject.GetComponent<MeshRenderer>().SetPropertyBlock(props);
        }
    }
}
