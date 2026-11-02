using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

/// <summary>
///     Host box ReturnWhereLeft: a player sent back to the world map tile it left from (TeamSpawnDelta). The game puts
///     whoever walks onto the world map on the tile of the map it comes out of (Zone.GetSpawnPos), so the tile is
///     taken right after the world map is shown, when it can be walked on (not blocked, not the sea). Otherwise the
///     player stays where the game put it
/// </summary>
[HarmonyPatch]
internal static class LeaveSpotArrival
{
    // long enough for a lease to be granted and the world map to load
    private const float WaitSeconds = 120f;

    private static (int ZoneUid, int X, int Z, float Until)? _expected;

    internal static void Expect(int zoneUid, int x, int z)
    {
        _expected = (zoneUid, x, z, Time.realtimeSinceStartup + WaitSeconds);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
    internal static void OnActivate(Zone __instance)
    {
        if (_expected is not { } spot || NetSession.Instance.Connection is not ElinNetClient) {
            return;
        }

        if (Time.realtimeSinceStartup > spot.Until) {
            _expected = null;
            return;
        }

        if (__instance.uid != spot.ZoneUid || !__instance.IsRegion) {
            return;
        }

        _expected = null;
        // once the map is in place and the character on it
        EClass.core.actionsNextFrame.Add(() => TakeSpot(spot.ZoneUid, spot.X, spot.Z));
    }

    private static void TakeSpot(int zoneUid, int x, int z)
    {
        if (!EClass.core.IsGameStarted || EClass._zone?.uid != zoneUid || EClass._map is not { } map || EClass.pc is not { } pc) {
            return;
        }

        var spot = new Point(x, z);
        var elomap = EClass.scene.elomap;
        if (!spot.IsValid || !spot.IsInBounds || spot.IsBlocked ||
            (elomap is not null && elomap.IsWater(spot.x + elomap.minX, spot.z + elomap.minY))) {
            EmpLog.Information("The world map tile {X},{Z} this player left from cannot be walked on, it stays at {@Pos}", x, z,
                (Models.Position)pc.pos);
            return;
        }

        if (pc.pos.Equals(spot)) {
            return;
        }

        map.MoveCard(spot, pc);
        EmpLog.Information("Back on the world map tile {X},{Z} this player left from", x, z);
    }
}
