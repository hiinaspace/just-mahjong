
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class CustomPhysicsSync : UdonSharpBehaviour
{
    // udon's "synchronize position" is really broken with
    // non-kinematic rigidbodies. Instead, try to sync position manually
    // through synced variables on each tile; this script tries to make the
    // updates reasonably speedy, considering how slow udon execution is.
    // XXX UdonBehavior[] doesn't exist in udon types, so have to cast it dynamically
    public float syncIntervalSecs = 1f;
    public float syncProbability = .1f;
    private Component[] tiles;
    private float lastSync = 0;

    void Start()
    {
        tiles = transform.GetComponentsInChildren(typeof(UdonBehaviour));
        for (int i = 0; i < 136; ++i)
        {
            ((UdonBehaviour)tiles[i]).SendCustomEvent("DoCustomPhysicsSync");
        }
    }

    void FixedUpdate()
    {
        lastSync += Time.deltaTime;
        if (lastSync > syncIntervalSecs)
        {
            lastSync = 0;
            // I think sending an event makes the assembly for FixedUpdate more efficient
            // compared to actually calling the method. dunno for sure
            SendCustomEvent("DoSync");
        }
    }

    public void DoSync()
    {
        // make all the tiles sync to their owner
        for (int i = 0; i < 136; ++i)
        {
            if (Random.value < syncProbability)
            {
                Debug.Log($"forcing sync for tile {tiles[i].gameObject.name}");
                ((UdonBehaviour)tiles[i]).SendCustomEvent("DoCustomPhysicsSync");
            }
        }
    }

}
