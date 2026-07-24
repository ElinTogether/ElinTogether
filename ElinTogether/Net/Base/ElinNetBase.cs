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

    private void Awake()
    {
        Initialize();

#if !DEBUG
        if (!HarmonyLib.Harmony.HasAnyPatches(ModInfo.Guid)) {
            EmpMod.SharedHarmony.PatchAll(EmpMod.Assembly);
        }
#endif
    }

    protected virtual void Update()
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

#if !DEBUG
        EmpMod.SharedHarmony.UnpatchSelf();
#endif
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
}