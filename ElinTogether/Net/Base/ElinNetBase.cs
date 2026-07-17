using System.Text;
using ElinTogether.Helper;
using ElinTogether.Helper.Steam;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using ReflexCLI.UI;
using Steamworks;
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
    protected EGui? DebugProgress;

    public abstract bool IsHost { get; }

    public bool IsClient => !IsHost;

    public bool IsConnected => Socket.IsConnected;

    private void Awake()
    {
        Initialize();
        SourceValidation.BuildActMapping();

#if !DEBUG
        if (!HarmonyLib.Harmony.HasAnyPatches(ModInfo.Guid)) {
            EmpMod.SharedHarmony.PatchAll(EmpMod.Assembly);
        }
#endif
    }

    private void Update()
    {
        Scheduler.Tick();
        Socket.Poll();

        if (Input.GetKeyDown(EmpConfig.Client.PingKeybind.Value) && !ui.BlockActions) {
            var point = Scene.HitPoint;
            if (point is not null) {
                Delta.AddRemote(PingPointDelta.Ping(point));
            }
        }
    }

    private void OnDestroy()
    {
        Stop();
        Socket.Dispose();

        SteamCallback<SteamNetConnectionStatusChangedCallback_t>.Shutdown();
        NetSession.Instance.Lobby.Shutdown();
        SteamUserName.Shutdown();

#if !DEBUG
        EmpMod.SharedHarmony.UnpatchSelf();
#endif
    }

    protected abstract void OnPeerConnected(ISteamNetPeer peer);

    protected abstract void OnPeerDisconnected(ISteamNetPeer peer, string reason);

    protected abstract void RegisterPackets();

    protected abstract void DisconnectInactive();

    protected void Initialize()
    {
        if (_initialized) {
            return;
        }

        Router.OnPeerConnectedEvent += OnPeerConnected;
        Router.OnPeerDisconnectedEvent += OnPeerDisconnected;

        Socket.Initialize(Router);

        Session.LocalPeerUid = (ulong)SteamUser.GetSteamID();

        RegisterPackets();

        CreateValidation();

        _initialized = true;
    }

    internal virtual void Stop()
    {
        if (ReflexUIManager.IsConsoleOpen()) {
            ReflexUIManager.StaticClose();
        }

        Socket.Stop();
        DebugProgress?.Kill();
    }

    protected string BuildDebugInfo()
    {
        var sb = new StringBuilder();

        var peers = Socket.Peers;
        for (var i = 0; i < peers.Count; ++i) {
            var peer = peers[i];

            sb.AppendLine(peer.Colorize(peer.Name));
            sb.AppendLine(peer.Stat.ToString());
        }

        sb.Append(Delta);

        return sb.ToString();
    }
}