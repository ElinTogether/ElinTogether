using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class GameSaveLoad
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    internal static bool OnSaveRemoteGame(ref bool __result, bool isAutoSave)
    {
        // an away client reads as host but only holds a copy of the host world
        if (NetSession.Instance.IsHost && !NetSession.Instance.IsAway) {
            // a stack split here is held out of any bag (InvSplitThingEvent): in no save either (issue 48)
            Helper.HeldDrag.PutBack("saving");

            // the maps players handed back go in with this save, not before (the game skips an autosave it was told to)
            if (!(isAutoSave && EClass.debug.ignoreAutoSave)) {
                try {
                    ZoneLeaseState.CommitAllStaged();
                } catch (System.Exception ex) {
                    // never a reason not to save
                    EmpLog.Warning(ex, "Maps handed back by players could not all be put in the save");
                }
            }

            return true;
        }

        EmpLog.Debug("Blocked saving game as client");
        __result = true;

        // the game also saves by itself (changing map, sleeping): one line now and then, not one per save
        if (EClass.core.IsGameStarted && Time.realtimeSinceStartup - _saveNoticeAt > 60f) {
            _saveNoticeAt = Time.realtimeSinceStartup;
            Msg.Say("emp_ui_save_kept".lang());
        }

        return false;
    }

    private static float _saveNoticeAt = float.MinValue;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.TryLoad))]
    internal static bool OnLoadRemoteGame(ref bool __result)
    {
        // the title screen, a game played alone, and a session nobody else is in (it opens again after the load)
        var session = NetSession.Instance;
        if (EClass.game?.player?.chara is null || session.Transport is null ||
            (session.Transport.IsHost && session.CurrentPlayers.Count <= 1)) {
            return true;
        }

        // a shared game has no save of its own to go back to: a guest has none on its PC (the game threw on the
        // missing folder), and the host loading would throw every player out. Said in the game's log, nothing loaded
        Msg.Say("emp_ui_load_blocked".lang());
        __result = false;
        return false;
    }

    [ElinPreLoad]
    internal static void TerminateConnectionOnLoad(GameIOContext context)
    {
        NetSession.Instance.ResetSession();
    }

    [ElinPostSceneInit]
    internal static void TerminateConnectionOnLoad(Scene.Mode mode)
    {
        if (mode == Scene.Mode.Title) {
            NetSession.Instance.ResetSession();
        }
    }
}