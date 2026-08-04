using System.Collections.Generic;
using System.Reflection.Emit;
using ElinTogether.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class AIPlayMusicPatch
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(AI_PlayMusic), nameof(AI_PlayMusic.Evaluate))]
    internal static IEnumerable<CodeInstruction> OnEvaluate(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchStartForward(
                new CodeMatch(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC))))
            .SetInstructionAndAdvance(
                Transpilers.EmitDelegate((Chara chara) => chara.IsPlayer))
            .InstructionEnumeration();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(AI_PlayMusic), nameof(AI_PlayMusic.ThrowReward))]
    internal static IEnumerable<CodeInstruction> OnThrowReward(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchStartForward(
                new CodeMatch(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC))))
            .Repeat(cm => cm.SetInstructionAndAdvance(
                Transpilers.EmitDelegate((Chara chara) => chara.IsPlayer)))
            .InstructionEnumeration();
    }
}