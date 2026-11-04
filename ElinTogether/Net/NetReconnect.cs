using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     A guest whose link with the host dropped by itself joins the same game again by itself, with a notice on
///     screen the player can cancel. Lives outside the connection component, which dies with the link
/// </summary>
internal static class NetReconnect
{
    private const float RetrySeconds = 5f;
    private const float GiveUpSeconds = 180f;

    /// <summary>
    ///     A host that said it leaves is waited for that long only: a world reloaded comes back in that time, a game
    ///     closed for good does not, and its guests waited 3 minutes on "Going back into the game" (two real PCs,
    ///     2026-10-27)
    /// </summary>
    private const float HostLeftSeconds = 45f;

    private const float HostLeftFirstTry = 10f;

    /// <summary>
    ///     Council 9: a host gone without a word is waited for that long before a guest takes the world over (a
    ///     line cut for a moment comes back by itself, and nothing is lost)
    /// </summary>
    internal static float TakeoverSeconds = 60f;

    private static Coroutine? _routine;
    private static Dialog? _notice;

    internal static bool Active => _routine is not null;

    /// <summary>
    ///     To call before the session is reset: where the game was is read from the connection going away
    /// </summary>
    internal static void Begin(bool hostLeft = false)
    {
        var session = NetSession.Instance;

        // only for a player who was in the game: a join that fails is not a link lost
        if (Active || WorldTakeover.Busy || PeerKeeper.Claiming || !session.Rules.AllowReconnect || !EClass.core.IsGameStarted ||
            session.Transport is not ElinNetClient { IsZoneSession: false } client ||
            client.Rejoin is not { } rejoin) {
            return;
        }

        EmpLog.Information("Link with the host lost, joining the same game again");
        // the world comes back from the host, which ties the rides with another player again on its side; this game
        // loads that world before, without them: tied again here once back (ride2 cut_home, 2026-10-26)
        _ties = EClass.pc is { } pc ? CompanionHelper.PlayerRideTies(pc) : null;

        _routine = EmpMod.Instance.StartCoroutine(Retry(rejoin, !client.IsLocalConnection && !client.IsDirectConnection,
            session.Rules.AllowTakeover, hostLeft));
    }

    /// <summary>
    ///     No more attempts, the one under way is left alone
    /// </summary>
    internal static void Stop()
    {
        if (_routine is not null) {
            EmpMod.Instance.StopCoroutine(_routine);
        }

        Clear();
    }

    private static void Clear()
    {
        _routine = null;
        WorldHandover.Followed();
        // (however it ends: given up, cancelled, stopped by a takeover or a world started from the title)
        UnpatchIfNoSession();

        var notice = _notice;
        _notice = null;
        if (notice != null) {
            notice.Close();
        }
    }

    private static IEnumerator Retry(Action rejoin, bool byLobby, bool takeover, bool hostLeft)
    {
        var session = NetSession.Instance;
        var lost = Time.realtimeSinceStartup;
        // the host said it left: no wait for it, the turns run from now
        var wait = WorldHandover.Following ? 0f : TakeoverSeconds;
        // (with a takeover: the wait for the host, then a turn for each guest, then the time to join that game)
        var giveUp = lost + (hostLeft && !takeover ? HostLeftSeconds : GiveUpSeconds) +
                     (takeover ? wait + WorldHandover.TurnSeconds * WorldHandover.Turns : 0f);
        // the host said it leaves: its first join again waits a little. Asked at once, the host still closing its game
        // (a reload) kept the port busy and could not open its session again (ride2 reload, 2026-10-27)
        var tried = hostLeft ? lost + HostLeftFirstTry - RetrySeconds : float.MinValue;
        var back = false;

        // whatever happens in here, no notice left on screen and nothing left that reads as still trying
        try {
            // the caller resets the session right after Begin: the title screen is up on the next frame
            yield return null;

            try {
                ShowNotice(hostLeft);
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Reconnect: the notice could not be shown");
            }

            // a hard end: a join that connects but never loads the world counts in the same time
            while (Time.realtimeSinceStartup < giveUp) {
                yield return null;

                // one try that throws is one try lost, never the routine (robustness audit 2026-11-01): it ended with
                // the session left as it was and no word to the player
                int step;
                try {
                    step = Step();
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Reconnect: this try failed, the next one comes");
                    tried = Time.realtimeSinceStartup;
                    continue;
                }

                if (step == StepBack) {
                    back = true;
                    yield break;
                }

                if (step == StepWait) {
                    continue;
                }

                if (step == StepResetFirst) {
                    try {
                        // the component closes its link at the end of the frame, which resets the session once more
                        session.ResetSession();
                    } catch (Exception ex) {
                        EmpLog.Warning(ex, "Reconnect: the session could not be reset before the next try");
                    }

                    yield return null;
                }

                tried = Time.realtimeSinceStartup;
                Attempt(rejoin, byLobby);
                // Attempt can freeze for 20-34 s (plugin patching); count the wait from its end, not its start
                tried = Time.realtimeSinceStartup;
            }
        } finally {
            try {
                Clear();
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Reconnect: the notice could not be closed");
            }
        }

        if (back) {
            yield break;
        }

        EmpLog.Information("Gave up joining the game again after {Seconds}s", Time.realtimeSinceStartup - lost);

        // always both: the player is never left on a half-open session without a word
        try {
            session.ResetSession();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Reconnect: the session could not be reset after giving up");
        }

        Dialog.Ok(hostLeft ? "emp_ui_host_closed" : "emp_ui_reconnect_failed");
        yield break;

        // what one turn of the loop decides, without waiting
        int Step()
        {
            // back in the game, or the player started something else
            if (EClass.core.IsGameStarted || session.Transport is ElinNetHost) {
                PeerKeeper.LinkBack();
                if (_ties is { Count: > 0 } ties && session.Transport is ElinNetClient) {
                    EmpMod.Instance.StartCoroutine(TieAgainOnceHere(ties));
                }

                return StepBack;
            }

            var now = Time.realtimeSinceStartup;
            if (now - tried < RetrySeconds || PeerKeeper.Claiming) {
                return StepWait;
            }

            // the host did not come back: the guest whose turn it is opens the world from its copy, the others
            // look for that game (it said nothing before going: a crash, a line cut for good)
            // (also when the guest whose turn it was did not open it in its time: the next one does)
            // (a peer-hosted world kept with the world's store: its lease says when the keeper is gone, and the
            // keeper's own list who takes it, see PeerKeeper)
            if (takeover && PeerKeeper.WhileLost()) {
                // the election handles it; the attempts below join the keeper that comes back, or the new one
            } else if (takeover && now - lost >= wait && !WorldTakeover.Busy) {
                if (!WorldHandover.IsMyTurn(now - lost - wait)) {
                    WorldHandover.Follow();
                } else if (WorldTakeover.Begin() is { Length: > 0 } why) {
                    EmpLog.Warning("The host is gone and the world is not taken over: {Why}", why);
                    takeover = false;
                } else {
                    // (the takeover ends this routine itself, see WorldTakeover)
                    EmpLog.Information("The host did not come back in {Seconds}s: taking the world over", now - lost);
                    EmpPop.Information("emp_takeover_begin".lang());
                }
            }

            if (session.Transport is ElinNetClient client) {
                // connected: the join is under way. Through a lobby nothing is connected yet while Steam
                // lets us in and the key is handed over: that attempt gets the time of any request
                var direct = client.IsLocalConnection || client.IsDirectConnection;
                if (client.IsConnected || (!direct && now - tried < EmpConfig.Policy.Timeout.Value)) {
                    return StepWait;
                }

                return StepResetFirst;
            }

            return StepTry;
        }
    }

