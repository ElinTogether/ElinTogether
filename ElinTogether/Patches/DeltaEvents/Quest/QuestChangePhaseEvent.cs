using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class QuestChangePhaseEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Quest), nameof(Quest.ChangePhase))]
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