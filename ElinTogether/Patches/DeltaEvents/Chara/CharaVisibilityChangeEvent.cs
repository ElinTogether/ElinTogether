using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;
using NPOI.SS.Formula.Functions;

[HarmonyPatch]
internal static class CharaVisibilityChangeEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CharaRenderer), nameof(CharaRenderer.OnEnterScreen))]
    internal static void OnEnterScreen(CharaRenderer __instance)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        if (__instance.owner.ExistsOnMap && !EClass._zone.IsRegion && __instance.owner.IsHostile() && EClass.pc.CanSeeLos(__instance.owner, -1)) {
            if (ActionModeCombat.EnemyVisibility.TryGetValue(EClass.pc.uid, out var value) && value is true) {
                return;
            }

            ActionModeCombat.EnemyVisibility[EClass.pc.uid] = true;
            connection.Delta.AddRemote(new EnemyVisibilityDelta {
                PlayerId = EClass.pc.uid,
                Visible = true,
            });
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CardRenderer), nameof(CardRenderer.KillActor))]
    internal static void OnKillActor(CardRenderer __instance)
    {
        if (NetSession.Instance.Connection is not { } connection || EClass._zone.IsRegion || __instance.owner is not Chara) {
            return;
        }

        if (HasNoEnemyInSight()) {
            if (ActionModeCombat.EnemyVisibility.TryGetValue(EClass.pc.uid, out var value) && value is false) {
                return;
            }

            ActionModeCombat.EnemyVisibility[EClass.pc.uid] = false;
            connection.Delta.AddRemote(new EnemyVisibilityDelta {
                PlayerId = EClass.pc.uid,
                Visible = false,
            });
        }
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static void OnDie()
    {
        if (NetSession.Instance.Connection is not ElinNetHost connection || EClass._zone.IsRegion) {
            return;
        }

        var hasEnemyInSight = !HasNoEnemyInSight();
        ActionModeCombat.EnemyVisibility[EClass.pc.uid] = hasEnemyInSight;
        connection.Delta.AddRemote(new EnemyVisibilityDelta {
            PlayerId = EClass.pc.uid,
            Visible = hasEnemyInSight,
        });
    }

    internal static bool HasNoEnemyInSight()
    {
        return EClass.game?.activeZone?.map.charas.Any(c => !c.isDead && c.ExistsOnMap && c.IsHostile() && EClass.pc.CanSeeLos(c)) is false;
    }
}