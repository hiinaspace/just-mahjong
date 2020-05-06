
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class WhiteboardMarker : UdonSharpBehaviour
{

    public Material markerMat;
    public Transform markerTip;
    public Transform palette;

    void Update()
    {
        var tipInLocal = palette.InverseTransformPoint(markerTip.position);

        if (Mathf.Abs(tipInLocal.z) < 0.05f && Mathf.Abs(tipInLocal.x) <= 0.5f && Mathf.Abs(tipInLocal.y) <= 0.5f)
        {
            SetColor(tipInLocal);
        }
    }

    void SetColor(Vector3 tipInLocal)
    {
        // same logic of ColorPalette shader
        var hue = Mathf.InverseLerp(-0.5f, 0.5f, tipInLocal.x);
        var y = Mathf.InverseLerp(-0.5f, 0.5f, tipInLocal.y); // back to 0-1
        var saturation = Mathf.Min(1, Mathf.Min(y, 1 - y) * 3f);
        var value = Mathf.SmoothStep(0, 1, Mathf.Min(1, y * 2f));

        var c = Color.HSVToRGB(hue, saturation, value);

        markerMat.color = c;
    }
}
