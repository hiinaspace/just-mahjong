
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class SyncPhysics : UdonSharpBehaviour
{
    public Transform tileParent;
    Transform[] tiles;

    public Material onMaterial;
    public Material offMaterial;

    [UdonSynced] private bool physicsOn = true;
    private bool localPhysicsOn = true;

    void Start()
    {
        tiles = new Transform[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tileParent.GetChild(i);
        }
    }

    void Interact()
    {
        if (Networking.IsMaster)
        {
            physicsOn = !physicsOn;
        }
    }

    void FixedUpdate()
    {
        if (localPhysicsOn != physicsOn)
        {
            localPhysicsOn = physicsOn;
            TogglePhysics();
        }
    }

    void TogglePhysics()
    {
        if (physicsOn)
        {
            Debug.Log("turning physics colliders and gravity back on");
            foreach (Transform t in tiles)
            {
                Debug.Log($"tile {t.gameObject.name}");
                //t.gameObject.GetComponent<BoxCollider>().enabled = true;
                var r = t.gameObject.GetComponent<Rigidbody>();
                // r.isKinematic = false; XXX doesn't work with udon sync position
                r.detectCollisions = true;
                r.drag = 10;
                //r.useGravity = true; XXX also doesn't work
                // but!
                t.gameObject.GetComponent<ConstantForce>().enabled = false;
                Debug.Log($"r.isKinematic {r.isKinematic} r.useGravity {r.useGravity}, detectCollisions: {r.detectCollisions}, mass {r.mass}");
            }
            GetComponent<MeshRenderer>().material = onMaterial;
        } else
        {
            Debug.Log("turning physics colliders and gravity off");
            foreach (Transform t in tiles)
            {
                Debug.Log($"tile {t.gameObject.name}");
               // t.gameObject.GetComponent<BoxCollider>().enabled = false;
                var r = t.gameObject.GetComponent<Rigidbody>();
                r.detectCollisions = false;
                r.drag = 100;
                //r.useGravity = false;
                // poor man's toggle gravity
                t.gameObject.GetComponent<ConstantForce>().enabled = true;
                Debug.Log($"r.isKinematic {r.isKinematic}, r.useGravity {r.useGravity}, detectCollisions: {r.detectCollisions}, mass {r.mass}");
            }
            GetComponent<MeshRenderer>().material = offMaterial;
        }
    }
}
