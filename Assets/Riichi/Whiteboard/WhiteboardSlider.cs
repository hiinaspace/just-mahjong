
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

public class WhiteboardSlider : UdonSharpBehaviour
{

    public Material markerMat;
    public Slider slider;
    public Image background;

    [UdonSynced] float hue;

    void Start()
    {
        SetColor();
    }

    public void UpdateColor()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);

        hue = slider.value;

        SetColor();
    }

    void SetColor()
    {
        slider.value = hue; // to sync UI element on remote.
        markerMat.color = Color.HSVToRGB(hue, 1, 1);
        background.color = Color.HSVToRGB(hue, 1, 1);
    }

    public override void OnDeserialization()
    {
        SetColor();
    }
}
