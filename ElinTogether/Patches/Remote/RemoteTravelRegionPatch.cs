using System.Collections.Generic;
using System.Reflection.Emit;
using ElinTogether.Net;
using EModding.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class RemoteTravelRegionPatch
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Chara), nameof(Chara._Move))]
    internal static IEnumerable<CodeInstruction> OnRegionTravelIl(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchEndForward(
                new OperandContains(OpCodes.Call, nameof(Chara.currentZone)),
                new OperandContains(OpCodes.Callvirt, nameof(Spatial.IsRegion)))
            .EnsureValid("Chara._Move currentZone.IsRegion")
            .Advance(1)
            .InsertAndAdvance(
                Transpilers.EmitDelegate((bool isRegion) => isRegion && NetSession.Instance.IsHost))
            .InstructionEnumeration();
    }
}