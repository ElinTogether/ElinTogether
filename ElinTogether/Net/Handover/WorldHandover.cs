using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Models;
using ElinTogether.Common;
using ElinTogether.LangMod;
using Steamworks;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     Council 9, step 4, prepared and not switched on: what a guest needs to know to take the world over when
///     its host is gone. Nothing in here is called by the game yet, and nothing in here starts anything: the
///     takeover itself is another piece of work, after trials on two PCs
/// </summary>
internal static class WorldHandover
{
    /// <summary>
    ///     The guest whose turn it is has that long to open the world, then it is the next one's turn
    /// </summary>
    internal const float TurnSeconds = 120f;

    // the world this game last played in as a guest, and who was in it, as of the host's last save
    private static WorldCopyManifest? _last;
    private static ulong _me;
    private static ulong[] _guests = [];

    // the host said it leaves and another guest takes the world over: the lobby left then, the one found since
    private static ulong _oldLobby;
    private static ulong _newLobby;

    /// <summary>
    ///     The host said it leaves and it is another guest's turn to open the world: this game joins that guest
    ///     once its game is open (NetReconnect), instead of going back to the title screen
    /// </summary>
    internal static bool Following { get; private set; }

    internal static void Followed()
    {
        Following = false;
    }

    /// <summary>
    ///     The host is gone without a word (a crash, its line cut) and it is another guest's turn to open the
    ///     world: its game is looked for from now on, as when the host said it left
    /// </summary>
    internal static void Follow()
    {
        if (!Following) {
            Following = true;
            _newLobby = 0;
        }
    }

    // a peer-hosted world kept with the world's store: the keeper's own order of who takes over, and the one it named
    private static bool _ordered;
    private static ulong _next;

    /// <summary>
    ///     The keeper of the world this game was last a guest of (its Steam id), 0 when none
    /// </summary>
    internal static ulong LastKeeper => _last is { Keeper: not 0 } last ? last.Keeper : _last?.Host ?? 0;

    /// <summary>
    ///     Another player keeps the world now (the store says so, PeerKeeper): its game is looked for and joined
    /// </summary>
    internal static void FollowKeeper(ulong keeper)
    {
        if (keeper == 0 || keeper == _me) {
            return;
        }

        if (Array.IndexOf(_guests, keeper) < 0) {
            _guests = [keeper, .._guests];
        }

        _next = keeper;
        _ordered = true;
        Follow();
    }

    /// <summary>
    ///     This game's place in the keeper's last succession list (0: first), the length of the list when it is not
    ///     in it: it waits for that many turns before it takes the world
    /// </summary>
    internal static int Position(ulong me)
    {
        var order = _guests.Where(g => g != 0 && g != LastKeeper).Distinct().ToArray();
        var at = Array.IndexOf(order, me);
        return at >= 0 ? at : order.Length;
    }

    /// <summary>
    ///     How many guests may take their turn at opening the world
    /// </summary>
    internal static int Turns => System.Math.Max(1, _guests.Count(g => g != 0));

    /// <summary>
    ///     That player may be the one who took over the world this game waits for
    /// </summary>
    internal static bool IsTaker(ulong user)
    {
        return Following && user != _me && Array.IndexOf(_guests, user) >= 0;
    }

    /// <summary>
    ///     The lobby opened by the guest that took the world over, 0 while there is none. A friend's is read at
    ///     once; anyone else's comes from the list of lobbies, asked here and read at the next call <br />
    ///     Steam only: not to call for a game joined by a port or an address
    /// </summary>
    // ponytail: any former guest that hosts now counts as the taker, the lobby does not say which world it is.
    // Write the world and its handover number in the lobby when the crash case (R5) needs to tell them apart
    internal static ulong NewHostLobby()
    {
        if (!Following) {
            return 0;
        }

        foreach (var guest in _guests) {
            if (guest != _me && SteamFriends.GetFriendGamePlayed(new(guest), out var played) &&
                played.m_steamIDLobby.IsValid() && played.m_steamIDLobby.m_SteamID != _oldLobby) {
                return played.m_steamIDLobby.m_SteamID;
            }
        }

        var found = _newLobby;
        NetSession.Instance.Lobby.GetOnlineLobbies(lobbies => {
            foreach (var lobby in lobbies) {
                if (IsTaker(lobby.GameServer.id.m_SteamID)) {
                    _newLobby = lobby;
                }
            }
        });
        return found;
    }

    /// <summary>
    ///     A whole copy of a world kept on this disk
    /// </summary>
    internal readonly struct Copy
    {
        internal required string Dir { get; init; }
        internal required string World { get; init; }
        internal required ulong Host { get; init; }
        internal required int Handover { get; init; }
        internal required DateTime SavedUtc { get; init; }
        internal required int Files { get; init; }
        internal required long Bytes { get; init; }

        /// <summary>
        ///     The highest handover number wins, then the latest save: two games that both went on with the same
        ///     world are told apart by who took it over last
        /// </summary>
        internal bool IsNewerThan(int handover, DateTime savedUtc)
        {
            return Handover != handover ? Handover > handover : SavedUtc > savedUtc;
        }

        public override string ToString()
        {
            return $"{World} of {Host}, handover {Handover}, saved {SavedUtc:u}, {Files} files, {Bytes} bytes";
        }
    }

