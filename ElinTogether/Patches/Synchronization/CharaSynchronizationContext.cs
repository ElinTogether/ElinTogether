using System.Collections.Generic;
using System.Reflection.Emit;
using EModding.Helper;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class CharaSynchronizationContext : SynchronizationContext
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Chara), nameof(Chara._Move))]
    internal static IEnumerable<CodeInstruction> OnCharaMove(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchStartForward(
                new OperandContains(OpCodes.Stfld, nameof(Chara.actTime)))
            .EnsureValid("Chara._Move set field actTime")
            .SetInstructionAndAdvance(
                Transpilers.EmitDelegate(SetActTime))
            .InstructionEnumeration();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
    internal static IEnumerable<CodeInstruction> OnCharaTick(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchStartForward(
                new OperandContains(OpCodes.Stfld, nameof(Chara.actTime)))
            .EnsureValid("Chara.Tick set field actTime 1")
            .SetInstructionAndAdvance(
                Transpilers.EmitDelegate(SetActTime))
            .MatchStartForward(
                new CodeMatch(OpCodes.Stfld, AccessTools.Field(typeof(Chara), nameof(Chara.actTime))))
            .EnsureValid("Chara.Tick set field actTime 2")
            .InsertAndAdvance(
                new(OpCodes.Pop),
                Transpilers.EmitDelegate(() => player.baseActTime))
            .SetInstructionAndAdvance(
                Transpilers.EmitDelegate(SetActTime))
            .InstructionEnumeration();
    }

    private static void SetActTime(Chara chara, float num)
    {
        chara.actTime = num * Mathf.Max(0.1f, (float)RefSpeed / chara.Speed);
    }
}