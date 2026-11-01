using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The loot of a kill (Card.SpawnLoot) is decided by the host only (<see cref="CardSpawnLootEvent" />), for
///     whoever struck. What it reads and teaches belonged to the host's side:
///     - Anatomy (290) trained on the killer: a guest's character here is a copy whose element changes are ignored, so
///     the skill never rose. The guest's game is told, as for stealth (<see cref="RemoteStealthExpPatch" />).
///     - Teams learning on their own (<see cref="TeamKnowledge" />): the guaranteed first figure of a legendary and the
///     drop bonus read the codex of the host's team. The killer's team's book is lent for the loot, and what it
///     marks (the figure dropped) goes in that team's book and is told to its players
/// </summary>
[HarmonyPatch(typeof(Card), nameof(Card.SpawnLoot))]
internal static class RemoteKillLootPatch
{
    // the player whose kill's loot is being decided here, innermost
    private static Chara? _killer;

    /// <summary>
    ///     The player whose kill's loot is being decided, while it is; null otherwise
    /// </summary>
    internal static Chara? Killer => _killer;

    [HarmonyPrefix]
    internal static void OnSpawnLootBegin(Card origin, out (Chara? Killer, System.Action? Lore) __state)
    {
        __state = (_killer, null);
        if (NetSession.Instance.Connection is not ElinNetHost || !NetCompany.HasCompany) {
            return;
        }

        var killer = PlayerKarma.PlayerBehind(origin);
        _killer = killer;
        if (killer is not null && TeamKnowledge.On) {
            __state.Lore = TeamKnowledge.Lend(killer);
        }
    }

    [HarmonyFinalizer]
    internal static void OnSpawnLootEnd((Chara? Killer, System.Action? Lore) __state)
    {
        _killer = __state.Killer;
        __state.Lore?.Invoke();
    }
}

[HarmonyPatch(typeof(ElementContainer), nameof(ElementContainer.ModExp))]
internal static class RemoteAnatomyExpPatch
{
    private const int Anatomy = 290;

    [HarmonyPrefix]
    internal static void OnModExp(ElementContainer __instance, int ele, float a, bool chain)
    {
        // only the loot's own lessons: SpawnLoot runs on the host alone, the guest's game never gains it itself
        if (ele != Anatomy || a <= 0f || chain || RemoteKillLootPatch.Killer is null ||
            NetSession.Instance.Connection is not ElinNetHost host || __instance.Card is not Chara { IsRemotePlayer: true } chara ||
            !chara.HasElement(Anatomy)) {
            return;
        }

        foreach (var (peerId, remote) in host.ActiveRemoteCharas) {
            if (remote == chara) {
                host.SendDeltaTo(peerId, new PartyExpDelta { Element = ele, Amount = UnityEngine.Mathf.RoundToInt(a) });
                return;
            }
        }
    }
}
