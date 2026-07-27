using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Quest), nameof(Quest.ChangePhase))]
internal class QuestChangePhaseEvent
{
    [HarmonyPrefix]
    internal static bool OnClientChangePhase(Quest __instance, int a)
    {
        if (NetSession.Instance.IsHost) {
            return true;
        }

        __instance.phase = a;
        __instance.UpdateJournal();

        return false;
    }

    [HarmonyPostfix]
    internal static void OnChangePhase(Quest __instance, int a)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return;
        }

        if (connection.IsClient) {
            return;
        }

        connection.Delta.AddRemote(new QuestChangePhaseDelta {
            Uid = __instance.uid,
            Modifier = a,
        });
    }
}