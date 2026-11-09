using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Nymelle's crystal floor without Fiama, the main story blocked (a player's report on the Workshop page,
///     2026-11-05): the game moves Fiama there once, on the first visit of that floor (Zone_Nymelle.OnBeforeSimulate:
///     visitCount 0, MoveHome to 43,67). A floor first made by a player travelling alone moved Fiama in that game only;
///     the host's world kept her at home and its floor was visited, so no game moved her there again and the crystal
///     talk (QuestExploration phase 3) could never happen. The same kind as GitHub issue 55 (Farris, see
///     <see cref="NymelleFarrisRepair" />). <br />
///     Now whoever simulates the crystal floor (the host, or a player holding it) moves her there the game's way when
///     the floor was visited already, she stands elsewhere, and the story still waits for that talk
/// </summary>
[HarmonyPatch(typeof(Zone_Nymelle), nameof(Zone_Nymelle.OnBeforeSimulate))]
internal static class NymelleFiamaRepair
{
    [HarmonyPostfix]
    internal static void After(Zone_Nymelle __instance)
    {
        if (!__instance.IsCrystalLv || __instance.visitCount == 0 ||
            EClass.game.quests.GetPhase<QuestExploration>() >= QuestExploration.AfterCrystalTalk) {
            return;
        }

        // a visitor's game: the floor and who stands on it come from the game that simulates it
        var session = NetSession.Instance;
        if (session.Transport is ElinNetClient && !session.IsZoneAuthority) {
            return;
        }

        if (EClass.game.cards.globalCharas.Find("fiama") is not { isDead: false } fiama || fiama.currentZone == __instance) {
            return;
        }

        fiama.MoveHome(__instance, 43, 67);
        fiama.AddEditorTag(EditorTag.AINoMove);
        EmpLog.Information("Nymelle's crystal floor was visited without Fiama, the story waiting for her: moved there");
    }
}
