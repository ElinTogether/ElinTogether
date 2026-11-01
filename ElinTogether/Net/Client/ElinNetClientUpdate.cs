using ElinTogether.Models;
using ElinTogether.Patches;

namespace ElinTogether.Net;

internal partial class ElinNetClient
{
    private WorldStateSnapshot? _lastTick;
    private bool _pauseUpdate;

    /// <summary>
    ///     Send out local deltas and self snapshot to remote host
    /// </summary>
    internal void WorldStateDeltaUpdate()
    {
        if (_pauseUpdate) {
            return;
        }

        // send out delta
        if (!Delta.HasPendingOut) {
            return;
        }

        if (Delta.FlushOutBuffer() is not { Count: > 0 } deltaList) {
            return;
        }

        Host.Send(new WorldStateDeltaList {
            DeltaList = deltaList,
        });
    }

    /// <summary>
    ///     Process local or deferred deltas received from host
    /// </summary>
    internal void WorldStateDeltaProcess()
    {
        if (!Delta.HasPendingIn) {
            return;
        }

        Delta.ProcessLocalBatch(this);
    }

    /// <summary>
    ///     Net event: Apply client-side reconciliation from host
    /// </summary>
    private void OnWorldStateSnapshot(WorldStateSnapshot snapshot)
    {
        if (_lastTick is not null) {
            var dropped = snapshot.ServerTick - _lastTick.ServerTick;
            if (dropped > 1) {
                EmpLog.Warning("Falling behind with {DroppedTicks} dropped ticks",
                    dropped);
            }
        }

        _lastTick = snapshot;
        Session.Tick = snapshot.ServerTick;

        if (core.game?.activeZone?.map is null) {
            return;
        }

        // apply world state
        snapshot.ApplyReconciliation();

        // send back client state
        Host.Send(CharaStateSnapshot.CreateSelf());
    }

    /// <summary>
    ///     Net event: Apply delta changes from host
    /// </summary>
    private void OnWorldStateDeltaResponse(WorldStateDeltaList response)
    {
        if (Session.IsAway && !IsZoneSession) {
            ApplyChatWhileAway(response);
            return;
        }

        foreach (var delta in response.DeltaList) {
            Delta.AddLocal(delta);
        }
    }

    /// <summary>
    ///     Net event: Apply new session remote player states
    /// </summary>
    private void OnSessionStatesUpdate(SessionPlayersSnapshot states)
    {
        states.Apply();
    }

    /// <summary>
    ///     Net event: Apply new session rules
    /// </summary>
    /// <param name="rules"></param>
    private void OnSessionRulesUpdate(NetSessionRules rules)
    {
        // a peer-hosted world: the rules of whoever keeps it are the signed ones, or this game leaves it (the keeper's
        // own game too, a guest of its hidden server like everyone)
        if (Helper.PeerHosting.Active && Helper.PeerRules.Check(rules) is { } differ) {
            Helper.CrossCheck.Rules("rules", differ);
            LeaveKeeper(differ);
            return;
        }

        // the first rules of this host are the snapshot of the join; after that, a change is told in one line and
        // the card "This world's rules" is drawn again (before them the session holds this game's own config)
        var changed = Components.SettingsTable.NoticeChanges(_rulesReceived ? NetSession.Instance.Rules : null, rules);
        _rulesReceived = true;
        NetSession.Instance.Rules = rules;
        Helper.ServerInfo.Arrived();
        if (changed) {
            Components.SettingsTable.MarkDirty();
        }
    }

    private bool _rulesReceived;

    /// <summary>
    ///     This keeper of a peer-hosted world does not play its signed rules (or build): left, the reason shown by the
    ///     disconnection's own window (OnPeerDisconnected; a reason of ours, so no reconnection is tried). The
    ///     store hears of it with P3 (report)
    /// </summary>
    internal void LeaveKeeper(string why)
    {
        var reason = Common.EmpDisconnectInfo.With(Common.EmpDisconnectInfo.PeerRules, why);
        EmpLog.Warning("Peer-hosted world: the keeper {@Peer} is left, its rules are not the signed ones: {Why}", Host, why);
        Socket.Disconnect(Host, reason);
    }

#region Scheduler Jobs

    /// <summary>
    ///     Subscribe all scheduler jobs and reset pause state
    /// </summary>
    public void StartWorldStateUpdate()
    {
        // 50hz delta dispatch
        Scheduler.Subscribe(SynchronizationContext.AllowDeltaSending, 50f);

        _pauseUpdate = false;
    }

    /// <summary>
    ///     Unsubscribe all scheduler jobs and reset pause state
    /// </summary>
    public void StopWorldStateUpdate()
    {
        Scheduler.Unsubscribe(SynchronizationContext.AllowDeltaSending);

        _pauseUpdate = false;

        EmpLog.Debug("Stopping client state update");
    }

    /// <summary>
    ///     Pause sending out deltas, *but they still accumulate*
    /// </summary>
    public void PauseWorldStateUpdate()
    {
        _pauseUpdate = true;

        EmpLog.Debug("Pausing client state update");
    }

    /// <summary>
    ///     Resume sending out deltas
    /// </summary>
    public void ResumeWorldStateUpdate(bool clearDelta = false)
    {
        _pauseUpdate = false;

        EmpLog.Debug("Resuming client state update");

        if (clearDelta) {
            Delta.ClearOut();
        }
    }

#endregion
}