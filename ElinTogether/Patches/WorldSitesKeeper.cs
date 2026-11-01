using System;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

/// <summary>
///     New random sites (nefias) are drawn only while the game stands on the world map: the hour's check
///     (GameDate.AdvanceHour) runs on the region alone, and Region.OnActivate is where expired sites are taken away.
///     A host that stays in a town or a dungeon, and a dedicated server whose stand-in never walks the world map, drew
///     none: the players saw the sites expire one by one and nothing new appear. Guests never draw them (WorldKeeper).
///     The host does each day, wherever it stands, what the world map's activation does: expired sites taken away,
///     then the check. New sites reach the guests as any site the host makes (SpatialGenEvent); the expired ones each
///     game removes itself, from the same expiry date
/// </summary>
[HarmonyPatch]
internal static class WorldSitesKeeper
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameDate), nameof(GameDate.AdvanceDay))]
    internal static void OnAdvanceDay()
    {
        if (NetSession.Instance.Transport is not ElinNetHost || EClass._zone is not { } zone || zone.IsRegion ||
            EClass.world?.region is not { } region || EClass.player.simulatingZone) {
            return;
        }

        try {
            region.InitElomap();

            // as Region.OnActivate: a site nobody needs any more (CanDestroy: expired, no player in it, see
            // LeasedZonePatch for the guests' zones)
            for (var i = region.children.Count - 1; i >= 0; i--) {
                if (region.children[i] is Zone site && !site.destryoed && site.CanDestroy()) {
                    site.Destroy();
                }
            }

            region.children.RemoveAll(s => s.destryoed);

            region.CheckRandomSites();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "The world's random sites could not be renewed away from the world map");
        }
    }

    /// <summary>
    ///     A random site's place is drawn inside the world map's bounds (Region.GetRandomPoint reads map.bounds): away
    ///     from the world map its map is not loaded. The bounds the region keeps with its save are the same, or the
    ///     world map's own size when it was never loaded in this world
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Region), nameof(Region.GetRandomPoint), typeof(ElomapSiteType))]
    internal static bool OnRandomPoint(Region __instance, ElomapSiteType type, ref Point? __result)
    {
        if (__instance.map is not null) {
            return true;
        }

        var elomap = __instance.elomap;
        if (!elomap.initialized) {
            __instance.InitElomap();
        }

        var bounds = __instance.bounds;
        if (bounds is null || bounds.Width <= 1 || bounds.Height <= 1) {
            var size = Mathf.Max(elomap.w, elomap.h);
            bounds = new() { x = 0, z = 0, maxX = size - 1, maxZ = size - 1, Size = size };
        }

        __result = null;
        for (var i = 0; i < 1000; i++) {
            var point = bounds.GetRandomPoint();
            point.x += elomap.minX;
            point.z += elomap.minY;
            if (elomap.CanBuildSite(point.x, point.z, 1, type)) {
                __result = point;
                break;
            }
        }

        return false;
    }
}
