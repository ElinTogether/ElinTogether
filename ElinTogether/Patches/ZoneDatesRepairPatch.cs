using System.Linq;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A client's copy of a map never expires nor regenerates by itself (ElinNetClientZone.OnZoneDataResponse sets
///     both dates to int.MaxValue). A guest that then inherited that map and handed it back sent those dates with
///     its state: on the host the dungeon never expired, the zone never regenerated, and the save kept it so. The
///     host no longer takes them (ZoneLeaseState.ApplyState); a world already marked is put back on the dates the
///     game gives a map generated today
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.OnLoad))]
[AlwaysPatched]
internal static class ZoneDatesRepairPatch
{
    [HarmonyPostfix]
    internal static void OnLoaded()
    {
        // a client's own copy is meant to be so
        if (NetSession.Instance.Connection is ElinNetClient || EClass.game?.spatials?.map is not { } map ||
            EClass.world?.date is not { } date) {
            return;
        }

        var now = date.GetRaw();
        var repaired = 0;
        foreach (var zone in map.Values.OfType<Zone>()) {
            if (zone.dateExpire == int.MaxValue) {
                zone.dateExpire = now + 1440 * zone.ExpireDays;
                repaired++;
            }

            if (zone.dateRegenerate == int.MaxValue) {
                zone.dateRegenerate = now + 1440 * EClass.setting.balance.dateRegenerateZone;
                repaired++;
            }
        }

        if (repaired > 0) {
            EmpLog.Warning("Repaired {Count} map dates set to never by a guest's copy (expiry, regeneration)", repaired);
        }
    }
}
