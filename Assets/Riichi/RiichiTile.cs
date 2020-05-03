
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class RiichiTile : UdonSharpBehaviour
{
    private Rigidbody r;
    private MaterialPropertyBlock props;
    private new MeshRenderer renderer;
    private new BoxCollider collider;
    private VRC_Pickup pickup;

    /// <summary>
    /// udon's native ownership tracking is unreliable and gets out of sync with
    /// the custom networking in RiichiGame.
    /// Instead, we transmit ownership changes implicitly; if the local client reads
    /// a packet with a tile in it, we assume that tile is now owned remotely (by whoever).
    /// Unless: the server timestamp sent with that packet is earlier than we ourselves
    /// moved the tile, or we're currently holding the tile. OnDrop, we update our
    /// locally last seen serverTime (I think it's from the last packet we
    /// received from vrchat servers, possibly plus some local delta), as well
    /// as when we sort the hand or shuffle.
    /// 
    /// XXX isKinematic is also dual duty with 'is in deal position' since the
    /// stacked walls won't work without it.
    /// 
    /// Not the clearest implementation of ownership, but hopefully documented at least.
    /// </summary>
    int lastMovedLocallyServerTime;

    void Start()
    {
        Init();
        
        renderer = GetComponent<MeshRenderer>();

        props = new MaterialPropertyBlock();
        props.SetFloat("_Tile", int.Parse(name.Substring(0, 2)));

        SetBackColorOffset(Color.black);

        r.maxDepenetrationVelocity = 0.1f;

        // disable pickup by default; when you hit a sort tile button you can
        // grab them. Prevents non-players from messing with tiles.
        collider = GetComponent<BoxCollider>();
        collider.enabled = false;
    }

    void Init()
    {
        if (pickup == null) pickup = (VRC_Pickup)(GetComponent(typeof(VRC_Pickup)));
        if (r == null) r = GetComponent<Rigidbody>();
    }

    void OnPickup()
    {
        TakeCustomOwnership();
    }

    void OnDrop()
    {
        TakeCustomOwnership();
    }

    public void TakeOwnershipForShuffle()
    {
        // XXX the stacked walls will vibrate and fall apart unless the tiles
        // are kinematic while in it. Weird with the whole ownership thing, but
        // hopefully encapsulated here.

        r.isKinematic = true;
        // add small fudge factor for tiles to settle;
        lastMovedLocallyServerTime = Networking.GetServerTimeInMilliseconds() + 100;

        // visual indicator of shuffle state
        SetBackColorOffset(new Color(0, 0.1f, 0));
    }

    public void TakeCustomOwnership()
    {
        r.isKinematic = false;

        // add small fudge factor for tiles to settle;
        lastMovedLocallyServerTime = Networking.GetServerTimeInMilliseconds() + 100;

        // visual indicator of ownership
        SetBackColorOffset(Color.blue);
    }

    public void ReleaseCustomOwnership()
    {
        r.isKinematic = true;
        // visually reset ownership
        SetBackColorOffset(Color.black);
    }

    public bool IsCustomOwnedAndNotInDealPosition()
    {
        return pickup.IsHeld || !r.isKinematic;
    }

    public bool IsRemoteTile(int seenInRemotePacket)
    {
        // we're not currently holding it and we haven't moved it after the remote packet
        return !pickup.IsHeld && lastMovedLocallyServerTime < seenInRemotePacket;
    }

    public bool IsCustomOwnedAfterServerTime(int seenServerTimeMillis)
    {
        return pickup.IsHeld || lastMovedLocallyServerTime > seenServerTimeMillis;
    }

    public void SetBackColorOffset(Color color)
    {
        props.SetColor("_BackColorOffset", color);
        renderer.SetPropertyBlock(props);
    }
}
