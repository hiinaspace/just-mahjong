using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DrawTileGizmo : MonoBehaviour
{
    public Vector3 tileSize = new Vector3(0.0375f, 0.05f, 0.032f);
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, tileSize);
    }
}
