using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class SleepSynchronizationContext : SynchronizationContext
{
    private static readonly HashSet<int> _ready = [];
    private static readonly HashSet<int> _lastReady = [];
    private static bool _sleepStarted;
    private static bool _cancelSent;

    private static readonly AccessTools.FieldRef<LayerSleep, int> _minRef =
        AccessTools.FieldRefAccess<LayerSleep, int>("min");
    private static readonly AccessTools.FieldRef<LayerSleep, int> _maxMinRef =
        AccessTools.FieldRefAccess<LayerSleep, int>("maxMin");
    private static readonly AccessTools.FieldRef<LayerSleep, int> _hoursRef =
        AccessTools.FieldRefAccess<LayerSleep, int>("hours");

    internal static bool AllPlayersReady { get; private set; } = true;

    internal static void Update()
    {
        if (pc?.conSleep is null) {
            _cancelSent = false;
        }

        if (NetSession.Instance.Connection is not ElinNetHost host || pc is null) {
            _ready.Clear();
            _lastReady.Clear();
            _sleepStarted = false;
            AllPlayersReady = true;
            return;
        }

        _ready.Clear();
        var alive = 0;
        foreach (var netPlayer in NetSession.Instance.CurrentPlayers) {
            var chara = netPlayer.CharaUid == pc.uid ? pc : netPlayer.FindChara();
            // 死是凉爽的夏夜，可供人无忧的安眠
            if (chara is null or { isDead: true }) {
                continue;
            }

            alive++;
            if (chara.conSleep is not null) {
                _ready.Add(netPlayer.Index);
            }
        }

        AllPlayersReady = _ready.Count >= alive;

        if (_sleepStarted || alive < 2) {
            if (_sleepStarted && _ready.Count == 0) {
                _sleepStarted = false;
            }

            _lastReady.Clear();
            _lastReady.UnionWith(_ready);
            return;
        }

        foreach (var index in _ready) {
            if (!_lastReady.Contains(index)) {
                Announce(host, index, true, alive);
            }
        }

        foreach (var index in _lastReady) {
            if (!_ready.Contains(index)) {
                Announce(host, index, false, alive);
            }
        }

        _lastReady.Clear();
        _lastReady.UnionWith(_ready);
    }

    private static void Announce(ElinNetHost host, int playerIndex, bool ready, int total)
    {
        EmpLog.Debug("Sleep vote from {PeerIndex}, ready {SleepReady}, {SleepReadyCount} of {PlayerCount}",
            playerIndex, ready, _ready.Count, total);

        var delta = new SleepReadyDelta {
            PlayerIndex = playerIndex,
            Ready = ready,
            ReadyCount = _ready.Count,
            TotalCount = total,
        };
        delta.Play();
        host.Delta.AddRemote(delta);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.CanSleep))]
    internal static void AllowPartySleep(Chara __instance, ref bool __result)
    {
        if (__result || NetSession.Instance.Connection is null || !__instance.IsPC) {
            return;
        }

        if (_zone.events.GetEvent<ZoneEventQuest>() is not null) {
            return;
        }

        // are you tired? yes you are
        __result = true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Sleep))]
    internal static bool OnPcSleep(Chara __instance)
    {
        if (NetSession.Instance.Connection is not ElinNetClient client || !__instance.IsPC) {
            return true;
        }

        // forced
        if (ElinDelta.IsApplying) {
            return false;
        }

        if (__instance.conSleep is not null) {
            return false;
        }

        EmpLog.Debug("Requesting party sleep");

        client.Delta.AddRemote(new SleepRequestDelta());
        WidgetPopText.Say("emp_ui_sleep_request".Loc());
        return false;
    }

    private static bool InSleepWaitWindow(Chara chara)
    {
        if (chara.conSleep is not { pcSleep: <= 1 } || ui.GetLayer<LayerSleep>() is not null) {
            return false;
        }

        return NetSession.Instance.Connection is not ElinNetHost || !AllPlayersReady;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConSleep), nameof(ConSleep.ConsumeTurn), MethodType.Getter)]
    internal static void FreezeWaitTurns(ConSleep __instance, ref bool __result)
    {
        if (!__result || NetSession.Instance.Connection is null) {
            return;
        }

        if (__instance.owner is { IsPC: true } owner && InSleepWaitWindow(owner)) {
            __result = false;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.SetAIImmediate))]
    internal static bool CancelSleepOnAction(Chara __instance, AIAct g)
    {
        if (NetSession.Instance.Connection is not { } connection || !__instance.IsPC || g.IsNoGoal) {
            return true;
        }

        if (ElinDelta.IsApplying || !InSleepWaitWindow(__instance)) {
            return true;
        }

        switch (connection) {
            case ElinNetHost:
                __instance.conSleep?.Kill();
                break;
            case ElinNetClient client when !_cancelSent:
                // client wait for delta
                _cancelSent = true;
                EmpLog.Debug("Requesting sleep cancel");
                client.Delta.AddRemote(new SleepCancelDelta());
                break;
        }

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ConSleep), nameof(ConSleep.Tick))]
    internal static bool GateSleepTick(ConSleep __instance)
    {
        if (NetSession.Instance.Connection is not { } connection || __instance.owner is not { } owner) {
            return true;
        }

        if (connection.IsClient) {
            return !owner.IsPlayer;
        }

        if (owner.IsRemotePlayer) {
            return false;
        }

        return !owner.IsPC || __instance.pcSleep != 1 || AllPlayersReady;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LayerSleep), nameof(LayerSleep.Sleep))]
    internal static void OnSleepStart(int _hours)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        _sleepStarted = true;

        EmpLog.Debug("Party sleep started {SleepHours}", _hours);

        host.Delta.AddRemote(new SleepStartDelta {
            Hours = _hours,
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.OnSleep), typeof(int), typeof(int), typeof(bool))]
    internal static void OnHostSleep(Chara __instance, int power, int days)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        if (!__instance.IsPC) {
            return;
        }

        EmpLog.Debug("Zzz party sleep {SleepPower}", power);

        host.Delta.AddRemote(new CharaSleepDelta {
            Power = power,
            Days = days,
        });

        foreach (var chara in host.ActiveRemoteCharas.Values) {
            if (chara.conSleep is { } sleep) {
                sleep.Kill();
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LayerSleep), nameof(LayerSleep.Advance))]
    internal static bool OnClientAdvance(LayerSleep __instance)
    {
        if (NetSession.Instance.Connection is not ElinNetClient) {
            return true;
        }

        if (_minRef(__instance) > _maxMinRef(__instance) + 600) {
            EmpLog.Warning("Sleep layer timed out waiting for host wake");
            CloseSleepLayer(__instance);
        } else {
            _minRef(__instance) += 10;
        }

        return false;
    }

    internal static void CloseSleepLayerIfOpen()
    {
        if (ui.GetLayer<LayerSleep>() is { } layer) {
            CloseSleepLayer(layer);
        }
    }

    private static void CloseSleepLayer(LayerSleep layer)
    {
        if (_maxMinRef(layer) == int.MaxValue) {
            return;
        }

        _maxMinRef(layer) = int.MaxValue;
        layer.CancelInvoke();
        Msg.Say("slept", _hoursRef(layer).ToString());
        ui.ShowCover();
        TweenUtil.Delay(layer.hideDelay, () => ui.HideCover(layer.coverHide, layer.Close));
    }
}