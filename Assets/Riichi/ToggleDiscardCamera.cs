
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

public class ToggleDiscardCamera : UdonSharpBehaviour
{
    public Toggle toggle;
    public GameObject discardCamera;
    public GameObject discardCameraQuad;
    public Transform spawn;
    public Transform discardCameraLoc;

    public void DoToggle()
    {
        if (toggle.isOn)
        {
            discardCamera.SetActive(true);
            discardCameraQuad.SetActive(true);
            discardCamera.transform.SetPositionAndRotation(discardCameraLoc.position, discardCameraLoc.rotation);
            discardCameraQuad.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
        } else
        {
            discardCamera.SetActive(false);
            discardCameraQuad.SetActive(false);
        }
    }
}
