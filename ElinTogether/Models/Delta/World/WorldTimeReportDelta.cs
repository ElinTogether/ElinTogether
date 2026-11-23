using System.Collections.Immutable;
using System.Linq;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A player who simulates a map away from the host tells the date its own game reached. The world has one
///     date: the most advanced one. The host catches up and tells everyone, see <see cref="WorldDateAdvanceDelta" />
/// </summary>
[MessagePackObject]
public class WorldTimeReportDelta : ElinDelta
{
    [Key(0)]
    public required ImmutableArray<int> GameDate { get; init; }

    /// <summary>
    ///     The date moved by a trip of this player on its own copy of the world map (council 2026-10-23): the
    ///     others are told who travelled
    /// </summary>
    [Key(1)]
    public bool Travel { get; init; }

    /// <summary>
    ///     Only a game that simulates a map of its own has a clock of its own, and a trip is made on the world map
    ///     (security audit 2026-10-27, item 10: any player made days pass at the express travel's rate). On its own
    ///     map, hours pass at the rate of a rest, see WorldTravelClock.StayFits
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (!NetSession.Instance.Rules.UseSharedWorldTime || PlayerIdle.IsIdle(OriginPeer)) {
            return null;
        }

        var held = host.LeasedZones(OriginPeer).ToList();
        if (!host.IsAwayPeer(OriginPeer) || held.Count == 0) {
            return "the time of a player who simulates no map of its own";
        }

        if (Travel) {
            return held.Any(zone => zone is { IsRegion: true }) ? null : "travel time from a player off the world map";
        }

        var ahead = WorldDateAdvanceEvent.Minutes([..GameDate]) - WorldDateAdvanceEvent.AfterPending(0);
        return WorldTravelClock.StayFits(OriginPeer, ahead) ? null : $"{ahead} minute(s) passed at once on its own map";
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host || !NetSession.Instance.Rules.UseSharedWorldTime) {
            return;
        }

        // away from the keyboard: the clock of its map runs on its own, it does not pull the world's date
        // (council 2026-10-26). A trip is an act of its own, which ends its idleness anyway
        // nor does the owner's admin character move it (Helper.OwnerAdmin)
        if (PlayerIdle.IsIdle(OriginPeer) || Helper.OwnerAdmin.IsPeer(host, OriginPeer)) {
            return;
        }

        // audit 2026-10-23: the host took any date a player sent, a month ahead at once. It believes what
        // travel can make pass since what it already has (what waits to be caught up included)
        var from = WorldDateAdvanceEvent.AfterPending(0);
        var ahead = WorldDateAdvanceEvent.Minutes([..GameDate]) - from;
        var granted = WorldTravelClock.Plausible(OriginPeer, ahead, "time away");
        if (granted <= 0) {
            return;
        }

        WorldDateAdvanceEvent.CatchUpNextFrame(from + granted, Travel ? host.TravellerOf(OriginPeer) : null);
    }
}
