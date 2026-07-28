using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class WorldSendPackageEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(World), nameof(World.SendPackage))]
    internal static void OnSendPackage(Thing p)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        CardCache.KeepAlive(p);

        if (ZoneActivateEvent.IsHappening) {
            host.Delta.AddRemote(CardGenDelta.Create(p));
        }
    }
}