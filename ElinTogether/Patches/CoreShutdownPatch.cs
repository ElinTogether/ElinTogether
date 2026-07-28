using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class CoreShutdownPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Core), nameof(Core.OnApplicationQuit))]
    internal static void OnTeardownBeforeSteamShutdown()
    {
        NetShutdown.Shutdown();
    }
}