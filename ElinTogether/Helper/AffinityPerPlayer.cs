using System.Collections.Generic;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     Host option "Affinity per player" (docs_affinity.md): each player has its own affinity with each resident.
///     The game keeps one number, Chara._affinity, for "the player": that number stays the world owner's (the host's,
///     what a save without the mod reads), the other players' are a table (<see cref="ElinNetHost.Affinities" />, a
///     guest's game has its own column only). Every line of the game that reads or changes it means the affinity
///     with EClass.pc, so the property itself answers with the column of EClass.pc: on a guest's game its own, on the
///     host's the guest standing in (RemoteCraft.AsCrafter, PlayerStandIn) while the host computes for it
/// </summary>
[HarmonyPatch]
internal static class AffinityPerPlayer
{
    /// <summary>
    ///     One act never brings more than this (a precious gift, a ring, with every bonus): a guest's change above it
    ///     is refused when trust is enforced
    /// </summary>
    internal const int MaxGain = 400;

    // a guest's own column (resident uid -> player uid -> value: another player standing in reads its own, empty)
    private static Dictionary<int, Dictionary<int, int>> _known = [];

    internal static bool On => NetSession.Instance is { Transport: not null } session && session.Rules.AffinityPerPlayer;

    private static Dictionary<int, Dictionary<int, int>> Book =>
        NetSession.Instance.Transport is ElinNetClient ? _known : ElinNetHost.Affinities;

    [ElinPreLoad]
    private static void Forget(GameIOContext context)
    {
        _known = [];
    }

    /// <summary>
    ///     The player whose column Chara._affinity means now for this resident, 0 for the game's own value (the world
    ///     owner's, or the option is off)
    /// </summary>
    internal static int ColumnOf(Chara npc)
    {
        if (!On || EClass.core?.game?.player?.chara is not { } pc || npc == pc) {
            return 0;
        }

        var column = NetSession.Instance.Transport switch {
            ElinNetHost when pc.uid == EClass.player.uidChara => 0,
            ElinNetHost or ElinNetClient => pc.uid,
            _ => 0,
        };

        // (what a resident thinks of a player, not what a player thinks of another)
        return column != 0 && !npc.GetBool("remote_chara") ? column : 0;
    }

    /// <summary>
    ///     A player that never met a resident starts at 0; its companion or its spouse keeps the value it had before
    ///     the option (the group's): turning the option on takes nothing from anyone's companions
    /// </summary>
    private static int Start(Chara npc, int player)
    {
        return SharedMarriages.SpouseOf(npc) == player || CompanionHelper.OwnerOf(npc)?.uid == player ? npc._cints[5] : 0;
    }

    internal static int Of(Chara npc, int player)
    {
        return Book.TryGetValue(npc.uid, out var row) && row.TryGetValue(player, out var value) ? value : Start(npc, player);
    }

    private static void Set(int npc, int player, int value)
    {
        var book = Book;
        if (!book.TryGetValue(npc, out var row)) {
            book[npc] = row = [];
        }

        row[player] = value;
    }

    /// <summary>
    ///     The host writes down the starting value of a companion or spouse (it no longer follows the host's)
    /// </summary>
    internal static void Keep(Chara npc, int player)
    {
        if (npc.uid == player || (Book.TryGetValue(npc.uid, out var row) && row.ContainsKey(player))) {
            return;
        }

        if (Start(npc, player) is var start and not 0) {
            Set(npc.uid, player, start);
        }
    }

    /// <summary>
    ///     On the host, a guest's change: added to that guest's column only. The new value
    /// </summary>
    internal static int Add(RemoteCard npc, int player, int change)
    {
        var chara = npc.Find() as Chara ?? EClass.game.cards.globalCharas.Find(npc.Uid);
        var value = (Book.TryGetValue(npc.Uid, out var row) && row.TryGetValue(player, out var had)
            ? had
            : chara is not null ? Start(chara, player) : 0) + change;
        Set(npc.Uid, player, value);
        return value;
    }

    /// <summary>
    ///     On a guest's game, its own column as the host keeps it
    /// </summary>
    internal static void Learn(int npc, int value)
    {
        Set(npc, EClass.player.uidChara, value);
    }

