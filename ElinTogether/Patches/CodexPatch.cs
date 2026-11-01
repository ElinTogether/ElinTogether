using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The codex is shared, see <see cref="CodexDelta" />. A card collected, a weak spot read in a book, a card
///     given once, a creature that appeared: known only to the game where it happened, told to everyone. A kill
///     is counted by every game of the map where it happened (the blow is played again in each): the game that
///     keeps that map tells only those who are not on it. Teams learning on their own (<see cref="TeamKnowledge" />):
///     told to the player's team only, and a kill counted only by the games of the killer's team
/// </summary>
[HarmonyPatch]
internal static class CodexPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodexManager), nameof(CodexManager.AddCard))]
    internal static void OnAddCard(CodexManager __instance, string id, int num)
    {
        Tell(__instance, CodexKind.Card, id, num);
    }

    // a card taken out of the codex as a figure: the count goes down by hand, not through AddCard
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ContentCodex), nameof(ContentCodex.OnClickGetCard))]
    internal static void OnGetCard(ContentCodex __instance)
    {
        if (__instance.currentCodex is { numCard: > 0 } creature) {
            Tell(EClass.player.codex, CodexKind.Card, creature.id, -1);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodexManager), nameof(CodexManager.AddWeakspot))]
    internal static void OnAddWeakspot(CodexManager __instance, string id)
    {
        Tell(__instance, CodexKind.Weakspot, id, 1);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodexManager), nameof(CodexManager.AddSpawn))]
    internal static void OnAddSpawn(CodexManager __instance, string id)
    {
        Tell(__instance, CodexKind.Spawn, id, 1);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodexManager), nameof(CodexManager.MarkCardDrop))]
    internal static void OnMarkCardDrop(CodexManager __instance, string id)
    {
        Tell(__instance, CodexKind.CardDrop, id, 1);
    }

    // who deals the blow being settled (Card.DamageHP counts the kill), innermost last
    private static readonly Stack<Card?> _blows = [];

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.DamageHP), typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
        typeof(bool), typeof(Thing), typeof(Chara), typeof(int))]
    internal static void OnBlowBegin(Card origin)
    {
        _blows.Push(origin);
    }

    // a finalizer: a throw does not leave the blow on the stack
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Card), nameof(Card.DamageHP), typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
        typeof(bool), typeof(Thing), typeof(Chara), typeof(int))]
    internal static void OnBlowEnd()
    {
        if (_blows.Count > 0) {
            _blows.Pop();
        }
    }

    /// <summary>
    ///     The player whose blow (its own, its companion's, its summon's) is being settled; 0 for none
    /// </summary>
    private static int Killer => _blows.Count > 0 && PlayerKarma.PlayerBehind(_blows.Peek()) is { } player ? player.uid : 0;

    /// <summary>
    ///     Teams learning on their own: a kill counts in the games of the killer's team only. The game that keeps the
    ///     book writes another team's in that team's ledger
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CodexManager), nameof(CodexManager.AddKill))]
    internal static bool OnAddKillBegin(CodexManager __instance, string id)
    {
        if (!TeamKnowledge.On || string.IsNullOrEmpty(id) || !ReferenceEquals(__instance, EClass.player?.codex) ||
            TeamKnowledge.Shares(Killer, EClass.pc.uid)) {
            return true;
        }

        TeamKnowledge.Noted(Killer, CodexKind.Kill, id, 1);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CodexManager), nameof(CodexManager.AddKill))]
    internal static void OnAddKill(CodexManager __instance, string id)
    {
        if (ElinDelta.IsApplying || string.IsNullOrEmpty(id) || !ReferenceEquals(__instance, EClass.player?.codex)) {
            return;
        }

        var killer = Killer;
        switch (NetSession.Instance.Connection) {
            // not the game that keeps this map: the kill is counted here when the blow is played again
            case ElinNetClient:
                return;
            // the host of the world: the players away did not see it (teams learning on their own: the killer's team)
            case ElinNetHost { IsZoneSession: false } host:
                var seen = new CodexDelta { Id = id, Num = 1, Kind = CodexKind.Kill, Learner = killer };
                foreach (var (peer, _) in host.AwayPeers) {
                    if (host.ActiveRemoteCharas.TryGetValue(peer, out var away) && TeamKnowledge.Shares(killer, away.uid)) {
                        host.SendDeltaTo(peer, seen);
                    }
                }

                return;
            // alone on a map, or keeping it for visitors: the world is told, it tells those on the host's map
            default:
                QuestAwaySync.Send(new CodexDelta { Id = id, Num = 1, Kind = CodexKind.Kill, Relayed = true, Learner = killer });
                return;
        }
    }

    private static void Tell(CodexManager codex, CodexKind kind, string id, int num)
    {
        // (the loot of a kill decided here, a guest's blow replayed included: the killer learns it, RemoteKillLootPatch)
        var killer = TeamKnowledge.On ? RemoteKillLootPatch.Killer : null;
        if ((ElinDelta.IsApplying && killer is null) || num == 0 || string.IsNullOrEmpty(id) || !ReferenceEquals(codex, EClass.player?.codex)) {
            return;
        }

        var session = NetSession.Instance;
        var learner = killer?.uid ?? EClass.pc.uid;

        // travelling alone, or keeping a map for visitors: the codex is the world's, the host tells the others
        // (not our visitors again, they hear it from us below)
        QuestAwaySync.Send(new CodexDelta {
            Id = id,
            Num = num,
            Kind = kind,
            Relayed = session.Connection is ElinNetHost { IsZoneSession: true },
            Learner = learner,
        });

        var told = new CodexDelta {
            Id = id,
            Num = num,
            Kind = kind,
            Learner = learner,
        };
        // teams learning on their own: a host tells only this player's team
        if (TeamKnowledge.On && session.Connection is ElinNetHost host) {
            TeamKnowledge.Tell(host, told, learner, int.MinValue);
            return;
        }

        session.Connection?.Delta.AddRemote(told);
    }
}
