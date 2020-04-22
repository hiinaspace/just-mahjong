
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class DiscardCameraReplacement : UdonSharpBehaviour
{
    public Shader shader;
    void Start()
    {
        var c = GetComponent<Camera>();
        c.SetReplacementShader(shader, "RiichiTile");

    }
}
