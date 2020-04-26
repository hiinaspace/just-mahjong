
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Channel is a wrapper around the largest possible atomically
/// [UdonSynced]able variables. The Bus behavior manages access to
/// these channels as a shared broadcast medium
/// 
/// Udon will throw errors on either the send or receive size if the
/// [UdonSynced] strings are too large. The size of the state in each Bus was
/// experimentally determined to be the max size possible, when the strings are
/// ascii (7bit).
///
/// Udon will also eventually throw errors if there are too many behaviors with
/// UdonSynced variables in the scene and syncing fails to occur.
/// Experimentally the max number of gameobjects without errors is at about 8,
/// regardless of the amount of synced variables on each gameobject. It's
/// possible that activating/deactivating gameobjects with UdonSynced variables
/// could prevent the errors (by only allowing a few gameobjects to sync at a
/// time). Hopefully the vrchat devs raise the limit of syncable behaviors
/// before that's necessary though.
/// </summary>
public class Channel : UdonSharpBehaviour
{
    [HideInInspector]
    [UdonSynced] public string string0 = "";
    [HideInInspector]
    [UdonSynced] public string string1 = "";

    [HideInInspector]
    public float lastLocalSend = float.MinValue;

    [HideInInspector]
    public string lastLocalString0;

    // a bit of a hack; when this client detects a successful send on this
    // Channel (kept ownership for long enough for broadcast to have happened)
    // we set this ack object back in the Bus for upstream to do something with
    // in practice, this is an int[] array of tiles that were in the data packet
    // to mark clean again.
    [HideInInspector]
    public object localAckObject;
}
