using System.Linq;
using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using Object = UnityEngine.Object;

using ElinTogether.Patches;

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

    private bool _resetting;

    public static NetSession Instance => field ??= new();

    public Mode SyncMode { get; private set; } = Mode.None;

    /// <summary>
    ///     The connection gameplay code synchronizes through <br />
    ///     the zone session while playing with others away from the host, null while alone in an away zone
    ///     (the game runs as single player there), the host session otherwise
    /// </summary>
    public ElinNetBase? Connection => ZoneSession ?? (IsAway ? null : Transport);

    /// <summary>
    ///     The network component linked to the host, regardless of away state
    /// </summary>
    public ElinNetBase? Transport { get; private set; }

    /// <summary>
    ///     Session limited to the away zone: host of it when other players visit, client of it when visiting
    ///     the zone of another player
    /// </summary>
    public ElinNetBase? ZoneSession { get; private set; }

    /// <summary>
    ///     Away in the zone of another player, which simulates it (zone session client, or leaving it)
    /// </summary>
    public bool IsGuest { get; internal set; }

    /// <summary>
    ///     This game simulates the away zone: holds its lease, sends its checkpoints
    /// </summary>
    public bool IsZoneAuthority => IsAway && !IsGuest;

    /// <summary>
    ///     Zone this client is leased and simulates on its own, away from the host
    /// </summary>
    public Zone? AwayZone { get; internal set; }

    public bool IsAway => AwayZone is not null;

    public Chara? Player { get; internal set; }
    public int SharedSpeed { get; internal set; }
    public Zone? CurrentZone { get; internal set; }
    public int Tick { get; internal set; }

    // currently, this is Steam LobbyData
    public ulong SessionId { get; internal set; }
    public NetSessionRules Rules { get; internal set; } = NetSessionRules.Default;

    public List<NetPeerState> CurrentPlayers => field ??= [];

    /// <summary>
    ///     The players a person sees in the menus: a dedicated server's own seat (its stand-in, Helper/ServerAvatar) is
    ///     the server itself, not a player (user 2026-10-30: "Elin Together" listed among the players)
    /// </summary>
    internal IEnumerable<NetPeerState> VisiblePlayers =>
        CurrentPlayers.Where(p => !(p.Index == 0 && Helper.ServerAvatar.IsUid(p.CharaUid)));

    /// <summary>
    ///     Every connected player of the server, on the host's map or away (user 2026-10-30: the players list showed only
    ///     the same map's), as the host last sent it (ServerRosterDelta); the host builds its own. Never the server's
    ///     stand-in. The menus read it; the sync reads <see cref="CurrentPlayers" />. Empty before the first one arrives
    /// </summary>
    internal List<RosterEntry> ServerRoster { get; private set; } = [];

    internal void SetServerRoster(List<RosterEntry> players)
    {
        ServerRoster = players.FindAll(p => !Helper.ServerAvatar.IsUid(p.CharaUid));
    }

    /// <summary>
    ///     How many players the server has: the roster, or this map's when none arrived yet (a host of an older version)
    /// </summary>
    internal int ServerPlayerCount => ServerRoster.Count > 0 ? ServerRoster.Count : VisiblePlayers.Count();

    public SteamNetLobbyManager Lobby => field ??= new();
    public NetPeerState? Self { get; internal set; }

    public bool HasActiveConnection => Transport != null && Transport.IsConnected;

    /// <summary>
    ///     In a multiplayer game: hosting (even before anyone joins) or connected to a host. HasActiveConnection is
    ///     false for a host alone, which locked the Team page of a freshly started server (user, 2026-10-29)
    /// </summary>
    public bool InGame => Transport is ElinNetHost || HasActiveConnection;
    public bool IsHost => Connection?.IsHost is not false;
    public bool IsClient => !IsHost;
    public bool ShouldSimulate => IsHost || SyncMode == Mode.PartialSync;

    public T InitializeZoneSession<T>() where T : ElinNetBase
    {
        RemoveZoneSession();

        var session = EmpMod.Instance.gameObject.AddComponent<T>();
        session.IsZoneSession = true;
        ZoneSession = session;

        EmpLog.Debug("Initialized zone session of {ConnectionType}",
            typeof(T).Name);

        return session;
    }

    /// <summary>
    ///     Close the zone session only, the link with the host stays
    /// </summary>
    public void RemoveZoneSession()
    {
        if (ZoneSession is not { } session) {
            return;
        }

        ZoneSession = null;
        Object.Destroy(session);

        // players of the zone session go with it
        Self = null;
        CurrentPlayers.Clear();

        EmpLog.Debug("Removed zone session of {ConnectionType}",
            session.GetType().Name);
    }

    /// <summary>
    ///     A zone session ended on its own (disconnect, timeout): close it and let the host link react
    /// </summary>
    internal void EndZoneSession(ElinNetBase session, string reason)
    {
        if (ZoneSession != session) {
            return;
        }

        RemoveZoneSession();
        (Transport as ElinNetClient)?.OnZoneSessionEnded(session, reason);
    }

    public void RemoveComponent()
    {
        RemoveZoneSession();
        AwayZone = null;
        IsGuest = false;

        if (Transport != null) {
            // before the links close: the guests hear that this game ends from the host itself
            (Transport as ElinNetHost)?.AnnounceLeaving();

            if (!Transport.IsHost && core.IsGameStarted) {
                ui.hud?.SetDragImage(null);
                ui.RemoveLayers();
                game.Kill();
                scene.Init(Scene.Mode.Title);
            }

            Object.Destroy(Transport);

            EmpLog.Debug("Removed connection component of {ConnectionType}",
                Transport.GetType().Name);
        }

        Transport = null;
        ActionModeCombat.ResetForNewSession();

        EmpLog.Information("Connection component removed");
    }

    public void ResetSession()
    {
        if (_resetting) {
            return;
        }

        _resetting = true;
        try {
            RemoveComponent();

            Tick = 0;
            SharedSpeed = 0;
            Self = null;
            CurrentPlayers.Clear();
            ServerRoster = [];
            Lobby.LeaveLobby();

            ResourceFetch.InvalidateTemp();

            SwitchSyncMode(Mode.None);

            EmpLog.Information("Session reset to None");
        } finally {
            _resetting = false;
        }
    }

    public T InitializeComponent<T>() where T : ElinNetBase
    {
        RemoveComponent();

        Lobby.Reset();

        Transport = EmpMod.Instance.gameObject.AddComponent<T>();

        // every mod is loaded by now
        OtherModsCompat.Apply();

        EmpLog.Debug("Initialized new connection component of {ConnectionType}",
            typeof(T).Name);

        return (Transport as T)!;
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