using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class RemoteRideHostPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BaseGameScreen), nameof(BaseGameScreen.RefreshPosition))]
    internal static void OnSetHostRideFocus(BaseGameScreen __instance)
    {
        if (EClass.pc.host is { } host) {
            EClass.player.position = host.renderer.position;
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(BaseTileMap), nameof(BaseTileMap.DrawTile))]
    internal static IEnumerable<CodeInstruction> OnDrawTileIl(IEnumerable<CodeInstruction> instructions)
    {
        // if (!chara.IsPC && !chara.renderer.IsMoving && this.detail.charas.Count > 1 && (this.detail.charas.Count != 2 || !this.detail.charas[0].IsDeadOrSleeping || !this.detail.charas[0].IsPCC))
        // {
        //     this._actorPos += this.renderSetting.charaPos[1 + ((num29 < 4) ? num29 : 3)];
        // }
        return new CodeMatcher(instructions)
            .MatchStartForward(
                new CodeMatch(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPC))))
            .SetInstruction(
                Transpilers.EmitDelegate((Card card) => card.IsPC || EClass.pc.host == card))
            .InstructionEnumeration();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActRide), nameof(ActRide.Ride))]
    internal static bool OnRideRemoteHost(Chara host, Chara t)
    {
        if (!IsRidingBeRiddenTheRide(host, t)) {
            return true;
        }

        EmpLog.Debug("blocked cyclic ride, chara {OwnerUid} is already hosted by {TargetUid}", host.uid, t.uid);

        if (host.IsPC) {
            Msg.SayInvalidAction();
        }

        return false;
    }

    private static bool IsRidingBeRiddenTheRide(Chara host, Chara t)
    {
        var chara = host;
        for (var depth = 0; chara is not null && depth < 8; ++depth) {
            if (chara == t) {
                return true;
            }

            chara = chara.host;
        }

        return chara is not null;
    }
}