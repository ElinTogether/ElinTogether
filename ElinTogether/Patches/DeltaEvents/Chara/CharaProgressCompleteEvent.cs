using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaProgressCompleteEvent
{
    private static List<ElinDelta> _deltaList = [];
    private static List<ElinDelta>? _sideDeltaList;
    internal static Chara? Chara { get; private set; }
    internal static bool IsHappening { get; private set; }
    internal static AIAct? Action { get; private set; }

    // host packs side delta during ProgressComplete, clients replay
    // pick is for remote players only
    internal static bool ShouldPack(bool remoteOnly)
    {
        if (!IsHappening || NetSession.Instance.Connection is not { IsHost: true }) {
            return false;
        }

        return !remoteOnly || Chara is { IsRemotePlayer: true };
    }

    internal static void Pack(ElinDelta delta)
    {
        _deltaList.Add(delta);
    }

    internal static ScopeExit CollectBuildSideEffects(List<ElinDelta> into)
    {
        _sideDeltaList = into;
        return new() {
            OnExit = () => _sideDeltaList = null,
        };
    }

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer
            .FindAllOverrides(typeof(AIAct), nameof(AIAct.OnProgressComplete))
            .Where(mi => typeof(AIProgress).IsAssignableFrom(mi.DeclaringType) || mi.DeclaringType == typeof(TaskBuild));
    }

    [HarmonyPrefix]
    internal static bool OnProgressComplete(AIAct __instance)
    {
        if (NetSession.Instance.Connection is not { } connection || __instance.owner is null) {
            return true;
        }

        Chara = __instance.owner;
        Action = __instance;
        IsHappening = true;

        if (__instance is not TaskBuild taskBuild) {
            return true;
        }

        if (connection.IsClient && Chara.IsPC && !ElinDelta.IsApplying && taskBuild.held is not null) {
            connection.Delta.AddRemote(CharaBuildDelta.Create(taskBuild));
        }

        return connection.IsHost || ElinDelta.IsApplying;
    }

    [HarmonyPostfix]
    internal static void OnProgressCompleteEnd(AIAct __instance)
    {
        Chara = null;
        Action = null;
        IsHappening = false;

        var captured = _deltaList;
        _deltaList = [];

        if (__instance.owner is null) {
            return;
        }

        if (__instance is TaskBuild taskBuild) {
            if (NetSession.Instance.Connection is not ElinNetHost buildHost) {
                return;
            }

            if (_sideDeltaList is { } collector) {
                collector.AddRange(captured);
                return;
            }

            if (taskBuild.owner.IsPC && taskBuild.held is not null && !ElinDelta.IsApplying) {
                buildHost.Delta.AddRemote(CharaBuildDelta.Create(taskBuild, captured));
                return;
            }

            foreach (var delta in captured) {
                buildHost.Delta.AddRemote(delta);
            }

            return;
        }

        // only host can complete progress
        if (NetSession.Instance.Connection is not ElinNetHost host || __instance is DelegateProgress) {
            return;
        }

        if (__instance.parent?.GetType() is not { } actType ||
            !ActMappingValidator.Default.ActToIdMapping.TryGetValue(actType, out var actId)) {
            return;
        }

        // due to randomness in max progress
        // remote needs to be notified that a remote task is completed before starting anew
        host.Delta.AddRemote(new CharaProgressCompleteDelta {
            Owner = __instance.owner,
            CompletedActId = actId,
            DeltaList = captured,
        });
    }

    [HarmonyFinalizer]
    internal static void OnProgressCompleteCleanup(Exception? __exception)
    {
        if (__exception is null || !IsHappening) {
            return;
        }

        EmpLog.Warning("Progress complete of {OwnerUid} threw, discarding {ReplayCount} packed deltas",
            Chara?.uid ?? -1, _deltaList.Count);

        Chara = null;
        Action = null;
        IsHappening = false;
        _deltaList = [];
    }
}