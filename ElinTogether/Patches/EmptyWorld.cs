using System.Linq;
using ElinTogether.Net;

namespace ElinTogether.Patches;

/// <summary>
///     Council 2026-10-26 (MMO world time, stage 3): an empty world freezes. While no player is connected and
///     active, the date of the world stands still: no hour, no day, no waiting or resting passing time. The host
///     counts as a player only when it is not away from the keyboard (PlayerIdle); the game of an always-on server
///     (-empserver) never does. Its world is paused (PauseGame) and GameDate.AdvanceMin does nothing
///     (WorldDateAdvanceEvent), except a catch-up with what a player made pass elsewhere. <br />
///     The same for a map a player holds alone, away from the host: nobody lives it while that player is away
///     from the keyboard, its clock stops too
/// </summary>
internal static class EmptyWorld
{
    internal static bool Frozen
    {
        get {
            var session = NetSession.Instance;
            if (!session.Rules.TimeStopsWhenEmpty || !EClass.core.IsGameStarted) {
                return false;
            }

            // a map of its own (zone lease), no visitor: its clock ran on in real time and, once its player
            // played again, pulled the world's date by those hours (WorldTimeReportDelta)
            if (session is { IsZoneAuthority: true, Transport: ElinNetClient }) {
                // (a visitor may be playing there: its idleness is not known on this side, the map goes on)
                return PlayerIdle.SelfIdle && !(session.ZoneSession is ElinNetHost zone && zone.PeerIds.Any());
            }

            if (session.Transport is not ElinNetHost { IsZoneSession: false }) {
                return false;
            }

            if (!EmpServer.Requested && !PlayerIdle.IsIdle(0)) {
                return false;
            }

            foreach (var player in session.CurrentPlayers) {
                // (the owner's admin character makes no time pass, see Helper.OwnerAdmin)
                if (player.Index != 0 && !PlayerIdle.IsIdle(player.Index) && !player.OwnerAdmin) {
                    return false;
                }
            }

            return true;
        }
    }
}
