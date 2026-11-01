using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The boss of the zone dies in the game that simulates it, see <see cref="BossWinDelta" />
/// </summary>
[HarmonyPatch(typeof(Chara), nameof(Chara.TryDropBossLoot))]
internal static class BossWinEvent
{
    [HarmonyPrefix]
    internal static void Before(Chara __instance, out Zone? __state)
    {
        __state = EClass._zone is { } zone && zone.uidBoss != 0 && zone.uidBoss == __instance.uid ? zone : null;
    }

    [HarmonyPostfix]
    internal static void After(Zone? __state)
    {
        var session = NetSession.Instance;
        if (__state is not { uidBoss: 0 } zone || session.Transport is null || ElinDelta.IsApplying ||
            (session.Connection is ElinNetClient && !session.IsZoneAuthority)) {
            return;
        }

        var delta = new BossWinDelta {
            ZoneUid = zone.uid,
            TopZoneUid = zone.GetTopZone().uid,
            Nefia = zone.IsNefia,
            DangerLv = zone.DangerLv,
        };

        // alone on a map of its own: the dungeon is the world's, the host tells everyone
        QuestAwaySync.Send(delta);
        session.Connection?.Delta.AddRemote(delta);
    }
}

/// <summary>
///     The boss of a nefia leaves when its floor is entered again (Zone.Simulate): the game that runs the zone marks
///     the dungeon conquered in its world alone. A guest alone in that dungeon left it unconquered in everyone else's,
///     the host's too (its lease hands back the zone it held, not the top of the dungeon)
/// </summary>
[HarmonyPatch(typeof(Zone), nameof(Zone.Simulate))]
internal static class BossLeaveEvent
{
    [HarmonyPrefix]
    internal static void Before(Zone __instance, out Zone? __state)
    {
        __state = __instance.GetTopZone() is { isConquered: false } top ? top : null;
    }

    [HarmonyPostfix]
    internal static void After(Zone __instance, Zone? __state)
    {
        var session = NetSession.Instance;
        if (__state is not { isConquered: true } || !__instance.IsNefia || session.Transport is null || ElinDelta.IsApplying ||
            (session.Connection is ElinNetClient && !session.IsZoneAuthority)) {
            return;
        }

        var delta = new BossWinDelta {
            ZoneUid = __instance.uid,
            TopZoneUid = __state.uid,
            Nefia = true,
            DangerLv = __instance.DangerLv,
            Left = true,
        };

        QuestAwaySync.Send(delta);
        session.Connection?.Delta.AddRemote(delta);
    }
}
