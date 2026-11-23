using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Council 2026-10-23: a player on the host's world map took a step (or an hour of its express travel) that
///     moves the date, as the host's own would. Its game does not own the date: the host moves it, after
///     checking the step is one the game can make, and tells everyone with the traveller's name
/// </summary>
[MessagePackObject]
public class WorldTravelStepDelta : ElinDelta
{
    [Key(0)]
    public required int Minutes { get; init; }

    /// <summary>
    ///     Personal Pace: the step was taken on the player's own copy of the world map, away from the host. The
    ///     host keeps what it holds as the lead and tells the player the rest, which its own game passes and
    ///     reports as in the Shared Clock (WorldTimeReportDelta, checked there)
    /// </summary>
    [Key(1)]
    public bool Away { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host || !FromPeer) {
            return;
        }

        // the owner's admin character travels without moving the world's date (Helper.OwnerAdmin)
        if (Helper.OwnerAdmin.IsPeer(host, OriginPeer)) {
            return;
        }

        if (Minutes is <= 0 or > WorldTravelClock.MaxStepMinutes) {
            EmpLog.Warning("World time: player {Peer} reported a step of {Minutes} minute(s), refused",
                OriginPeer, Minutes);
            return;
        }

        if (Away) {
            var (heldAway, leadAway) = host.HoldLeadOfPeer(OriginPeer, Minutes);
            host.SendDeltaTo(OriginPeer, new PlayerLeadDelta {
                Lead = leadAway,
                Held = heldAway,
                Excess = Minutes - heldAway,
            });
            return;
        }

        // the player saw everyone on the world map, the host no longer does (someone just left): no hours
        if (NetSession.Instance.Rules.WorldMapTime == WorldMapTime.TravelTogether && !host.AllOnWorldMap()) {
            EmpLog.Debug("World time: step of player {Peer} while not everyone is on the world map, no hours",
                OriginPeer);
            return;
        }

        // Personal Pace: what the world waits for is this player's lead, it paid it with its turns and its quests
        var granted = WorldTravelClock.Plausible(OriginPeer, Minutes, "travel");
        var (held, lead) = host.HoldLeadOfPeer(OriginPeer, granted);
        if (held > 0) {
            host.SendDeltaTo(OriginPeer, new PlayerLeadDelta {
                Lead = lead,
                Held = held,
            });
        }

        var moves = WorldTravelClock.Step(OriginPeer, granted - held);
        if (moves > 0) {
            WorldDateAdvanceEvent.CatchUpNextFrame(WorldDateAdvanceEvent.AfterPending(moves), host.TravellerOf(OriginPeer));
        }
    }
}

/// <summary>
///     Council 2026-10-23, Personal Pace, host to a player: its lead on the world (minutes), and for its step the
///     part of it held back (its own quests pay it) and, away from the host, the part its game passes itself
/// </summary>
[MessagePackObject]
public class PlayerLeadDelta : ElinDelta
{
    [Key(0)]
    public required int Lead { get; init; }

    [Key(1)]
    public int Held { get; init; }

    [Key(2)]
    public int Excess { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost) {
            return;
        }

        if (Held > 0) {
            WorldMapLead.Held(Held, Lead);
        } else {
            WorldMapLead.Set(Lead);
        }

        if (Excess is > 0 and <= WorldTravelClock.MaxStepMinutes) {
            // not from inside the delta loop, like any time this game passes
            var excess = Excess;
            core.actionsNextFrame.Add(() => RemoteTravelRegionPatch.AdvanceAway(excess));
        }
    }
}

/// <summary>
///     Council 2026-10-23, Travel Together: whether every player is on the world map, as the host sees it. A
///     player's step moves the date only then, whoever steps; its game needs to know it before stepping
/// </summary>
[MessagePackObject]
public class WorldMapTogetherDelta : ElinDelta
{
    /// <summary>
    ///     Last told by the host
    /// </summary>
    internal static bool AllOnWorldMap { get; private set; }

    [Key(0)]
    public required bool All { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (!net.IsHost) {
            AllOnWorldMap = All;
        }
    }
}
