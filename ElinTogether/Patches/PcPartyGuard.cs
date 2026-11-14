using System;
using System.Linq;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     GitHub issue 34: a host that died in a permadeath world (waiting for a revive at the bartender) had lost its own
///     party (pc.party null), and the game threw thousands of errors a second (Player.RefreshEmptyAlly, the stats bar,
///     the party notice, OwnerPartyPatch.Side) until it quit. The game never takes the player out of the party it leads:
///     refused here, with the call stack in the log so the caller can be found. A party lost any other way is given back
///     (the one it leads, kept in Game.parties, or a new one)
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class PcPartyGuard
{
    private static bool _told;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Party), nameof(Party.RemoveMember))]
    [HarmonyPriority(Priority.First)]
    internal static bool OnRemoveMember(Party __instance, Chara c)
    {
        if (c is null || !EClass.core.IsGameStarted || c != EClass.pc || __instance.uidLeader != c.uid) {
            return true;
        }

        EmpLog.Warning("Refused to take the player out of the party it leads: {Stack}", Environment.StackTrace);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.RefreshEmptyAlly))]
    internal static void BeforeRefreshEmptyAlly()
    {
        Repair();
    }

    internal static void Repair()
    {
        if (!EClass.core.IsGameStarted || EClass.pc is not { party: null } pc || EClass.game?.parties is not { } parties) {
            return;
        }

        // (another player's character stands in as "the player" for a moment (RemoteCraft, PersonalQuests...), and an
        // absent player has no party on purpose: only the local player's own character)
        var session = NetSession.Instance;
        if (pc.GetBool("remote_chara") ||
            session.CurrentPlayers.Any(p => p is not null && p != session.Self && p.CharaUid == pc.uid)) {
            return;
        }

        var party = parties.list.Find(p => p is not null && p.uidLeader == pc.uid);
        if (party is null) {
            parties.Create(pc);
        } else {
            party.uidMembers.Remove(pc.uid);
            party.SetMembers();
            party.AddMemeber(pc);
        }

        if (!_told) {
            _told = true;
            EmpLog.Warning("The player had no party: given back ({Kept})", party is null ? "new" : "the one it leads");
        }
    }
}
