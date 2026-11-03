using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Entering a map, the game catches up on the hours it was not played (Zone.OnVisit -> Zone.Simulate): on a base,
///     livestock lay eggs and fertilized eggs on beds hatch (TraitFoodEggFertilized.Incubate, a new resident of the
///     base). A client entering a map another game keeps (the host's, or a player's it visits) ran that catch-up in
///     its own copy too, on the map the keeper had already lived through: the chicks hatched there got uids
///     waiting for the keeper's number that never came (made during the zone activation, so neither sent nor
///     thrown away). When that client later kept the map (the host left it, the player stayed), it gave them
///     numbers of its own and handed them back as residents made while alone: the base grew by every chick each
///     client had hatched on its own (GitHub issue 74: 25 snow putits, then 90; the host log shows 24 new residents
///     with consecutive uids from the start of the range of the player who took the map over). Only the game
///     that keeps the map catches up
/// </summary>
[HarmonyPatch(typeof(Zone), nameof(Zone.Simulate))]
internal static class KeeperSimulatesPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    internal static bool OnSimulate(Zone __instance)
    {
        // a visitor of another player's map (even before its zone session is up), or a client on the host's map,
        // the take-over of the host's copy included (loaded before this game is away)
        var session = NetSession.Instance;
        if (!session.IsGuest && (session.IsAway || session.Transport is not ElinNetClient)) {
            return true;
        }

        // what BossFleePatch would have taken for this entry, skipped with the catch-up
        ZoneLeaseState.Imported.Remove(__instance.uid);

        EmpLog.Debug("Catch-up of zone {ZoneUid} left to the game that keeps it", __instance.uid);
        return false;
    }
}
