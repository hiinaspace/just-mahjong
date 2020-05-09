
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
    ///
    /// Instead, the ownership is implicit in this slightly odd implementation
    /// of Lamport clocks for each tile. Each time the tile is detected moved
    /// locally the clock is incremented (modulo 256). The clock is transmitted
    /// in packets for each tile along with the rest of the information. On
    /// packet receive, compare the local clock of the tile vs the received
    /// clock. If the received clock is greater than the local clock and less
    /// than (local clock + 128) % 256, then our move of the tile happened
    /// before the move of the remote player (they saw our move); set local
    /// tile clock to that value and move the tile to their position;
    /// otherwise, we probably moved our tile after them, so ignore (could be a
    /// retransmitted packet).
    /// 
    /// The modular windowing means that if a player somehow misses packets and
    /// is their clock is 128 behind (modularly), then our packets will start
    /// looking like the past to them and their packets in our future. this
    /// should be fine in normal play though, as you won't move a tile 128
    /// times before a successful transmission; 1 extra byte per tile is
    /// feasible in our tiny packets as well.
    /// 
    /// if two players do mess with the same packet, their packets will
    /// race and observers will get out of sync. They'll get back into sync
    /// once the players acquiesce though, and the DoResync happens.
    /// 
    /// So ownership is implicit as the player who has the highest clock,
    /// because their transmissions will win over remote players when they
    /// transmit and players only transmnit if they detect local changes.
    /// 
    /// However, besides the clock, it's worth talking about the detect
    /// local changes thing, which heavily relies on whether the tile
    /// rigidbody isKinematic or not. Kinematic tiles are also "remote"
    /// aka unowned tiles. When you explicitliy pick up a tile or it
    /// gets sorted into your hand, it becomes unkinematic aka owned,
    /// then the local move detector will pick up the changes, increment
    /// the counter, mark tile dirty, etc. If it's kinematic, the move
    /// detector skips it entirely. When we get a packet with a higher
    /// clock than local, then we mark the tile kinematic (if it wasn't
    /// already), then it gets frozen in the remote position, which is
    /// also good since we're only transmimtting position (not velocity)
    /// so the floating tiles would drop to the floor otherwise.
    ///
    /// So overall, ownership is basically combination of having the highest
    /// clock and the tile being non-kinematic.
    ///
    /// shuffles are handled specially outside the clock system. If a shuffle
    /// comes in where the per-game clock is higher, then all tiles become
    /// effectively owned by the table owner and reset.
    /// 
    /// Special sentinel value -1 is always lower than any clock (ignoring
    /// modular wraparound). On shuffle for new game clocks sets this so the
    /// deal position is always the absolute lowest clock.
    /// </summary>
    public int clock = -1;

    // TODO program structure wise, should move a lot of the logic of
    // RiichiGame into each tile, instead of all those parallel arrays.

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
        r.isKinematic = true;

        // sentinel for "earliest clock possible"
        clock = -1; 

        // visual indicator of shuffle state
        SetBackColorOffset(new Color(0, 0.1f, 0));
    }

    public void TakeCustomOwnership()
    {
        r.isKinematic = false;

        clock = (clock + 1) % 256;

        // visual indicator of ownership
        SetBackColorOffset(Color.blue);
    }

    public void ReleaseCustomOwnership(int packetTileClock)
    {
        r.isKinematic = true;
        // visually reset ownership
        SetBackColorOffset(Color.black);

        // ratchet forward
        clock = packetTileClock;
    }

    public bool IsCustomOwnedAndNotInDealPosition()
    {
        return pickup.IsHeld || !r.isKinematic;
    }

    const int LT = -1, EQ = 0, GT = 1;

    // whether the packetClock is newer than our local clock, modulo 256
    // -1 for less than, 1 for greater than, 0 for equal
    int compareClock(int packetClock)
    {
        if (packetClock == clock) return EQ;
        if (clock == -1) return GT; // all clocks are newer than shuffle sentinel
        // I think this modular math works out right for modular >
        // with the wraparound centered at our clock (more than 128 ahead of us,
        // consider it less than)
        int newestConsidered = clock + 128;
        int nextNow = clock + 256;
        int nextPacketClock = packetClock + 256;
        if ((nextPacketClock < newestConsidered) || (nextPacketClock > nextNow)) {
            return GT;
        } else
        {
            return LT;
        }
    }

    public bool IsNewerRemoteTile(int packetTileClock)
    {
        // we're not currently holding it and we haven't moved it after the remote packet
        // bias clock quality to saying it's a remote tile; that way if they send us the
        // same packet again we'll at least go and try to update our local state again
        return !pickup.IsHeld && compareClock(packetTileClock) != LT;
    }

    public void SetBackColorOffset(Color color)
    {
        props.SetColor("_BackColorOffset", color);
        renderer.SetPropertyBlock(props);
    }
}
