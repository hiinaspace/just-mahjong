using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

public class GenTiles : MonoBehaviour
{
    static string[] tiles = new string[] {
        "aPin1",
        "aPin2",
        "aPin3",
        "aPin4",
        "aPin5",
        "aPin5-Dora",
        "aPin6",
        "aPin7",
        "aPin8",
        "aPin9",
        "bMan1",
        "bMan2",
        "bMan3",
        "bMan4",
        "bMan5",
        "bMan5-Dora",
        "bMan6",
        "bMan7",
        "bMan8",
        "bMan9",
        "cSou1",
        "cSou2",
        "cSou3",
        "cSou4",
        "cSou5",
        "cSou5-Dora",
        "cSou6",
        "cSou7",
        "cSou8",
        "cSou9",
        "dTon",
        "eNan",
        "fShaa",
        "gPei",
        "hHaku",
        "iHatsu",
        "jChun",
    };

    [MenuItem("RiichiHelpers/Generate Gameobjects")]
    static void genGameobjects()
    {
        var tileParent = GameObject.Find("Tiles").transform;
        var n = 0;
        int m = 0;
        var tilePrefab = (GameObject)AssetDatabase.LoadAssetAtPath("Assets/Riichi/tileprefab.prefab", typeof(Object));
        var tileMesh = (Mesh)AssetDatabase.LoadAssetAtPath("Assets/Riichi/tileFbx.fbx/Tile", typeof(Mesh));
        Debug.Log($"tileMesh: {tileMesh}");
        var tileFbx = (GameObject)AssetDatabase.LoadAssetAtPath("Assets/Riichi/tileFbx.fbx", typeof(GameObject));
        Debug.Log($"tileFbx: {tileFbx}");
        var tileMat = (Material)AssetDatabase.LoadAssetAtPath("Assets/Riichi/TileMat", typeof(Material));


        MaterialPropertyBlock props = new MaterialPropertyBlock();
        foreach (string tile in tiles)
        {
            int times = tile.Contains("5") ? (tile.Contains("Dora") ? 1 : 3) : 4;
            for (int i = 1; i <= times; ++i)
            {
                var t = tile.Trim();
                // i think there's something weird about prefabs that break udon;
                // an identical script (TileScript) on a non-prefab works fine toggling isKinematic,
                // but the prefab ones are all fucked
                //GameObject obj = (GameObject)PrefabUtility.InstantiatePrefab(tilePrefab, tileParent);
                GameObject obj = new GameObject();
                obj.transform.parent = tileParent;
                var rb = obj.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                var f = obj.AddComponent<MeshFilter>();
                f.sharedMesh = tileFbx.GetComponent<MeshFilter>().sharedMesh;
                var r = obj.AddComponent<MeshRenderer>();
                r.sharedMaterial = tileFbx.GetComponent<MeshRenderer>().sharedMaterial;
                obj.AddComponent<BoxCollider>();
                var u = obj.AddComponent<UdonBehaviour>();;
                // ok?
                EditorUtility.CopySerialized(tilePrefab.GetComponent<UdonBehaviour>(), u);
                // bad
                u.SynchronizePosition = false;

                u.AllowCollisionOwnershipTransfer = false;
                var p = obj.AddComponent<VRC.SDK3.Components.VRCPickup>();
                EditorUtility.CopySerialized(tilePrefab.GetComponent<VRC.SDK3.Components.VRCPickup>(), p);

                obj.layer = 23; // riichitiles

                obj.name = $"{n:D2}.{i}";

                float rho = (m % 4) * 0.07f + 0.25f;
                float theta = (int)(m / 4) / 34f * 360.0f;
                Debug.Log($"m {m} r {rho} t {theta}");
                var x = rho * Mathf.Cos(Mathf.Deg2Rad*theta);
                var z = rho * Mathf.Sin(Mathf.Deg2Rad*theta);

                obj.transform.localPosition = new Vector3(x, 0.0f, z);
                obj.transform.localRotation = Quaternion.Euler(-90, 0, 90 - theta);
                m++;
            }
            n++;
        }
    }