    internal static void Learn(Dictionary<int, int> values)
    {
        var uid = EClass.player.uidChara;
        foreach (var row in _known.Values) {
            row.Remove(uid);
        }

        foreach (var (npc, value) in values) {
            Set(npc, uid, value);
        }
    }

    /// <summary>
    ///     ModAffinity aimed at a player that is not the one standing here (a guest's bout won on the host, the
    ///     receiver of a quest step the host runs): the host runs it as that player, so that it lands in its column.
    ///     Null when nothing changes
    /// </summary>
    internal static ScopeExit? StandIn(Chara npc, ref Chara c)
    {
        if (!On || NetSession.Instance.Transport is not ElinNetHost { IsZoneSession: false } || npc.IsPC ||
            npc.GetBool("remote_chara")) {
            return null;
        }

        Chara? player = null;
        if (!c.IsPC && c.GetBool("remote_chara")) {
            player = c;
        } else if (c.IsPC && c.uid == EClass.player.uidChara &&
                   (QuestRewardPatch.Receiver ?? QuestRewardPatch.Earner) is { } receiver && receiver != c) {
            player = receiver;
        }

        if (player is null) {
            return null;
        }

        c = player;
        return RemoteCraft.AsCrafter(player, false);
    }

    /// <summary>
    ///     After ModAffinity changed the value of the column standing here: the player who acted keeps it and tells
    ///     the host; the host tells the guest it computed for. A replay of another player's act changes nothing
    /// </summary>
    internal static void OnChanged(Chara npc, int before, int change)
    {
        switch (NetSession.Instance.Transport) {
            // (Transport, not Connection: an away guest, or one on a map another guest holds, tells the world host)
            case ElinNetClient client when !ElinDelta.IsRemoteStateLanding && EClass.pc.uid == EClass.player.uidChara:
                client.SendWhileAway(new CharaAffinityDelta {
                    Owner = npc,
                    Value = change,
                    Relative = true,
                });
                break;
            case ElinNetHost { IsZoneSession: false } host when !ElinDelta.IsApplying:
                // the host's own needs telling nobody: no one else reads it
                if (ColumnOf(npc) is var player and not 0 && host.PeerOfChara(player) is var peer and >= 0) {
                    host.SendDeltaTo(peer, new CharaAffinityDelta {
                        Owner = npc,
                        Value = npc._affinity,
                    });
                }

                break;
            default:
                // through the property: the column it came from
                npc._affinity = before;
                break;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara._affinity), MethodType.Getter)]
    internal static bool OnGet(Chara __instance, ref int __result)
    {
        if (ColumnOf(__instance) is not (var player and not 0)) {
            return true;
        }

        __result = Of(__instance, player);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara._affinity), MethodType.Setter)]
    internal static bool OnSet(Chara __instance, int value)
    {
        if (ColumnOf(__instance) is not (var player and not 0)) {
            return true;
        }

        Set(__instance.uid, player, value);
        return false;
    }

    /// <summary>
    ///     A companion learns faster the more it likes "the player" (Card.GetExpMtp): on the host, another player's
    ///     companion was judged by its affinity with the host. Its own player's column
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.GetAffinityExpBonus))]
    internal static bool OnGetAffinityExpBonus(Card __instance, ref int __result)
    {
        if (!On || NetSession.Instance.Transport is not ElinNetHost { IsZoneSession: false } || __instance is not Chara npc ||
            npc.IsPC || npc.GetBool("remote_chara") || CompanionHelper.OwnerOf(npc) is not { } owner || owner == EClass.pc ||
            owner.uid == EClass.player.uidChara) {
            return true;
        }

        // the stage of that column's value, as Affinity.Get finds it for Chara._affinity
        var value = Of(npc, owner.uid);
        var stages = Affinity.list;
        var stage = stages.Find(a => value < a.value) ?? stages[stages.Count - 1];
        __result = UnityEngine.Mathf.Clamp(100 + stage.value / 10, 50, 200);
        return false;
    }
}
