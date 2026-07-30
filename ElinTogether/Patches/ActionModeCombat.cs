using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ElinTogether.Elements;
using ElinTogether.Models;
using ElinTogether.Net;
using EModding.Helper;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch]
public class ActionModeCombat
{
    public enum CombatPhase
    {
        Inactive,
        Deciding,
        Executing,
    }

    // failsafe
    private const float ExecutingTimeout = 30f;
    private const float VisibilityRefreshInterval = 0.5f;

    private static readonly HashSet<int> _decided = [];
    private static readonly HashSet<int> _done = [];
    private static float _executingTimer;
    private static float _visibilityTimer;
    private static int _lastPlayerCount;

    private static readonly Dictionary<int, float> _turnBuffer = [];
    private static float _turnBuffered;
    private static bool _pcActedThisRound;
    private static bool _executingBlockNotified;

    internal static Dictionary<int, bool> EnemyVisibility { get; } = [];
    internal static CombatPhase Phase { get; private set; }
    internal static bool Activated => Phase != CombatPhase.Inactive;
    internal static bool Paused => Phase == CombatPhase.Deciding;
    internal static bool WaitForSelf { get; private set; }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.OnUpdate))]
    internal static void CheckIfPauseNeeded()
    {
        if (NetSession.Instance.Connection is not { } connection) {
            ChangePhaseLocal(CombatPhase.Inactive);
            return;
        }

        var players = NetSession.Instance.CurrentPlayers.ToList();
        var keysToRemove = EnemyVisibility
            .Where(kv => players.All(p => p.CharaUid != kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in keysToRemove) {
            EnemyVisibility.Remove(key);
        }

        RefreshSelfVisibility(connection);

        if (connection.IsHost) {
            ApplyTurnBudget();
            HostPhaseUpdate(connection, players);
        }

        UpdateDecideMessage();
    }

    private static void RefreshSelfVisibility(ElinNetBase net)
    {
        _visibilityTimer += Time.deltaTime;
        if (_visibilityTimer < VisibilityRefreshInterval) {
            return;
        }
        _visibilityTimer = 0f;

        // HasNoEnemyInSight count null map as false
        if (EClass.game?.activeZone?.map is null) {
            return;
        }

        var visible = !EClass._zone.IsRegion && !CharaVisibilityChangeEvent.HasNoEnemyInSight();
        if (EnemyVisibility.GetValueOrDefault(EClass.pc.uid) == visible) {
            return;
        }

        EnemyVisibility[EClass.pc.uid] = visible;
        EmpLog.Debug("Self enemy visibility changed to {CombatVisible}", visible);
        net.Delta.AddRemote(new EnemyVisibilityDelta {
            PlayerId = EClass.pc.uid,
            Visible = visible,
        });
    }

    internal static void OnRemoteTaskReport(int uid, bool hasGoal)
    {
        if (!Activated) {
            return;
        }

        bool changed;
        if (hasGoal) {
            changed = _decided.Add(uid) | _done.Remove(uid);
        } else {
            changed = _decided.Remove(uid);
            if (Phase == CombatPhase.Executing) {
                changed |= _done.Add(uid);
            }
        }

        if (changed) {
            EmpLog.Debug("Combat report from chara {Uid}, decided {CombatDecided}",
                uid, hasGoal);
        }
    }

    internal static void ChangePhaseLocal(CombatPhase phase)
    {
        if (Phase == phase) {
            return;
        }

        var prev = Phase;
        Phase = phase;
        _executingTimer = 0f;
        _executingBlockNotified = false;
        if (phase != CombatPhase.Executing) {
            _turnBuffer.Clear();
            _turnBuffered = 0f;
            _pcActedThisRound = false;
        }

        EmpLog.Debug("Combat phase changed to {CombatPhase}", phase);

        switch (phase) {
            case CombatPhase.Inactive:
                _decided.Clear();
                _done.Clear();
                WaitForSelf = false;
                Msg.SayGod("emp_ui_combat_exit".lang());
                break;
            case CombatPhase.Deciding when prev == CombatPhase.Inactive:
                EClass.pc.ai.Cancel();
                Msg.SayGod("emp_ui_combat_enter".lang());
                break;
            case CombatPhase.Executing:
                _done.Clear();
                break;
        }
    }

    private static void HostPhaseUpdate(ElinNetBase net, List<NetPeerState> players)
    {
        var active = NetSession.Instance.Rules.UseTurnBasedCombat &&
                     EnemyVisibility.Values.Any(v => v) &&
                     players.Count >= 2;

        if (!active) {
            ChangePhase(net, CombatPhase.Inactive);
            return;
        }

        // joined mid combat
        if (Activated && players.Count != _lastPlayerCount) {
            net.Delta.AddRemote(new CombatPhaseDelta {
                Phase = Phase,
            });
        }
        _lastPlayerCount = players.Count;

        switch (Phase) {
            case CombatPhase.Inactive:
                ChangePhase(net, CombatPhase.Deciding);
                break;

            case CombatPhase.Deciding: {
                var allDecided = !EClass.pc.HasNoGoal && players.All(p =>
                    p.CharaUid == EClass.pc.uid ||
                    (p.FindChara() is { } chara && _decided.Contains(chara.uid)));
                if (allDecided) {
                    ChangePhase(net, CombatPhase.Executing);
                }
                break;
            }

            case CombatPhase.Executing: {
                _executingTimer += Time.deltaTime;
                // disconnected mid combat
                var allDone = EClass.pc.HasNoGoal && players.All(p =>
                    p.CharaUid == EClass.pc.uid ||
                    p.FindChara() is not { } chara ||
                    _done.Contains(chara.uid));
                if (allDone) {
                    ChangePhase(net, CombatPhase.Deciding);
                } else if (_executingTimer > ExecutingTimeout) {
                    EmpLog.Warning("Combat executing phase timed out, forcing next round");
                    ChangePhase(net, CombatPhase.Deciding);
                }
                break;
            }
        }
    }

    private static void ChangePhase(ElinNetBase net, CombatPhase phase)
    {
        if (Phase == phase) {
            return;
        }

        net.Delta.AddRemote(new CombatPhaseDelta {
            Phase = phase,
        });
        ChangePhaseLocal(phase);
    }

    private static void UpdateDecideMessage()
    {
        if (Phase != CombatPhase.Deciding) {
            WaitForSelf = false;
            return;
        }

        if (EClass.pc.HasNoGoal) {
            if (!WaitForSelf) {
                WaitForSelf = true;
                Msg.SayGod("emp_ui_combat_decide".lang());
            }
        } else if (WaitForSelf) {
            WaitForSelf = false;
            Msg.SayGod("emp_ui_combat_wait".lang());
        }
    }

    internal static void OnRemotePlayerTick(Chara chara)
    {
        if (Phase != CombatPhase.Executing) {
            return;
        }

        if (chara.ai is not GoalRemote { child: not null }) {
            return;
        }

        // Chara.Tick
        var remote = EClass.player.baseActTime * Mathf.Max(0.1f, (float)SynchronizationContext.RefSpeed / chara.Speed);
        ReportPlayerTurn(chara.uid, remote);
    }

    private static void ReportPlayerTurn(int uid, float actTime)
    {
        _turnBuffer[uid] = _turnBuffer.GetValueOrDefault(uid) + Mathf.Max(actTime, 0.01f);
    }

    private static void ApplyTurnBudget()
    {
        if (!Activated || _turnBuffer.Count == 0) {
            return;
        }

        if (EClass.game?.activeZone?.map is null) {
            return;
        }

        var target = _turnBuffer.Values.Max();
        var grant = target - _turnBuffered;
        if (grant <= 0f) {
            return;
        }

        _turnBuffered = target;
        foreach (var chara in EClass._map.charas) {
            if (chara.IsPC || chara.ai is GoalRemote) {
                continue;
            }

            chara.roundTimer += grant;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
    private static void CapturePcTurnCount(Chara __instance, out int __state)
    {
        __state = Activated && __instance.IsPC ? EClass.player.stats.turns : -1;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
    private static void ReportPcTurnConsumed(Chara __instance, int __state)
    {
        if (__state < 0 || Phase != CombatPhase.Executing) {
            return;
        }

        // idle stats.turns++
        if (EClass.player.stats.turns == __state) {
            return;
        }

        _pcActedThisRound = true;

        if (NetSession.Instance.Connection is ElinNetHost) {
            ReportPlayerTurn(__instance.uid, __instance.actTime);
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(GameUpdater.CharaUpdater), nameof(GameUpdater.CharaUpdater.FixedUpdate))]
    private static IEnumerable<CodeInstruction> OnCharaUpdaterAccumulate(
        IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchStartForward(
                new OperandContains(OpCodes.Stfld, nameof(Card.roundTimer)))
            .EnsureValid("CharaUpdater.FixedUpdate accumulate roundTimer")
            .SetInstructionAndAdvance(
                Transpilers.EmitDelegate(AccumulateRoundTimer))
            .InstructionEnumeration();
    }

    private static void AccumulateRoundTimer(Chara chara, float value)
    {
        // non remote
        if (Activated && NetSession.Instance.Connection is ElinNetHost && chara is { IsPC: false, ai: not GoalRemote }) {
            return;
        }

        chara.roundTimer = value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AIAct), nameof(AIAct.Tick))]
    private static bool PreventImmediateAITick()
    {
        return !Paused;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActPlan.Item), nameof(ActPlan.Item.Perform))]
    private static bool QueueActWhileDeciding(ActPlan.Item __instance, ref bool __result)
    {
        if (!Activated) {
            return true;
        }

        var act = __instance.act;
        var pos = __instance.pos;
        if (!act.IsAct || pos is null) {
            return true;
        }

        // one action per round
        if (Phase == CombatPhase.Executing) {
            if (!__instance.cc.IsPC) {
                return true;
            }

            if (!_executingBlockNotified) {
                _executingBlockNotified = true;
                Msg.SayGod("emp_ui_combat_wait".lang());
            }

            __result = false;
            return false;
        }

        var cc = __instance.cc;
        var dist = cc.pos.Distance(pos);
        var canInteractNeighbor = dist == 1 && cc.CanInteractTo(pos);
        if (act.PerformDistance != -1 && (dist > act.PerformDistance || (dist == 1 && !canInteractNeighbor))) {
            // wrap DynamicAIAct
            return true;
        }

        var tc = __instance.tc;
        var tp = pos.Copy();
        Act.CC = cc;
        // no pos
        cc.SetAIImmediate(new DynamicAIAct(act.GetText(), () => act.Perform(cc, tc, tp)));

        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GoalManualMove), nameof(GoalManualMove.TryMove))]
    private static bool LimitManualMoveStep(ref bool __result)
    {
        return GateManualMove(ref __result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GoalManualMove), nameof(GoalManualMove.TryAltMove))]
    private static bool LimitManualAltMoveStep(ref bool __result)
    {
        return GateManualMove(ref __result);
    }

    private static bool GateManualMove(ref bool __result)
    {
        if (Phase != CombatPhase.Executing || !_pcActedThisRound) {
            return true;
        }

        EClass.player.nextMove = Vector2.zero;
        __result = false;
        return false;
    }
}