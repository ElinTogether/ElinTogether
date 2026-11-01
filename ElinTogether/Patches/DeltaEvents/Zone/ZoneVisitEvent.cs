using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A player arrives on a map another game keeps (the host's): what the game does to a town at the arrival of
///     its player (Zone.OnVisit: townspeople revived and restocked when due, the day's quests) is asked of that
///     game, which alone changes the map. Only the keeper's own arrival did it: a town the host stayed in never came
///     back to life for its guests. See <see cref="QuestRerollDelta.Visit" />
/// </summary>
[HarmonyPatch(typeof(Zone), nameof(Zone.OnVisit))]
internal static class ZoneVisitEvent
{
    internal static bool IsVisiting { get; private set; }

    [HarmonyPrefix]
    internal static void OnVisit(Zone __instance)
    {
        IsVisiting = true;
        if (ElinDelta.IsApplying || EClass.game.isLoading || NetSession.Instance.Connection is not ElinNetClient client ||
            __instance != EClass._zone) {
            if (NetSession.Instance.Connection is ElinNetClient) {
                EmpLog.Debug("Visit of zone {ZoneUid} not told to the host: applying {Applying}, loading {Loading}, current {Current}",
                    __instance.uid, ElinDelta.IsApplying, EClass.game.isLoading, __instance == EClass._zone);
            }

            return;
        }

        EmpLog.Debug("Telling the host of the visit of zone {ZoneUid}", __instance.uid);

        client.Delta.AddRemote(new QuestRerollDelta {
            ZoneUid = __instance.uid,
            Kind = QuestRerollDelta.Visit,
        });
    }

    [HarmonyFinalizer]
    internal static void OnVisitEnd()
    {
        IsVisiting = false;
    }
}
