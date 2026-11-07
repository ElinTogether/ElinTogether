using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using ReflexCLI.UI;
using UnityEngine;

namespace ElinTogether.Net;

public abstract partial class ElinNetBase : EMono
{
    protected static readonly NetSession Session = NetSession.Instance;
    public readonly ElinDeltaManager Delta = new();
    protected readonly SteamNetMessageRouter Router = new();
    protected readonly TickScheduler Scheduler = new();
    protected readonly SteamNetManager Socket = new();
    private bool _initialized;

    public abstract bool IsHost { get; }

    public bool IsClient => !IsHost;

    public bool IsConnected => Socket.IsConnected;

    /// <summary>
    ///     Session limited to an away zone, next to the link with the host, see NetSession.ZoneSession
    /// </summary>
    public bool IsZoneSession { get; internal set; }

    /// <summary>
    ///     A zone session only closes itself, the host session resets everything
    /// </summary>
    protected void EndSession(string reason)
    {
        if (IsZoneSession) {
            Session.EndZoneSession(this, reason);
        } else {
            Session.ResetSession();
        }
    }

#if DEBUG
    // bench: a start as slow as a release build's on a slow PC (it patches the whole mod there, issue #24)
    internal static int SlowStartMs;
#endif

    private void Awake()
    {
        Initialize();
#if DEBUG
        if (SlowStartMs > 0) {
            System.Threading.Thread.Sleep(SlowStartMs);
        }
#endif

        // (a release build, or the bench with -empreleasepatches: the session's patches come with it)
        if (EmpMod.PatchesPerSession && !HarmonyLib.Harmony.HasAnyPatches(ModInfo.Guid)) {
            EmpMod.PatchAll();
        }
    }

    protected virtual void Update()
    {
        if (NetShutdown.IsQuitting) {
            return;
        }

        Scheduler.Tick();
        Patches.FactionBonusPatch.Tick();
        Socket.Poll();
        NetMetrics.Tick(Socket);
        DialogFlagSync.Tick();
        Patches.InvSettingsWatch.Tick();
#if DEBUG
        EmpBotLauncher.Tick();
#endif

        // an away player is alone in its zone, there is nobody to ping, unless in a zone session
        if ((!Session.IsAway || IsZoneSession) && Input.GetKeyDown(EmpConfig.Client.PingKeybind.Value) && !ui.BlockActions) {
            var point = Scene.HitPoint;
            if (point is not null) {
                Delta.AddRemote(PingPointDelta.Ping(point));
            }
        }
    }

    private void OnDestroy()
    {
        if (NetShutdown.IsQuitting) {
            return;
        }

        // Destroy is deferred: when this component is no longer the session's (replaced since), closing its link
        // must not reach the Session now owned by the new one (bench 2026-10-29: new client reset by the old's close)
        if (Session.Transport != this && Session.ZoneSession != this) {
            Router.OnPeerConnectedEvent -= OnPeerConnected;
            Router.OnPeerDisconnectedEvent -= OnPeerDisconnected;
        }

        Stop();
        Socket.Dispose();

        // only when the last one goes: a zone session that closes leaves the link with the host, which went on
        // without a single patch (nothing sent, nothing applied, map changes not seen) until the next component.
        // (those marked AlwaysPatched stay, see EmpMod.PatchesPerSession)
        var transport = Session.Transport;
        var zoneSession = Session.ZoneSession;
        // (not while the guest joins the same game again by itself: taking the patches away then putting them back froze
        // the game 30 s, then 25 s more, before the first try, net_fault_check 2026-11-04; NetReconnect takes them away
        // if it gives up)
        if (EmpMod.PatchesPerSession && !NetReconnect.Active && !KeepPatches && (transport == null || transport == this) &&
            (zoneSession == null || zoneSession == this)) {
            EmpMod.SharedHarmony.UnpatchSelf();
        }
    }

    /// <summary>
    ///     This game leaves one session for another it opens itself in a moment (a takeover of the peer-hosted world:
    ///     out of the lost keeper's game, into its own hidden server's): the session's patches stay in between, as while
    ///     NetReconnect joins again. Taking them away and putting them back froze the game 20-30 s (crash takeover
    ///     2026-11-07: 26 s between the take and the hidden server's start). Cleared by <see cref="ReleasePatches" />
    /// </summary>
    internal static bool KeepPatches { get; set; }

    /// <summary>
    ///     The new session came, or will not: the patches go if no session holds them
    /// </summary>
    internal static void ReleasePatches()
    {
        KeepPatches = false;
        var session = NetSession.Instance;
        if (EmpMod.PatchesPerSession && !NetReconnect.Active && session.Transport == null && session.ZoneSession == null &&
            HarmonyLib.Harmony.HasAnyPatches(ModInfo.Guid)) {
            EmpMod.SharedHarmony.UnpatchSelf();
        }
    }

    protected abstract void OnPeerConnected(ISteamNetPeer peer);

    protected abstract void OnPeerDisconnected(ISteamNetPeer peer, string reason);

    protected abstract void RegisterPackets();

    protected void Initialize()
    {
        if (_initialized) {
            return;
        }

        Router.OnPeerConnectedEvent += OnPeerConnected;
        Router.OnPeerDisconnectedEvent += OnPeerDisconnected;

        Socket.Initialize(Router);

        RegisterPackets();

        CreateValidation();

        _initialized = true;
    }

    internal void Shutdown()
    {
        Socket.Shutdown();
    }

    internal virtual void Stop()
    {
        if (ReflexUIManager.IsConsoleOpen()) {
            ReflexUIManager.StaticClose();
        }

        Socket.Stop();
        StopDebugGui();
    }

    internal void DisconnectPeer(int peerIndex, string reason)
    {
        foreach (var peer in Socket.Peers) {
            if (peer.Id == peerIndex) {
                Socket.Disconnect(peer, reason);
                return;
            }
        }
    }

    internal void SendDeltaToAllExcept(int peerIndex, ElinDelta delta)
    {
        // (a relay of a story message: only to the players of that story, see TeamOwnStory)
        if (this is ElinNetHost host && Helper.TeamOwnStory.RouteOf(host, delta) is { } owner) {
            foreach (var (peer, uid) in host.PeerCharas()) {
                if (peer != peerIndex && Helper.StoryOwner.Of(uid) == owner) {
                    SendDeltaTo(peer, delta);
                }
            }

            return;
        }

        foreach (var peer in Socket.Peers) {
            if (peer.Id != peerIndex) {
                peer.Send(new WorldStateDeltaList {
                    DeltaList = [delta],
                });
            }
        }
    }

    internal IEnumerable<int> PeerIds => Socket.Peers.Select(p => p.Id);

    internal bool SendDeltaTo(int peerIndex, ElinDelta delta)
    {
        foreach (var peer in Socket.Peers) {
            if (peer.Id == peerIndex) {
                return peer.Send(new WorldStateDeltaList {
                    DeltaList = [delta],
                });
            }
        }

        return false;
    }
}