    [MenuItem("RiichiHelpers/Generate Placements")]
    static void makePlacements()
    {
        var dims = new Vector3(0.040f, 0.032f, 0.054f);
        var parent = GameObject.Find("Placements").transform;
        var rot = Quaternion.Euler(90f, 0, 0);

        int n = 1;
        // hands
        var start = 0.90f;
        var hands = new Vector2[4]
        {
            new Vector2(0f, start),
            new Vector2(start, 0f),
            new Vector2(0f, -start),
            new Vector2(-start, 0f),
        };
        var rots = new Vector3[4]
        {
            new Vector3(90, 0, 0),
            new Vector3(90, -180, -90),
            new Vector3(90,-180,0),
            new Vector3(90, 0, 90),
        };

        for (int j = 0; j < 4; ++j)
        {
            for (int i = 0; i < 13; ++i)
            {
                float x = (j % 2 == 0) ? (6f - i) * dims.x : 0;
                float z = (j % 2 == 0) ? 0 : (6f - i) * dims.x;
                var obj = new GameObject($"Hand-{j}-{i}-{n++}");
                obj.transform.parent = parent;
                obj.AddComponent<DrawTileGizmo>();
                obj.transform.localPosition = new Vector3(
                    hands[j].x + x,
                    .0f,
                    hands[j].y + z);
                obj.transform.rotation = Quaternion.Euler(rots[j]);
            }
        }
        // 84 tiles left
        // dead wall has 14 tiles
        for (int j = 0; j < 2; ++j)
        {
            float y = dims.y * j;
            for (int i = 0; i < 7; ++i)
            {
                float x = i * dims.x - 0.32f;
                var obj = new GameObject($"Dead-{j}-{i}-{n++}");
                obj.transform.parent = parent;
                obj.AddComponent<DrawTileGizmo>();
                obj.transform.localPosition = new Vector3(x, y, 0.7f);
                obj.transform.rotation = rot;
                // dora is flipped, very intelligent
                if (j == 1 && i == 4)
                {
                    obj.transform.rotation = Quaternion.Euler(-90, 0, 0);
                }
            }
        }
        // 70 tiles left
        var wallStart = 0.70f;
        var walls = new Vector2[]
        {
            new Vector2(wallStart, 0f),
            new Vector2(0f, -wallStart),
            new Vector2(-wallStart, 0f),
        };
        var lens = new int[] { 1, 17, 17 };
        for (int j = 0; j < 3; ++j)
        {
            for (int k = 0; k < 2; ++k)
            {
                float y = dims.y * k;
                for (int i = 0; i < lens[j]; ++i)
                {
                    float x = (j % 2 == 1) ? (i - 8f) * dims.x : 0;
                    float z = (j % 2 == 1) ? 0 : (i - 8f) * dims.x;
                    var obj = new GameObject($"Wall-{j}-{i}-{n++}");
                    obj.transform.parent = parent;
                    obj.AddComponent<DrawTileGizmo>();
                    obj.transform.localPosition = new Vector3(walls[j].x + x, y, walls[j].y + z);
                    obj.transform.rotation = Quaternion.Euler(90, 0, (1 + j) * -90);
                }
            }
        }
    }

    [MenuItem("RiichiHelpers/Generate Hand Placements")]
    static void GenerateHandPlacements()
    {
        var dims = new Vector3(0.040f, 0.032f, 0.054f);
        for (int i = 0; i < 4; ++i)
        {
            var parent = GameObject.Find($"HandPlacements{i}").transform;
            for (int j = 0; j < 16; ++j)
            {
                var t = parent.Find($"hand-{i}-{j}");
                GameObject obj;
                if (t == null)
                {
                    obj = new GameObject($"hand-{i}-{j}");
                    obj.transform.parent = parent;
                } else
                {
                    obj = t.gameObject;
                }
                obj.transform.localPosition = new Vector3(dims.x * (6f - j), 0, 0);
                obj.AddComponent<DrawTileGizmo>();
            }
        }
    }
}