    /// <summary>
    ///     Every save the host offers: which world this is and who plays in it
    /// </summary>
    internal static void Remember(WorldCopyManifest offer)
    {
        var session = NetSession.Instance;
        _last = offer;
        _me = session.Self is { } self ? (ulong)self.User : 0UL;
        // a peer-hosted world kept with the store: the keeper's order (its own seat is not in it)
        _ordered = offer.Succession is { Length: > 0 };
        _next = 0;
        if (_ordered) {
            _guests = offer.Succession;
            return;
        }

        // the host is the player 0 of its own list
        _guests = session.CurrentPlayers.Where(p => p is not null && p.Index != 0).Select(p => (ulong)p.User).ToArray();
    }

    /// <summary>
    ///     The host said it leaves, and who was playing then
    /// </summary>
    internal static void HostLeft(ulong[]? guests, ulong named = 0)
    {
        if (guests is { Length: > 0 }) {
            _guests = guests;
        }

        // the keeper named the next one (PeerKeeper): that one, then the keeper's order
        _next = named;
        _ordered = named != 0 || (Helper.PeerKeeper.Enabled && _ordered);
        if (named != 0 && Array.IndexOf(_guests, named) < 0) {
            _guests = [named, .._guests];
        }

        if (_me == 0 && NetSession.Instance.Self is { } self) {
            _me = (ulong)self.User;
        }

        var next = Successor(_guests, 0f);
        Following = next != 0 && next != _me;
        _oldLobby = NetSession.Instance.Lobby.Current;
        _newLobby = 0;
    }

    // ---- the former host comes back (council 9, slice R6) ----

    private const float WatchSeconds = 20f;
    private static float _nextWatch;

    /// <summary>
    ///     Which world the game loaded here is, the same in every copy of it
    /// </summary>
    internal static string WorldKey => EClass.game is { } game ? game.seed.ToString() : "";

    // games of this machine that host on a local port (the bench, a second window): world|handover|port|process
    // (not in the folder of the copies: on the bench each window has its own, and they must all read this one)
    private static string LocalFile => Path.Combine(Application.persistentDataPath, "ElinMP", "local-host.txt");

