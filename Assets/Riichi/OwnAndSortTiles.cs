
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class OwnAndSortTiles : UdonSharpBehaviour
{
    public Transform origin;
    public Vector3 halfExtents;
    public LayerMask tileLayer;

    public override void Interact()
    {
        var tiles = Physics.OverlapBox(origin.position, halfExtents, origin.rotation, tileLayer);
        Debug.Log($"tiles: {tiles.Length}");
        Sort(tiles);
        float z = -7.5f;
        foreach (Collider t in tiles)
        {
            var obj = t.gameObject;
            Debug.Log($"tile {obj.name}");
            Networking.SetOwner(Networking.LocalPlayer, obj);
            obj.transform.parent = origin;
            obj.transform.localPosition = new Vector3(0, 0, z * 0.038f);
            obj.transform.localRotation = Quaternion.Euler(0, 90, 0);
            var r = obj.GetComponent<Rigidbody>();
            r.isKinematic = false; // allow movement
            r.velocity = Vector3.zero; // dunno if this actually works, try to prevent explode

            z += 1f;
        }
    }

    private void Sort(Collider[] tiles)
    {
        Collider swap;
        // finally those Art of Programming books have value
        bool sorted = false;
        while (!sorted)
        {
            sorted = true;
            for (int i = 0; i < tiles.Length - 1; ++i)
            {
                // gameobject names are sorted by tile value and suit
                // since that's more convenient than trying to read values out
                // of some custom udon component
                string tile1 = tiles[i].gameObject.name;
                string tile2 = tiles[i+1].gameObject.name;
                if (tile1.CompareTo(tile2) > 0)
                {
                    swap = tiles[i];
                    tiles[i] = tiles[i+1];
                    tiles[i+1] = swap;
                    sorted = false;
                }
            }
        }
    }
}
