
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
        transform.Rotate(new Vector3(0, 0, 90));
        rot = transform.rotation;
    }

    float lastUpdate = 0;

    void FixedUpdate()
    {
        lastUpdate += Time.deltaTime;
        if (lastUpdate > 1)
        {
            lastUpdate = 0;
            transform.rotation = rot;
        }
    }
}
