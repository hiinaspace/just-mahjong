using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class DiscardCameraEditor : MonoBehaviour
{
    public Shader shader;

    void Awake()
    {
        var c = GetComponent<Camera>();
        c.SetReplacementShader(shader, "RiichiTile");
    }

}
