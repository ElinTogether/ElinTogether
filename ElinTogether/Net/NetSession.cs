using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net.Steam;
using Object = UnityEngine.Object;

namespace ElinTogether.Net;

public class NetSession : EClass
{
    public enum Mode : byte
    {
        None = 0,

        /// <summary>
        ///     player in the map alone, simulates <br />
        ///     TODO: full sync for now
        /// </summary>
        PartialSync,

        /// <summary>
        ///     players share the same map, first player simulates
        ///     host takes over whenever possible
        /// </summary>
        FullSync,
    }

    public static NetSession Instance => field ??= new();

    public Mode SyncMode { get; private set; } = Mode.None;
    public ElinNetBase? Connection { get; private set; }
    public Chara? Player { get; internal set; }
    public int SharedSpeed { get; internal set; }
    public Zone? CurrentZone { get; internal set; }
    public int Tick { get; internal set; }
    public ulong SessionId { get; internal set; }
    public NetSessionRules Rules { get; internal set; } = NetSessionRules.Default;

    public List<NetPeerState> CurrentPlayers => field ??= [];
    public SteamNetLobbyManager Lobby => field ??= new();
    public NetPeerState? Self { get; internal set; }
    public ulong? LocalPeerUid { get; internal set; }

    public bool HasActiveConnection => Connection != null && Connection.IsConnected;
    public bool IsHost => Connection?.IsHost is not false;
    public bool IsClient => !IsHost;
    public bool ShouldSimulate => IsHost || SyncMode == Mode.PartialSync;

    public void RemoveComponent()
    {
        if (Connection != null) {
            if (!Connection.IsHost && core.IsGameStarted) {
                ui.RemoveLayers();
                game.Kill();
            }

            Object.Destroy(Connection);

            EmpLog.Debug("Removed connection component of {NetType}",
                Connection.GetType().Name);
        }

        Connection = null;
        Tick = 0;
        Self = null;
        CurrentPlayers.Clear();

        ResourceFetch.InvalidateTemp();

        SwitchSyncMode(Mode.None);

        EmpLog.Information("Connection component removed, session reset to None");
    }

    public T InitializeComponent<T>() where T : ElinNetBase
    {
        RemoveComponent();

        Lobby.Reset();

        Connection = EmpMod.Instance.gameObject.AddComponent<T>();

        EmpLog.Debug("Initialized new connection component of {NetType}",
            typeof(T).Name);

        return (Connection as T)!;
    }

    public void SwitchSyncMode(Mode mode)
    {
        if (SyncMode == mode) {
            return;
        }

        EmpLog.Debug("Switched SyncMode to {SyncMode}", mode);

        SyncMode = mode;
    }
}