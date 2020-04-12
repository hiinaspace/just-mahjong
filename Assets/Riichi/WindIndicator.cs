
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class WindIndicator : UdonSharpBehaviour
{
    void Interact()
    {
        transform.Rotate(new Vector3(0, 90, 0));
    }
}
