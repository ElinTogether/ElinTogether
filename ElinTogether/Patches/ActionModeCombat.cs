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

    private static AIAct? _pendingAi;
    private static Vector2 _pendingMoveDir;
    private static bool _applyingPending;
    private static bool _applyPendingQueued;
    private static bool _cancelRequested;
    private static bool? _lastReportedReady;

    internal static Dictionary<int, bool> EnemyVisibility { get; } = [];

    private static readonly List<int> _keysToRemove = [];
    internal static CombatPhase Phase { get; private set; }
    internal static bool Activated => Phase != CombatPhase.Inactive;
    internal static bool Paused => Phase == CombatPhase.Deciding;
    internal static bool WaitForSelf { get; private set; }

    internal static bool SelfDecided => _pendingAi is not null || !EClass.pc.HasNoGoal;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.OnUpdate))]
    internal static void CheckIfPauseNeeded()
    {
        if (NetSession.Instance.Connection is not { } connection) {
            ChangePhaseLocal(CombatPhase.Inactive);
            return;
        }

        // alone: no round to wait for. The visibility timer is left due so the first look once somebody is there
        // is not half a second late
        if (!NetCompany.HasCompany) {
            // what the others saw before they left is not true any more (cheap when empty)
            EnemyVisibility.Clear();
            _visibilityTimer = VisibilityRefreshInterval;
            ChangePhaseLocal(CombatPhase.Inactive);
            return;
        }

        // (runs every frame: no allocation, nothing to drop when nobody was seen)
        if (EnemyVisibility.Count > 0) {
            var current = NetSession.Instance.CurrentPlayers;
            _keysToRemove.Clear();
            foreach (var key in EnemyVisibility.Keys) {
                var found = false;
                for (var i = 0; i < current.Count; i++) {
                    if (current[i].CharaUid == key) {
                        found = true;
                        break;
                    }
                }

                if (!found) {
                    _keysToRemove.Add(key);
                }
            }

            foreach (var key in _keysToRemove) {
                EnemyVisibility.Remove(key);
            }
        }

        RefreshSelfVisibility(connection);
        UpdatePendingDecision(connection);

        if (connection.IsHost) {
            ApplyTurnBudget();
            HostPhaseUpdate(connection, NetSession.Instance.CurrentPlayers.ToList());
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

    private static void UpdatePendingDecision(ElinNetBase net)
    {
        if (_applyPendingQueued && Phase == CombatPhase.Executing) {
            _applyPendingQueued = false;
            ApplyPendingDecision(net);
        }

        if (Phase != CombatPhase.Deciding) {
            return;
        }

        if (_cancelRequested) {
            _cancelRequested = false;
            // progress
            if (!EClass.pc.HasNoGoal && EClass.pc.ai.Current is not AIProgress) {
                EClass.pc.SetAI(new NoGoal());
            }
        }

        if (net.IsClient) {
            var ready = SelfDecided;
            if (_lastReportedReady != ready) {
                _lastReportedReady = ready;
                EmpLog.Debug("Combat ready report {CombatDecided}", ready);
                net.Delta.AddRemote(new CombatReadyDelta {
                    Ready = ready,
                });
            }
        }
    }

    private static void ApplyPendingDecision(ElinNetBase net)
    {
        var pending = _pendingAi;
        _pendingAi = null;

        if (pending is not null && !EClass.pc.isDead) {
            _applyingPending = true;
            try {
                EmpLog.Debug("Combat applying pending decision {ActType}", pending.GetType().Name);
                Act.CC = EClass.pc;
                if (pending is GoalManualMove) {
                    EClass.player.nextMove = _pendingMoveDir;
                }

                EClass.pc.SetAIImmediate(pending);
            } finally {
                _applyingPending = false;
            }
        }

        // nothing
        if (net.IsClient && EClass.pc.HasNoGoal) {
            _lastReportedReady = false;
            net.Delta.AddRemote(new CombatReadyDelta {
                Ready = false,
            });
        }
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

    /// <summary>
    ///     The round state is static and Game.OnUpdate does not run on the title screen: a guest whose link dropped
    ///     during a Deciding round kept it into its next session, where the host (Inactive, so silent) never ended it
    ///     and every step of the guest was held as a pending decision (issue 65). Called when the connection goes.
    /// </summary>
    internal static void ResetForNewSession()
    {
        if (Phase != CombatPhase.Inactive) {
            EmpLog.Debug("Combat phase {CombatPhase} reset with the connection", Phase);
        }

        Phase = CombatPhase.Inactive;
        _decided.Clear();
        _done.Clear();
        _turnBuffer.Clear();
        _turnBuffered = 0f;
        _pcActedThisRound = false;
        _pendingAi = null;
        _applyPendingQueued = false;
        _cancelRequested = false;
        _lastReportedReady = null;
        _executingTimer = 0f;
        _executingBlockNotified = false;
        _lastPlayerCount = 0;
        WaitForSelf = false;
        EnemyVisibility.Clear();
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
        _cancelRequested = false;
        _lastReportedReady = null;
        // entering Executing keeps the pending decision for the deferred apply
        _applyPendingQueued = phase == CombatPhase.Executing;
        if (phase != CombatPhase.Executing) {
            _turnBuffer.Clear();
            _turnBuffered = 0f;
            _pcActedThisRound = false;
            _pendingAi = null;
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
            case CombatPhase.Deciding:
                if (!EClass.pc.HasNoGoal) {
                    _pendingAi = EClass.pc.ai;
                    _applyingPending = true;
                    try {
                        EClass.pc.SetAI(new NoGoal());
                    } finally {
                        _applyingPending = false;
                    }
                }
                break;
            case CombatPhase.Executing:
                _done.Clear();
                break;
        }
    }

    private static void HostPhaseUpdate(ElinNetBase net, List<NetPeerState> players)
    {
        // rounds for everyone, unless every fight runs on its player's own time
        var active = NetSession.Instance.Rules.UseTurnBasedCombat &&
                     !NetSession.Instance.Rules.UsePlayerCombatTime &&
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
                var allDecided = SelfDecided && players.All(p =>
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

        if (!SelfDecided) {
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
        // on a player's clock: only that player's turns give it time, see PlayerCombatTime
        if (PlayerCombatTime.IsBound(chara)) {
            return;
        }

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
    [HarmonyPatch(typeof(Chara), nameof(Chara.SetAIImmediate))]
    private static bool CaptureDecisionWhileDeciding(Chara __instance, AIAct g)
    {
        if (Phase != CombatPhase.Deciding || _applyingPending || !__instance.IsPC || g.IsNoGoal) {
            return true;
        }

        if (_pendingAi?.GetType() != g.GetType()) {
            EmpLog.Debug("Combat pending decision {ActType}", g.GetType().Name);
        }

        _pendingAi = g;
        if (g is GoalManualMove) {
            _pendingMoveDir = EClass.player.nextMove;
        }

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AIAct), nameof(AIAct.Cancel))]
    private static void RevokeDecisionOnCancel(AIAct __instance)
    {
        if (Phase != CombatPhase.Deciding || _applyingPending || __instance.owner is not { IsPC: true }) {
            return;
        }

        if (_pendingAi is not null) {
            _pendingAi = null;
            EmpLog.Debug("Combat pending decision revoked");
        }

        _cancelRequested = true;
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