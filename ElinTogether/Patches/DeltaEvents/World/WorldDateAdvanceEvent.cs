using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(GameDate), nameof(GameDate.AdvanceMin))]
internal static class WorldDateAdvanceEvent
{
    [HarmonyPrefix]
    internal static bool OnAdvanceMin()
    {
        return NetSession.Instance.IsHost;
    }

    [HarmonyPostfix]
    internal static void OnAfterAdvanceMin(int a)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        // host only
        host.Delta.AddRemote(new WorldDateAdvanceDelta {
            Minutes = a,
            GameDate = [..EClass.world.date.raw],
        });
    }
}