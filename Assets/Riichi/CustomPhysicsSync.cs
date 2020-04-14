
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
    public float syncIntervalSecs = 5f;

    private Component[] tiles;
    private float lastSync = 0;

    // master will have full control now.
    [UdonSynced] public string transforms;

    void Start()
    {
        // GetComponentsInChildren includes "this", annoying
        var tilesAndThis = transform.GetComponentsInChildren(typeof(UdonBehaviour));
        tiles = new Component[136];
        for (int i = 0; i < 136; ++i)
        {
            tiles[i] = tilesAndThis[i + 1];
        }
        Debug.Log($"my player id: {VRCPlayerApi.GetPlayerId(Networking.LocalPlayer)}");
    }

    void FixedUpdate()
    {
        lastSync += Time.deltaTime;
        if (lastSync > syncIntervalSecs)
        {
            lastSync = 0;
            if (Networking.IsMaster)
            {
                // I think sending an event makes the assembly for FixedUpdate more efficient
                // compared to actually calling the method. dunno for sure
                SendCustomEvent("DoSync");
            } else
            {
                SendCustomEvent("ReadSync");
            }
        }
    }

    public void DoSync()
    {
        var s = "";
        for (int i = 0; i < 2; ++i)
        {
            var p = tiles[i].transform.position;
            var r = tiles[i].transform.rotation;
            s += $"{p.x:F4},{p.y:F4},{p.z:F4},{r.w:F4},{r.x:F4},{r.y:F4},{r.z:F4}|";
        }
        transforms = s;
        Debug.Log($"write: {transforms}");
    }
    public void ReadSync()
    {
        if (transforms.Length == 0) return;
        Debug.Log($"read: {transforms}");
        var ts = transforms.Split('|');
        for (int i = 0; i < 2; ++i)
        {
            var t = ts[i].Split(',');
            var p = new Vector3(float.Parse(t[0]), float.Parse(t[1]), float.Parse(t[2]));
            var r = new Quaternion(float.Parse(t[3]), float.Parse(t[4]), float.Parse(t[5]), float.Parse(t[6]));

            Debug.Log($"updating {tiles[i].gameObject.name} to {p} and {r}");
            tiles[i].transform.position = p;
            tiles[i].transform.rotation = r;
        }
    }

}
