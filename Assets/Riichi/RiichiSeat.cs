
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// One of the 4 seats around RiichiGame. Half as a convenience abstraction
/// around all the UI stuff that's the same for each seat, and as a gameobject
/// for udon's ownership tracking. Only players who own one of the seats can
/// move tiles, and the EAST seat is also the 'table owner' who can shuffle (
/// in addition to the instance master).
/// 
/// All the internal game state and syncing logic is still on RiichiGame.
///
/// XXX since the seating actually requires an additional bit of state besides
/// who the owner is, I'm using one UdonSynced bool on here. I'm afraid that'll
/// mess up my Bus setup (where too many UdonSynced behaviors just clog the
/// network). However, as long as it's not changing as rapidly as my tests,
/// maybe these additional bits will be okay.
/// </summary>
public class RiichiSeat : UdonSharpBehaviour
{
    public RiichiGame game;
    public BoxCollider handZone;

    const int EAST = 0, NORTH = 1, WEST = 2, SOUTH = 3;
    public int seat; 

    // all score indicators for this seat
    public Text[] thisPlayerScores;
    public Text seatStateIndicator;

    [UdonSynced]
    public bool playerSeated = false;

    float updateWait = 0;

    void Update()
    {
        if ((updateWait -= Time.deltaTime) > 0) return;
        updateWait = 0.2f;
        string score = game.scores[seat].ToString();
        foreach (Text t in thisPlayerScores)
        {
            t.text = score;
        }
        seatStateIndicator.text = playerSeated ? OwnerName() : "Seat Open";
    }

    private string OwnerName()
    {
        var p = Networking.GetOwner(gameObject);
        if (p != null) return p.displayName;
        return "Editor";
    }

    // called by UI buttons
    public void SortHand()
    {
        if (Networking.IsOwner(gameObject) && playerSeated)
        {
            game.SortHand(seat);
        }
    }

    public void TakeSeat()
    {
        if (playerSeated) return; // can't take over
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        playerSeated = true;
        Update();
    }

    public void ReleaseSeat()
    {
        if (playerSeated && (Networking.IsOwner(gameObject) || Networking.IsMaster))
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            playerSeated = false;
            Update();
        }
    }

    private void AdjustScore(int delta)
    {
        if (Networking.IsOwner(gameObject) && playerSeated)
        {
            game.AdjustScore(seat, delta);
            Update();
        }
    }

    public void ScoreUp100() { AdjustScore(100); }
    public void ScoreDown100() { AdjustScore(-100); }
    public void ScoreUp1000() { AdjustScore(1000); }
    public void ScoreDown1000() { AdjustScore(-1000); }
    public void ScoreUp10000() { AdjustScore(10000); }
    public void ScoreDown10000() { AdjustScore(-10000); }

}
