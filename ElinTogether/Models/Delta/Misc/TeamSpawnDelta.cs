using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     From the host to a player who just came in (<see cref="Teams" />): a new player, or one whose team has no
///     base, starts at the host's spawn zone; a player whose link dropped while it held a map alone goes back to it;
///     a player coming back to the world goes back to the map and tile it left from (host box ReturnWhereLeft).
///     Its game travels there the way a player travels alone
/// </summary>
[MessagePackObject]
public class TeamSpawnDelta : ElinDelta
{
    [Key(0)]
    public required int ZoneUid { get; init; }

    /// <summary>
    ///     The tile to stand on there, -1 for wherever the game puts a player walking in
    /// </summary>
    [Key(1)]
    public int X { get; set; } = -1;

    [Key(2)]
    public int Z { get; set; } = -1;

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return "not a request";
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetClient { IsZoneSession: false } || NetSession.Instance.IsAway ||
            game.spatials.Find(ZoneUid) is not Zone zone || zone == _zone) {
            return;
        }

        EmpLog.Information("Going to {ZoneFullName}, as the host says", zone.ZoneFullName);
        if (X < 0 || Z < 0) {
            pc.MoveZone(zone, ZoneTransition.EnterState.Auto);
            return;
        }

        // a map: the game stands the player on that tile, or the nearest free one when it is blocked
        // (Zone.AddGlobalCharasOnActivate). The world map puts whoever walks in on the tile of the map it comes out
        // of (Zone.GetSpawnPos): the tile is taken once there, see LeaveSpotArrival
        if (zone.IsRegion) {
            Patches.LeaveSpotArrival.Expect(zone.uid, X, Z);
            pc.MoveZone(zone, ZoneTransition.EnterState.Auto);
        } else {
            pc.MoveZone(zone, new ZoneTransition {
                state = ZoneTransition.EnterState.Exact,
                x = X,
                z = Z,
            });
        }
    }
}
