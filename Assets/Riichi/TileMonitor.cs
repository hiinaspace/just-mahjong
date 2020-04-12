
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class TileMonitor : UdonSharpBehaviour
{

    public Shuffle s;
    public UnityEngine.UI.Text text;

    //void Update()
    //{
    //    if (s == null) return;
    //    if (s.shuffleState == null) return;
    //    text.text = $"shuffle state: {s.shuffleState}";
    //}
}
