using System.Collections.Generic;
using System.Reflection.Emit;
using ElinTogether.Helper;
using EModding.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class RemoteGetDistPatch
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(ThingContainer), nameof(ThingContainer.GetDest))]
    internal static IEnumerable<CodeInstruction> OnGetRemotePCIl(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchEndForward(
                new OperandContains(OpCodes.Callvirt, nameof(Card.IsPC)))
            .EnsureValid("replace IsPC")
            .SetInstructionAndAdvance(
                Transpilers.EmitDelegate((Card card) => card is Chara { IsPlayer: true }))
            .InstructionEnumeration();
    }
}