    private const int StepWait = 0;
    private const int StepBack = 1;
    private const int StepResetFirst = 2;
    private const int StepTry = 3;

    private static void Attempt(Action rejoin, bool byLobby)
    {
        try {
            // the host said it left and another guest opens the world: its lobby is the one to join, the old one
            // is gone. By a port or an address the new host listens where the old one did
            if (byLobby && WorldHandover.Following) {
                if (WorldHandover.NewHostLobby() is var lobby and not 0) {
                    EmpLog.Information("Joining the game of the player that took the world over, lobby {LobbyId}", lobby);
                    NetSession.Instance.Lobby.ConnectLobby(lobby);
                }

                return;
            }

            rejoin();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not start joining the game again");
            // noexcept
        }
    }

    private static List<(int Rider, int Carried, bool Parasite)>? _ties;

    private static IEnumerator TieAgainOnceHere(List<(int Rider, int Carried, bool Parasite)> ties)
    {
        _ties = null;
        var until = Time.realtimeSinceStartup + 60f;
        // (the world loads, then the map, then the other players stand on it)
        while (Time.realtimeSinceStartup < until) {
            yield return new WaitForSeconds(1f);
            if (NetSession.Instance.Transport is not ElinNetClient { IsConnected: true } || EClass._map is null ||
                !EClass.core.IsGameStarted) {
                continue;
            }

            if (ties.All(t => EClass._map.FindChara(t.Rider) is not null && EClass._map.FindChara(t.Carried) is not null)) {
                CompanionHelper.TieAgain(ties);
                yield break;
            }
        }
    }

    private static void ShowNotice(bool hostLeft)
    {
        var cancel = false;
        // (the host said it leaves: waiting for its game, not for a link)
        var notice = _notice = Dialog.Choice(hostLeft ? "emp_ui_host_left_waiting" : "emp_ui_reconnecting",
            d => d.AddButton("emp_ui_reconnect_cancel".lang(), () => cancel = true));
        notice.SetOnKill(() => {
            // closed by Clear
            if (!ReferenceEquals(_notice, notice)) {
                return;
            }

            // closed by the player, a way out. Closed by anything else once the game answers again
            // (the world loading): the join goes on
            _notice = null;
            if (cancel || NetSession.Instance.Transport is not ElinNetClient { IsConnected: true }) {
                EmpLog.Information("Player gave up joining the game again (by the button {Cancel}, transport {Transport})",
                    cancel, NetSession.Instance.Transport?.GetType().Name ?? "none");
                Stop();
                NetSession.Instance.ResetSession();
            }
        });
    }

    /// <summary>
    ///     The session's patches stay while the guest joins again (ElinNetBase.OnDestroy): taken away a frame after
    ///     it stops if no session is left, as when a session closes. Kept when a session is there or about to be (its
    ///     component is destroyed at the end of the frame and finds them gone otherwise)
    /// </summary>
    private static void UnpatchIfNoSession()
    {
        if (!EmpMod.PatchesPerSession || Active || !HarmonyLib.Harmony.HasAnyPatches(ModInfo.Guid)) {
            return;
        }

        // (the component of the last try is destroyed at the end of this frame: it takes them away itself)
        EmpMod.Instance.StartCoroutine(Later());

        static IEnumerator Later()
        {
            yield return null;
            var session = NetSession.Instance;
            if (!Active && session.Transport == null && session.ZoneSession == null && HarmonyLib.Harmony.HasAnyPatches(ModInfo.Guid)) {
                EmpLog.Information("No session after joining again was given up: the session's patches are taken away");
                EmpMod.SharedHarmony.UnpatchSelf();
            }
        }
    }
}
