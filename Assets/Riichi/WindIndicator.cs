
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class WindIndicator : UdonSharpBehaviour
{
    // XXX udon's synced position is awful at updating in any timely manner
    // probably getting starved by the other synced variables i guess.
    [UdonSynced] Quaternion rot = Quaternion.identity;

    void Start()
    {
        rot = transform.rotation;
    }

    void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        transform.Rotate(new Vector3(0, 0, 270));
        rot = transform.rotation;
    }

    public override void OnDeserialization()
    {
        transform.rotation = rot;
    }
}
