
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class CubeToggleKinematic : UdonSharpBehaviour
{
    public UdonBehaviour behavior;
    void Interact()
    {
        var r = GetComponent<Rigidbody>();
        r.isKinematic = !r.isKinematic;
        
    }
}
