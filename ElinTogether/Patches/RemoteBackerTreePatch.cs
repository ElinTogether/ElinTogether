using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A supporter's tree gives its reward once to "the player" (TaskHarvest, Player.doneBackers). A guest's harvest is
///     played on the host, against the host's list: a tree the host or any player had cut gave nothing to the next
///     one. While the host plays a player's harvest, the list is that player's own, kept on its character with the
///     world (its own game keeps its own copy too, OwnSettings)
/// </summary>
[HarmonyPatch(typeof(Progress_Custom), nameof(Progress_Custom.OnProgressComplete))]
internal static class RemoteBackerTreePatch
{
    // a string key of a character's own store (Card.SetStr: never the shared dialog flags)
    internal const string Key = "emp_done_backers";

    [HarmonyPrefix]
    internal static void Begin(Progress_Custom __instance, out (HashSet<int> Host, HashSet<int> Own)? __state)
    {
        __state = null;
        if (NetSession.Instance.Connection is not ElinNetHost || __instance.owner is not { IsRemotePlayer: true } chara ||
            EClass.player is not { } player) {
            return;
        }

        var own = Read(chara);
        __state = (player.doneBackers, own);
        player.doneBackers = [.. own];
    }

    [HarmonyFinalizer]
    internal static void End(Progress_Custom __instance, (HashSet<int> Host, HashSet<int> Own)? __state)
    {
        if (__state is not { } state || EClass.player is not { } player) {
            return;
        }

        var done = player.doneBackers;
        player.doneBackers = state.Host;
        if (!done.SetEquals(state.Own) && __instance.owner is { } chara) {
            chara.SetStr(Key, string.Join(",", done));
        }
    }

    private static HashSet<int> Read(Chara chara)
    {
        return (chara.GetStr(Key) ?? "").Split(',')
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Where(id => id != 0)
            .ToHashSet();
    }
}
