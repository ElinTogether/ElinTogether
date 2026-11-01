using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     GitHub issue 55: "Farris didn't spawn" on Nymelle B2F, the story stuck. The game makes Farris once, on the
///     first visit of the second floor (Zone_Nymelle.OnBeforeSimulate: visitCount 0 and no "farris" among the
///     world's characters), at its locked stairs. That floor was first made by a player travelling alone (a lease):
///     Farris was made in that game only, as a character of the world, which the map handed back never carries. The
///     host's world had no Farris and a visited floor, so no game ever made her again. <br />
///     Now the game holding the floor tells the host when Farris comes there (<see cref="NymelleFarrisDelta" />),
///     and a stuck world is repaired: whoever simulates the floor (the host, or a player holding it) makes her the
///     game's way when the floor was visited already, the world has no Farris and the story still waits for her
///     (exploration not past its start)
/// </summary>
[HarmonyPatch(typeof(Zone_Nymelle), nameof(Zone_Nymelle.OnBeforeSimulate))]
internal static class NymelleFarrisRepair
{
    private const string Farris = "farris";

    [HarmonyPrefix]
    internal static void Before(Zone_Nymelle __instance, out bool __state)
    {
        __state = __instance.lv == -2 && EClass.game.cards.globalCharas.Find(Farris) is null;
    }

    [HarmonyPostfix]
    internal static void After(Zone_Nymelle __instance, bool __state)
    {
        if (!__state) {
            return;
        }

        var session = NetSession.Instance;
        // a visitor's game: the floor and Farris come from the game that simulates it
        if (session.Transport is ElinNetClient && !session.IsZoneAuthority) {
            return;
        }

        var farris = EClass.game.cards.globalCharas.Find(Farris);
        if (farris is null) {
            // (the game makes her at the first visit: the floor was visited without her)
            if (EClass.game.quests.GetPhase<QuestExploration>() > QuestExploration.Started) {
                return;
            }

            var at = EClass._map.props.installed.Find<TraitStairsLocked>()?.pos ??
                     EClass._map.GetCenterPos().GetNearestPoint(allowBlock: false, allowChara: false);
            farris = Create(0);
            EClass._zone.AddCard(farris, at.x, at.z);
            EmpLog.Information("Nymelle's second floor was visited without Farris, the story waiting for her: made at {X},{Z}",
                at.x, at.z);
        }

        // a player holding the floor: the host's world is to have her too
        if (session.Transport is ElinNetClient main && farris.currentZone == __instance) {
            main.SendWhileAway(new NymelleFarrisDelta {
                ZoneUid = __instance.uid,
                Uid = farris.uid,
                X = farris.pos.x,
                Z = farris.pos.z,
            });
        }
    }

    /// <summary>
    ///     Farris as Zone_Nymelle.OnBeforeSimulate makes her, not yet placed. Under that number when not 0
    /// </summary>
    internal static Chara Create(int uid)
    {
        var farris = CharaGen.Create(Farris);
        if (uid != 0) {
            // (not CardCache.Rebind: the number is in the holder's lease range, the host's own counter stays below it)
            CardCache.Remove(farris.uid);
            farris.uid = uid;
            CardCache.Set(farris);
        }

        farris.SetGlobal();
        farris.AddEditorTag(EditorTag.AINoMove);
        farris.AddEditorTag(EditorTag.Invulnerable);
        return farris;
    }
}
