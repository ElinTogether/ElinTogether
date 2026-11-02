using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ElinTogether.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The character sheet's biography (Player.GetBackgroundText) and its edit button (EditBackgroundText) read the
///     background.txt of the save: on a client, the copy of the host's world, so the host's biography. They read the
///     player's own file instead, see <see cref="OwnSettings.BackgroundFile" />
/// </summary>
[HarmonyPatch]
internal static class OwnBackgroundPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Player), nameof(Player.GetBackgroundText));
        yield return AccessTools.Method(typeof(Player), nameof(Player.EditBackgroundText));
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> OnReadBackground(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var save = AccessTools.PropertyGetter(typeof(GameIO), nameof(GameIO.pathCurrentSave));
        var concat = AccessTools.Method(typeof(string), nameof(string.Concat), [typeof(string), typeof(string)]);
        var own = AccessTools.Method(typeof(OwnSettings), nameof(OwnSettings.BackgroundFile));
        var list = instructions.ToList();
        var changed = 0;
        // GameIO.pathCurrentSave + "background.txt"
        for (var i = 0; i < list.Count - 2; i++) {
            if (list[i].Calls(save) && list[i + 1].Is(OpCodes.Ldstr, "background.txt") && list[i + 2].Calls(concat)) {
                var call = new CodeInstruction(OpCodes.Call, own);
                call.labels.AddRange(list[i].labels);
                list.RemoveRange(i, 3);
                list.Insert(i, call);
                changed++;
            }
        }

        if (changed == 0) {
            EmpLog.Warning("Own biography: no background.txt read in {Method}, the game changed", original.FullDescription());
        }

        return list;
    }
}
