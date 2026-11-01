using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The game counts the gifts of a god once per world: with several players, the first to pray took the pet
///     and the artifact of that god from everyone else. Each player who is not the host of the save has its own
///     count, kept on its character; the world's count stays the host's <br />
///     council decision of 2026-10-04, see MODLOG
/// </summary>
[HarmonyPatch(typeof(Religion), nameof(Religion.TryGetGift))]
internal static class RemoteGodGiftPatch
{
    // an int key, not a string one: Card.SetInt(string) of "the player" also writes the shared dialog flags
    internal static int Key(Religion religion)
    {
        return Key(religion.id);
    }

    internal static int Key(string religionId)
    {
        return ("emp_gift_" + religionId).GetHashCode();
    }

    /// <summary>
    ///     A player's character uploaded by its own game replaces the host's copy (ElinNetHost.ReplaceRemoteChara):
    ///     each count is the higher of the two, the gifts the host gave on its map are not forgotten by an upload that
    ///     never heard of them, nor those given while away by the host's older copy (GitHub issue 51). Returns the
    ///     counts the uploading game is behind on, to tell it
    /// </summary>
    internal static List<(string Religion, int Rank)> Merge(Chara uploaded, Chara? old)
    {
        var behind = new List<(string, int)>();
        if (old is null || EClass.game?.religions?.list is not { } religions) {
            return behind;
        }

        foreach (var religion in religions) {
            var key = Key(religion);
            var mine = uploaded.GetInt(key);
            var kept = old.GetInt(key);
            if (kept > mine) {
                uploaded.SetInt(key, kept);
                behind.Add((religion.id, kept));
            }
        }

        return behind;
    }

    /// <summary>
    ///     The host gave a player a gift of its god: that player's own game keeps the count too, else praying away
    ///     from the host it would be given again
    /// </summary>
    private static void Tell(Chara chara, Religion religion)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host || !chara.IsRemotePlayer) {
            return;
        }

        foreach (var (peer, uid) in host.PeerCharas()) {
            if (uid == chara.uid) {
                host.SendDeltaTo(peer, new GodGiftDelta {
                    Religion = religion.id,
                    Rank = chara.GetInt(Key(religion)),
                });
            }
        }
    }

    [HarmonyPrefix]
    internal static bool OnTryGetGift(Religion __instance, Chara? chara, ref bool __result, out int? __state)
    {
        __state = null;
        chara ??= EClass.pc;

        var session = NetSession.Instance;
        if (session.Connection is ElinNetClient) {
            // given by the game that simulates the map, when it plays the prayer again (ActPrayEvent)
            __result = false;
            return false;
        }

        if (chara.IsRemotePlayer || (chara.IsPC && session.Transport is ElinNetClient)) {
            __state = __instance.giftRank;
            __instance.giftRank = chara.GetInt(Key(__instance));
        }

        return true;
    }

    [HarmonyFinalizer]
    internal static void OnTryGetGiftEnd(Religion __instance, Chara? chara, int? __state)
    {
        if (__state is not { } world) {
            return;
        }

        var player = chara ?? EClass.pc;
        var before = player.GetInt(Key(__instance));
        player.SetInt(Key(__instance), __instance.giftRank);
        __instance.giftRank = world;

        if (player.GetInt(Key(__instance)) > before) {
            Tell(player, __instance);
        }
    }
}
