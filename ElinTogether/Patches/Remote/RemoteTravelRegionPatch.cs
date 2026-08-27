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
                new CodeInstruction(OpCodes.Ldarg_0),
                Transpilers.EmitDelegate((bool isRegion, Chara chara) => {
                    // client exp comp
                    if (isRegion && chara.IsPC && NetSession.Instance.IsClient) {
                        EClass.player.distanceTravel++;
                    }
                    return isRegion && NetSession.Instance.IsHost;
                }))
            .InstructionEnumeration();
    }
}