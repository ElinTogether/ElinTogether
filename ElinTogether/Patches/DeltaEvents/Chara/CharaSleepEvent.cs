using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaSleepEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.OnSleep), typeof(int), typeof(int), typeof(bool))]
    internal static void OnHostSleep(Chara __instance, int power, int days)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        if (!__instance.IsPC) {
            return;
        }

        EmpLog.Debug("Zzz party sleep {SleepPower}", power);

        host.Delta.AddRemote(new CharaSleepDelta {
            Power = power,
            Days = days,
        });
    }
}