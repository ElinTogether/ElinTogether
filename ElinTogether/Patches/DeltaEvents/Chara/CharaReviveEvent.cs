using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaReviveEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.MakeGrave))]
    internal static bool OnCharaMakeGrave(Chara __instance)
    {
        return NetSession.Instance.Connection is not ElinNetClient || !__instance.IsPC;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Revive))]
    internal static bool OnCharaRevive(Chara __instance, ref bool __state)
    {
        __state = __instance.isDead;

        if (!__instance.isDead) {
            return true;
        }

        if (NetSession.Instance.Connection is not ElinNetClient client || ElinDelta.IsApplying) {
            return true;
        }

        // drop all other character revives and wait for delta
        if (!__instance.IsPC) {
            return false;
        }

        Position? pos = (__instance.pos.IsValid && EClass._map.charas.Contains(__instance)) ? __instance.pos : null;
        EmpLog.Debug("Requesting revive at {@Pos}", pos);

        client.Delta.AddRemote(new CharaReviveDelta {
            Owner = __instance,
            LastWords = null,
            Pos = pos,
        });

        // scene
        EClass.player.deathDialog = true;

        if (!__instance.pos.IsValid) {
            __instance.pos.Set(EClass._map.GetCenterPos());
        }

        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Revive))]
    internal static void OnCharaReviveEnd(Chara __instance, bool __state)
    {
        if (!__state || __instance.isDead || ElinDelta.IsApplying) {
            return;
        }

        if (NetSession.Instance.Connection is not ElinNetHost host || !__instance.IsPlayer) {
            return;
        }

        EmpLog.Debug("Revive chara {Uid} at {@Pos}",
            __instance.uid, (Position?)(__instance.IsInActiveMap ? __instance.pos : null));

        host.Delta.AddRemote(new CharaReviveDelta {
            Owner = __instance,
            LastWords = null,
            Pos = __instance.IsInActiveMap ? __instance.pos : null,
        });
    }
}