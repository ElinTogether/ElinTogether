using System.Collections.Generic;
using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class ActionModeCombat
{
    internal static Dictionary<int, bool> EnemyVisibility { get; } = [];
    internal static bool Paused { get; private set; }
    internal static bool WaitForSelf { get; private set; }
    internal static bool Activated { get; private set; }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.OnUpdate))]
    internal static void CheckIfPauseNeeded()
    {
        var players = NetSession.Instance.CurrentPlayers.ToList();
        var keysToRemove = EnemyVisibility
            .Where(kv => players.All(p => p.CharaUid != kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in keysToRemove) {
            EnemyVisibility.Remove(key);
        }

        if (!NetSession.Instance.Rules.UseTurnBasedCombat ||
            EnemyVisibility.Values.All(v => !v) ||
            NetSession.Instance.Connection is null ||
            players.Count < 2) {
            if (Activated) {
                Msg.SayGod("emp_ui_combat_exit".lang());
            }
            Activated = false;
            Paused = false;
            WaitForSelf = false;
            return;
        }

        if (!Activated) {
            EClass.pc.ai.Cancel();
            Msg.SayGod("emp_ui_combat_enter".lang());
        }

        Activated = true;

        if (EClass.pc.HasNoGoal) {
            if (Paused && WaitForSelf) {
                return;
            }

            Paused = true;
            WaitForSelf = true;
            Msg.SayGod("emp_ui_combat_decide".lang());

            return;
        }

        var hasAnyoneToDecide = EClass.pc.party.members.Any(c => c.IsRemotePlayer && c.ai is GoalRemote { child: null });
        if (hasAnyoneToDecide) {
            if (Paused && !WaitForSelf) {
                return;
            }

            Paused = true;
            WaitForSelf = false;
            Msg.SayGod("emp_ui_combat_wait".lang());

            return;
        }

        Paused = false;
        WaitForSelf = false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AIAct), nameof(AIAct.Tick))]
    private static bool PreventImmediateAITick()
    {
        return !Paused;
    }
}