    /// <summary>
    ///     This game hosts on a local port: said for the other games of this machine
    /// </summary>
    internal static void AnnounceLocal(int port)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalFile)!);
            var process = System.Diagnostics.Process.GetCurrentProcess().Id;
            File.WriteAllText(LocalFile, string.Join("|", WorldKey, ElinNetHost.HandoverNumber, port, process));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // noexcept
        }
    }

    /// <summary>
    ///     The local port of another game of this machine that hosts this same world, taken over since: 0 for none
    /// </summary>
    internal static int NewerLocalHost()
    {
        try {
            if (!File.Exists(LocalFile)) {
                return 0;
            }

            var parts = File.ReadAllText(LocalFile).Split((char)124);
            if (parts.Length != 4 || parts[0] != WorldKey || !int.TryParse(parts[1], out var handover) ||
                !int.TryParse(parts[2], out var port) || !int.TryParse(parts[3], out var process) ||
                handover <= ElinNetHost.HandoverNumber || process == System.Diagnostics.Process.GetCurrentProcess().Id) {
                return 0;
            }

            // (a game that crashed leaves its line behind)
            System.Diagnostics.Process.GetProcessById(process);
            return port;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) {
            return 0;
        }
    }

    /// <summary>
    ///     Every frame. This game hosts a world alone, and another player took that world over since (it left by a
    ///     crash, its line was cut, or it loaded its own save again): the game that goes on is the other one. It is
    ///     joined as a guest, without a question; the save played here is left as it is, nothing is deleted
    /// </summary>
    internal static void Watch()
    {
        if (Time.unscaledTime < _nextWatch) {
            return;
        }

        _nextWatch = Time.unscaledTime + WatchSeconds;

        var session = NetSession.Instance;
        // (a server never joins anyone: a peer-hosted world's keeper hands it over itself, PeerKeeper)
        if (!EmpConfig.Server.Takeover.Value || EmpServer.Requested || !EClass.core.IsGameStarted ||
            session.Transport is not ElinNetHost { IsZoneSession: false } host || session.CurrentPlayers.Count > 1 ||
            WorldTakeover.Busy) {
            return;
        }

        if (TryJoinNewerLocal()) {
            return;
        }

        // by Steam: the games that say they host this world, kept by a player of this world, taken over since
        if (host.IsLocalServer || session.Lobby.IsListing) {
            return;
        }

        var world = WorldKey;
        var mine = ElinNetHost.HandoverNumber;
        try {
            session.Lobby.GetOnlineLobbies(lobbies => {
                foreach (var lobby in lobbies) {
                    if (lobby == session.Lobby.Current || lobby[EmpLobbyData.World] != world ||
                        !int.TryParse(lobby[EmpLobbyData.Handover], out var handover) || handover <= mine ||
                        !ElinNetHost.IsPlayerOfWorld(lobby.GameServer.id.m_SteamID)) {
                        continue;
                    }

                    var found = lobby;
                    Rejoin(() => session.Lobby.ConnectLobby(found));
                    return;
                }
            }, world);
        } catch (Exception ex) {
            EmpLog.Debug(ex, "The games that host this world could not be listed");
            // noexcept
        }
    }

    /// <summary>
    ///     Another game of this machine hosts this world, taken over since: joined instead of hosting (also before
    ///     a session opens by itself at load: its port is the one this game would ask for)
    /// </summary>
    internal static bool TryJoinNewerLocal()
    {
        if (!EmpConfig.Server.Takeover.Value || NewerLocalHost() is not (var port and > 0)) {
            return false;
        }

        var session = NetSession.Instance;
        Rejoin(() => session.InitializeComponent<ElinNetClient>().ConnectLocalPort((ushort)port));
        return true;
    }

    private static void Rejoin(Action join)
    {
        var session = NetSession.Instance;
        // (asked a moment ago: a player may have come in meanwhile, this game is then the one that goes on)
        if (session.Transport is ElinNetClient || session.CurrentPlayers.Count > 1) {
            return;
        }

        EmpLog.Information("Another player took this world over since: joining its game instead of hosting a second one");
        EmpPop.Information("emp_takeover_rejoin".lang());

        session.ResetSession();
        if (EClass.core.IsGameStarted) {
            EClass.scene.Init(Scene.Mode.Title);
        }

        try {
            join();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not join the game that took this world over");
        }
    }

    internal static string Describe()
    {
        return $"mine: {Mine()?.ToString() ?? "none"}; me {_me}; guests {string.Join(",", _guests)}; first {Successor(_guests, 0f)}";
    }

    /// <summary>
    ///     "I have a whole copy of the world I was playing in, of that handover number, saved at that time",
    ///     null when there is none. Whole means every file was checked when the copy was closed; see
    ///     <see cref="Verify" /> to read them again
    /// </summary>
    internal static Copy? Mine()
    {
        return _last is null ? null : Find(_last.Host, _last.World);
    }

    internal static Copy? Find(ulong host, string world)
    {
        var folder = WorldCopyStore.Folder(new() { Host = host, World = world });
        foreach (var (dir, manifest) in WorldCopyStore.Copies(folder)) {
            // a copy that lacks files of the world is not one to open it from
            if (manifest.Incomplete) {
                continue;
            }

            return new Copy {
                Dir = dir,
                World = manifest.World,
                Host = manifest.Host,
                Handover = manifest.Handover,
                SavedUtc = new(manifest.Saved, DateTimeKind.Utc),
                Files = manifest.Files.Length,
                Bytes = manifest.Files.Sum(f => f.Size),
            };
        }

        return null;
    }

    /// <summary>
    ///     Reads every file of the copy again: before a world is opened from it. Not on the game's thread
    /// </summary>
    internal static bool Verify(Copy copy)
    {
        var (dir, manifest) = Read(copy);
        return manifest is not null && WorldCopyStore.Verify(dir, manifest);
    }

    /// <summary>
    ///     The copy as it is on the disk now, a null manifest when it is gone
    /// </summary>
    internal static (string Dir, WorldCopyManifest? Manifest) Read(Copy copy)
    {
        // (the folder of a copy is written with both kinds of slashes, and GetDirectoryName changes them)
        var wanted = Path.GetFullPath(copy.Dir);
        return WorldCopyStore.Copies(Path.GetDirectoryName(wanted)!)
            .FirstOrDefault(c => string.Equals(Path.GetFullPath(c.Dir), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Who takes the world over: the smallest Steam id among the guests, and the next one for every
    ///     <see cref="TurnSeconds" /> gone by without the world being open again. Every guest finds the same
    ///     answer from the same list without asking anyone. 0 when there is nobody
    /// </summary>
    internal static ulong Successor(IEnumerable<ulong> guests, float secondsWithoutHost)
    {
        // (the keeper's own order when it gave one, the one it named first)
        var listed = guests.Where(g => g != 0).Distinct();
        var order = _ordered ? (_next != 0 ? listed.Where(g => g == _next).Concat(listed.Where(g => g != _next)) : listed).ToArray()
            : listed.OrderBy(g => g).ToArray();
        if (order.Length == 0) {
            return 0;
        }

        var turn = (int)(Math.Max(0f, secondsWithoutHost) / TurnSeconds);
        return order[Math.Min(turn, order.Length - 1)];
    }

    /// <summary>
    ///     This game is the one to take over the world it was last a guest of, that long after its host was lost
    /// </summary>
    internal static bool IsMyTurn(float secondsWithoutHost)
    {
        return _me != 0 && Successor(_guests, secondsWithoutHost) == _me && Mine() is not null;
    }

#if DEBUG
    internal static string State => Describe();
#endif
}
