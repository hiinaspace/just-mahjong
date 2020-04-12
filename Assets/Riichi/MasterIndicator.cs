
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class MasterIndicator : UdonSharpBehaviour
{
    public UnityEngine.UI.Text text;
    private float lastUpdate = 0;
    
    void Update()
    {
        lastUpdate += Time.deltaTime;
        if (lastUpdate > 1)
        {
            lastUpdate = 0;
            DoUpdate();
        }

    }

    void DoUpdate()
    {
        // VRCPlayerApi all players is broken
        // https://vrchat.canny.io/vrchat-udon-closed-alpha-bugs/p/vrcplayerapi-getallplayers-is-broken-definetion
        var own = Networking.GetOwner(gameObject);
        if (own != null)
        {
            text.text = $"Current Master: {own.displayName}";
        } else
        {
            text.text = $"Current Master: unknown";
        }
    }
}
