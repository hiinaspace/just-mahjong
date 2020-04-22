
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class WalkHierarchy : UdonSharpBehaviour
{
    float time = 6;
    void Update()
    {
        time += Time.deltaTime;
        if (time < 5) return;
        time = 0;
        // try to get player
        var players = Physics.OverlapBox(Vector3.zero, Vector3.one * 100, Quaternion.identity);
        Debug.Log($"player collision: {players.Length}");
        for (int i = 0; i < players.Length; ++i)
        {
            var p = players[i].gameObject;
            Debug.Log($"player {i} gameobject is {p.name}");
            var parent = p.transform;
            while (parent.parent != null)
            {
                Debug.Log($"found parent: {parent.gameObject.name}");
                parent = parent.parent;
            }

            // now walk state;
            var stack = new Transform[2048];
            stack[0] = parent;
            var s = 1;
            while (s > 0)
            {
                var t = stack[--s];
                var tc = t.gameObject.GetComponents(typeof(Component));
                Debug.Log($"{t.gameObject.name} has {tc.Length} components");
                foreach (var co in tc)
                {
                    Debug.Log($"{t.gameObject.name} has Component {co.GetType()}");
                }
                for (int j = 0; j < t.childCount; ++j)
                {
                    var c = t.GetChild(j);
                    Debug.Log($"{c.gameObject.name} is child of {t.gameObject}");
                    stack[s++] = c;
                }
                Debug.Log($"visited {t.gameObject}");
            }

        }
    }
}
