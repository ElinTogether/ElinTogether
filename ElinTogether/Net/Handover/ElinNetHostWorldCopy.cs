using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using Steamworks;
using STask = System.Threading.Tasks.Task;

namespace ElinTogether.Net;

/// <summary>
///     Council 9, step 3 (A'): after each save the host makes by itself, every guest gets what changed in the
///     host's save folder and keeps a whole copy of the world on its own disk, so that a crash of the host no
///     longer costs the game <br />
///     The save is read off the game's thread, into memory: what is sent is one save, whatever is saved
///     meanwhile. It goes out a small part at a time between the game's own messages, never ahead of them
/// </summary>
internal partial class ElinNetHost
{
    // a part and what it may wait behind (48 KB) hold the game's messages back for about a third of a second
    // on a 1 Mbit/s upload: 128 KB/s at most in all, whatever the number of guests (one is served at a time), a
    // world of 5 MB in 40 s, a usual save (game.txt and the maps that changed, about 1 MB) in 8 s
    private const int WorldCopyPieceSize = 32 * 1024;
    private const float WorldCopyPiecesPerSecond = 4f;

    // no part is handed to Steam while more than this still waits to leave: a slow link slows the copy, not the game
    private const int WorldCopyQueueLimit = 16 * 1024;

    private readonly Dictionary<int, WorldCopyLink> _worldCopyLinks = [];
    private WorldSnapshot? _worldSnapshot;
    private WorldSnapshot? _worldSnapshotRead;
    private int _worldSnapshotReading;
    private bool _worldCopyStarted;
    private int _worldCopyOmittedTold;
    private int _worldCopyTooBigTold;

    // peer id -> the save (its Saved) that guest was sent whole: a link made again for the same player does not
    // offer that save a second time
    private readonly Dictionary<int, long> _worldCopyGiven = [];

    /// <summary>
    ///     How many times this world was taken over by another player after its host was lost, kept in the save.
    ///     Two games that went on with the same world are told apart by it: the highest wins. Raised when a guest
    ///     opens the world from its copy (WorldTakeover)
    /// </summary>
    [ElinGameIOProperty("world_handover")]
    private static int[] WorldHandoverCount
    {
        get => field is { Length: 1 } ? field : field = new int[1];
        set;
    }

    internal static int HandoverNumber => WorldHandoverCount[0];

    /// <summary>
    ///     A peer-hosted world kept with the world's store: the handover number is the one the store gave with the
    ///     lease (PeerKeeper.ServerOpening), never lower than the save's
    /// </summary>
    internal static void SetHandoverNumber(int handover)
    {
        WorldHandoverCount[0] = System.Math.Max(HandoverNumber, handover);
    }

    /// <summary>
    ///     That player's game was given the latest save whole (PeerKeeper's succession list puts it first)
    /// </summary>
    internal bool HasLatestCopy(ulong user)
    {
        return _worldSnapshot is { } latest && Socket.Peers.Any(p => (ulong)p.User == user &&
            _worldCopyGiven.TryGetValue(p.Id, out var had) && had == latest.Manifest.Saved);
    }

    /// <summary>
    ///     The world was just saved by itself, see EmpAutoHost
    /// </summary>
    internal void WorldSaved()
    {
        if (IsZoneSession || !Session.Rules.KeepWorldCopy || Socket.Peers.Count == 0) {
            return;
        }

        if (!_worldCopyStarted) {
            _worldCopyStarted = true;
            // here and not in RegisterPackets: no guest asks for anything before a save was offered
            Router.RegisterHandler<WorldCopyWant>(OnWorldCopyWant);
            Scheduler.Subscribe(PumpWorldCopy, WorldCopyPiecesPerSecond);
        }

        // (a read still under way: this save is left out, the next one is taken)
        if (Interlocked.Exchange(ref _worldSnapshotReading, 1) != 0) {
            return;
        }

        var dir = GameIO.pathCurrentSave;
        var last = _worldSnapshot;
        var manifest = new WorldCopyManifest {
            World = Game.id,
            Host = LocalUser,
            Handover = HandoverNumber,
            Saved = DateTime.UtcNow.Ticks,
            Succession = Helper.PeerKeeper.Succession,
            Keeper = Helper.PeerKeeper.Enabled && PeerHostedServer ? Helper.PeerKeeper.PlayerId : 0,
        };

        STask.Run(() => {
            try {
                // (FastJoinsAndSaves: the files of this save may still be on their way to the disk)
                Helper.SaveWriter.Wait();
                var watch = Stopwatch.StartNew();
                if (WorldSnapshot.Read(dir, manifest, last) is not { } snapshot) {
                    EmpLog.Information("World copy: the save changed while it was read, the next one is taken");
                    return;
                }

                EmpLog.Information("World copy: save of {Files} files, {Bytes} bytes read in {Ms} ms",
                    snapshot.Data.Length, snapshot.Data.Sum(d => (long)d.Length), watch.ElapsedMilliseconds);

                // told when the number changes, not at every save
                if (Interlocked.Exchange(ref _worldCopyOmittedTold, snapshot.Omitted) != snapshot.Omitted && snapshot.Omitted > 0) {
                    EmpLog.Warning("World copy: {Omitted} file(s) of the save have a name a guest's disk would refuse and are left out: the copy is marked incomplete and will not be used to take the world over",
                        snapshot.Omitted);
                }

                Volatile.Write(ref _worldSnapshotRead, snapshot);
            } catch (WorldSnapshot.TooBigException big) {
                // the same save would come again at each autosave: told once
                if (Interlocked.Exchange(ref _worldCopyTooBigTold, 1) == 0) {
                    EmpLog.Warning("World copy: the save is too big for a guest to keep ({Files} files, {Bytes} bytes; at most {MaxFiles} files, {MaxBytes} bytes), no copy is made of it",
                        big.Files, big.Bytes, WorldCopyStore.MaxFiles, WorldCopyStore.MaxBytes);
                }
            } catch (Exception ex) {
                EmpLog.Warning("World copy: the save could not be read: {Why}", ex.Message);
            } finally {
                Volatile.Write(ref _worldSnapshotReading, 0);
            }
        });
    }

    /// <summary>
    ///     This game stops hosting (session closed, title screen, Elin closed): said to every guest before the
    ///     links close, so that the one whose turn it is opens the world from its copy at once instead of
    ///     finding out by a dead link (WorldTakeover). Said once
    /// </summary>
    internal void AnnounceLeaving()
    {
        // told with the takeover off too: a guest that only saw its link drop took it for a lost link and tried to
        // join again for 3 minutes, on "Going back into the game" (two real PCs, 2026-10-27)
        if (_leavingTold || IsZoneSession) {
            return;
        }

        _leavingTold = true;
        var leaving = new HostLeaving {
            Guests = Session.CurrentPlayers.Where(p => p is not null && p.Index != 0).Select(p => (ulong)p.User).ToArray(),
        };
        // a peer-hosted world kept with the world's store: the keeper named the next one, and the order after it
        if (Helper.PeerKeeper.Enabled && PeerHostedServer) {
            leaving.Guests = Helper.PeerKeeper.Succession;
            leaving.Next = Helper.PeerKeeper.Next;
        }

        var told = 0;
        foreach (var peer in Socket.Peers) {
            if (!peer.IsConnected || !_handshakes.TryGetValue(peer.Id, out var shake) || shake.Phase != NetHandshakePhase.Joined) {
                continue;
            }

            // on the wire now: the listen socket is discarded with the component, and its links with it
            if (peer.Send(leaving) && peer is SteamNetPeer steam) {
                SteamNetworkingSockets.FlushMessagesOnConnection(steam.Connection);
                told++;
            }
        }

        EmpLog.Information("Leaving: {Told} guest(s) told, {Guests} playing", told, leaving.Guests.Length);
    }

    private bool _leavingTold;

#if DEBUG
    /// <summary>
    ///     Bench: every link is closed the way Steam closes one it gave up on, with Steam's own word and without
    ///     this game being told (it finds out as it would in a real game: the peer is no longer connected)
    /// </summary>
    internal int DropLinksAsSteam()
    {
        var dropped = 0;
        foreach (var peer in Socket.Peers) {
            if (peer is SteamNetPeer steam) {
                SteamNetworkingSockets.CloseConnection(steam.Connection, 0, "Connection dropped", false);
                dropped++;
            }
        }

        return dropped;
    }
#endif

    // the guests are given that long to get the last save, then the host leaves whatever they have
    private const float LastCopySeconds = 15f;
    private static bool _leaving;

    /// <summary>
    ///     The player who hosts asked to leave and guests would take the world over: they get a last save first
    /// </summary>
    internal static bool OwesGuestsLastCopy =>
        !_leaving && NetSession.Instance.Transport is ElinNetHost host && host.OwesLastCopy();

    private bool OwesLastCopy()
    {
        return !IsZoneSession && !_leavingTold && Session.Rules.AllowTakeover && Session.Rules.KeepWorldCopy &&
               core.IsGameStarted && Socket.Peers.Any(Playing);
    }

    // in the game: its first state came in, or it is on a map of its own
    private bool Playing(ISteamNetPeer peer)
    {
        return peer.IsConnected && _handshakes.TryGetValue(peer.Id, out var shake) &&
               shake.Phase == NetHandshakePhase.Joined &&
               (IsAway(peer) || (States.TryGetValue(peer.Id, out var state) && state.LastReceivedTick != -1));
    }

    /// <summary>
    ///     The host leaves by its own choice (the mod's Disconnect, the game's "return to title" and "quit"): the
    ///     world as it is now goes to the guests before the links close, so that the one who takes it over starts
    ///     from this very moment and not from the last save made by itself. Then <paramref name="then" />, always
    /// </summary>
    /// <param name="save">False when the game was just saved by what calls this</param>
    /// <param name="beforeLeaving">
    ///     Run once the guests have the last save, before they are told the host leaves (PeerKeeper: the last version
    ///     and the lease handed to the next keeper); not run when no guest is owed a last save
    /// </param>
    internal static void LeaveWithLastCopy(bool save, Action then, Func<System.Collections.IEnumerator>? beforeLeaving = null)
    {
        // (asked twice: the first one is under way)
        if (_leaving) {
            return;
        }

        if (NetSession.Instance.Transport is not ElinNetHost host || !host.OwesLastCopy()) {
            then();
            return;
        }

        _leaving = true;
        try {
            EmpMod.Instance.StartCoroutine(host.GiveLastCopy(save, then, beforeLeaving));
        } catch (Exception ex) {
            EmpLog.Error(ex, "Leaving: the last save could not be started, leaving without it");
            _leaving = false;
            then();
        }
    }

    /// <summary>
    ///     Whatever throws on the way (robustness audit 2026-11-01), the host leaves: _leaving is reset and then() is
    ///     called, also when the coroutine ends early. A step that throws ends the wait, nothing more
    /// </summary>
    private System.Collections.IEnumerator GiveLastCopy(bool save, Action then, Func<System.Collections.IEnumerator>? beforeLeaving)
    {
        try {
            var steps = GiveLastCopySteps(save, beforeLeaving);
            while (true) {
                try {
                    if (!steps.MoveNext()) {
                        break;
                    }
                } catch (Exception ex) {
                    EmpLog.Error(ex, "Leaving: the last save failed on the way, leaving anyway");
                    break;
                }

                yield return steps.Current;
            }

            try {
                if (this != null) {
                    AnnounceLeaving();
                }
            } catch (Exception ex) {
                EmpLog.Error(ex, "Leaving: the guests could not be told");
            }
        } finally {
            _leaving = false;
            then();
        }
    }

    private System.Collections.IEnumerator GiveLastCopySteps(bool save, Func<System.Collections.IEnumerator>? beforeLeaving)
    {
        EmpPop.Information("emp_takeover_leaving".lang());

        var watch = Stopwatch.StartNew();
        var since = DateTime.UtcNow.Ticks;
        var given = false;
        var saved = !save;
        if (save) {
            try {
                saved = EmpAutoHost.SafeToSave() && game.Save(true, true);
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Leaving: the world could not be saved, the guests keep the copy they have");
            }
        }

        if (saved) {
            // a read of an earlier save still under way would leave this one out
            while (this != null && Volatile.Read(ref _worldSnapshotReading) != 0 && watch.Elapsed.TotalSeconds < LastCopySeconds) {
                yield return null;
            }

            if (this != null) {
                Safely(WorldSaved);
            }

            // every frame instead of a few times a second: nothing else of the host is worth its upload now
            while (this != null && watch.Elapsed.TotalSeconds < LastCopySeconds && !(given = LastCopyGiven(since))) {
                Safely(PumpWorldCopy);
                yield return null;
            }

            // what Steam took is on the wire and came in
            var flushed = watch.Elapsed.TotalSeconds + 3;
            while (this != null && given && watch.Elapsed.TotalSeconds < flushed && Socket.Peers.Any(p => Unacked(p) > 0)) {
                yield return null;
            }
        }

        EmpLog.Information("Leaving: last save {Result} after {Seconds:F1}s",
            given ? "given to every guest" : saved ? "not given in time" : "not made", watch.Elapsed.TotalSeconds);

        if (beforeLeaving is not null) {
            var before = beforeLeaving();
            while (true) {
                try {
                    if (!before.MoveNext()) {
                        break;
                    }
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Leaving: what comes before the word to the guests failed");
                    break;
                }

                yield return before.Current;
            }
        }
    }

    // whatever goes wrong, the host leaves
    private static void Safely(Action step)
    {
        try {
            step();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Leaving: the last save is not on its way");
        }
    }

    private bool LastCopyGiven(long since)
    {
        return _worldSnapshot is { } latest && latest.Manifest.Saved >= since &&
               Socket.Peers.Where(Playing).All(p =>
                   _worldCopyGiven.TryGetValue(p.Id, out var had) && had == latest.Manifest.Saved &&
                   !(_worldCopyLinks.TryGetValue(p.Id, out var link) && link.Sending is not null));
    }

    /// <summary>
    ///     Reliable bytes this guest has not acknowledged yet
    /// </summary>
    private static int Unacked(ISteamNetPeer peer)
    {
        if (peer is not SteamNetPeer steam || NetShutdown.IsQuitting) {
            return 0;
        }

        var status = new SteamNetConnectionRealTimeStatus_t();
        var lane = new SteamNetConnectionRealTimeLaneStatus_t();
        return SteamNetworkingSockets.GetConnectionRealTimeStatus(steam.Connection, ref status, 0, ref lane) == EResult.k_EResultOK
            ? status.m_cbPendingReliable + status.m_cbSentUnackedReliable
            : 0;
    }

    /// <summary>
    ///     A few times a second: the latest save is offered to who does not have it, one part goes to one guest
    ///     that asked for files (the first in line, the others wait their turn)
    /// </summary>
    private void PumpWorldCopy()
    {
        if (Interlocked.Exchange(ref _worldSnapshotRead, null) is { } read) {
            _worldSnapshot = read;
            // a link gone holds a save in memory for nothing
            foreach (var gone in _worldCopyLinks.Where(l => !l.Value.Peer.IsConnected).Select(l => l.Key).ToArray()) {
                _worldCopyLinks.Remove(gone);
            }
        }

        if (_worldSnapshot is not { } latest || !Session.Rules.KeepWorldCopy) {
            return;
        }

        // one guest is served at a time (its turn lasts until it has what it asked for): the upload of the host
        // is shared by all guests, not multiplied by their number. The others wait with what they asked for
        var serving = false;

        foreach (var peer in Socket.Peers) {
            // in the game: its first state came in, or it is on a map of its own
            if (!peer.IsConnected || !_handshakes.TryGetValue(peer.Id, out var shake) ||
                shake.Phase != NetHandshakePhase.Joined ||
                !(IsAway(peer) || (States.TryGetValue(peer.Id, out var state) && state.LastReceivedTick != -1))) {
                continue;
            }

            // a player who comes back has a new link and the same id: it is offered the save again, and asks
            // for what it lacks only
            if (!_worldCopyLinks.TryGetValue(peer.Id, out var link) || !ReferenceEquals(link.Peer, peer)) {
                link = _worldCopyLinks[peer.Id] = new(peer);
            }

            if (link.Sending is not null) {
                // (a guest whose link is full does not hold the turn: nothing of the host's upload goes to it)
                serving = serving || SendWorldCopyPiece(link);
            } else if (!ReferenceEquals(link.Offered, latest)) {
                // never while an older save is on its way: a copy that takes longer than the time between two
                // saves would never be whole
                link.Offered = latest;

                // it was sent that very save whole by an earlier link: no list of files again. (It does not hear
                // of this save, so WorldHandover.Remember waits for the next one)
                if (_worldCopyGiven.TryGetValue(peer.Id, out var given) && given == latest.Manifest.Saved) {
                    continue;
                }

                peer.Send(latest.Manifest);
            }
        }
    }

    /// <summary>
    ///     Net event: the files of the save offered that this guest lacks
    /// </summary>
    private void OnWorldCopyWant(WorldCopyWant want, ISteamNetPeer peer)
    {
        if (!_worldCopyLinks.TryGetValue(peer.Id, out var link) || !ReferenceEquals(link.Peer, peer) ||
            link.Sending is not null || link.Offered is not { } offered || offered.Manifest.Saved != want.Saved ||
            want.Files is not { Length: > 0 } files || files.Any(i => i < 0 || i >= offered.Data.Length)) {
            return;
        }

        EmpLog.Debug("World copy: {Count} files, {Bytes} bytes for {@Peer}",
            files.Length, files.Sum(i => (long)offered.Data[i].Length), peer);

        link.Sending = offered;
        link.Wanted = files;
        link.Next = 0;
        link.Offset = 0;
    }

    /// <returns>true when a part went out (this tick's share of the upload is used)</returns>
    private bool SendWorldCopyPiece(WorldCopyLink link)
    {
        var snapshot = link.Sending!;

        while (link.Next < link.Wanted.Length && link.Offset >= snapshot.Data[link.Wanted[link.Next]].Length) {
            link.Next++;
            link.Offset = 0;
        }

        if (link.Next >= link.Wanted.Length) {
            link.Sending = null;
            _worldCopyGiven[link.Peer.Id] = snapshot.Manifest.Saved;
            return false;
        }

        if (PendingReliable(link.Peer) > WorldCopyQueueLimit) {
            return false;
        }

        var file = link.Wanted[link.Next];
        var bytes = snapshot.Data[file];
        var size = Math.Min(
#if DEBUG
            WorldCopyBench.PieceSize > 0 ? WorldCopyBench.PieceSize :
#endif
            WorldCopyPieceSize, bytes.Length - link.Offset);
        var data = new byte[size];
        Buffer.BlockCopy(bytes, link.Offset, data, 0, size);

        var sent = link.Peer.Send(new WorldCopyPiece {
            Saved = snapshot.Manifest.Saved,
            File = file,
            Offset = link.Offset,
            Data = data,
        });

        if (sent) {
            link.Offset += size;
        } else {
            // the guest keeps the files that are whole and asks for the rest at the next save
            link.Sending = null;
        }

        return sent;
    }

    /// <summary>
    ///     Reliable bytes Steam has not put on the wire yet for this guest
    /// </summary>
    private static int PendingReliable(ISteamNetPeer peer)
    {
        if (peer is not SteamNetPeer steam || NetShutdown.IsQuitting) {
            return 0;
        }

        var status = new SteamNetConnectionRealTimeStatus_t();
        var lane = new SteamNetConnectionRealTimeLaneStatus_t();
        return SteamNetworkingSockets.GetConnectionRealTimeStatus(steam.Connection, ref status, 0, ref lane) == EResult.k_EResultOK
            ? status.m_cbPendingReliable
            : 0;
    }

    /// <summary>
    ///     What one guest was offered and what it is being sent
    /// </summary>
    private sealed class WorldCopyLink(ISteamNetPeer peer)
    {
        public readonly ISteamNetPeer Peer = peer;
        public WorldSnapshot? Offered;
        public WorldSnapshot? Sending;
        public int[] Wanted = [];
        public int Next;
        public int Offset;
    }

    /// <summary>
    ///     One save, whole, in memory
    /// </summary>
    private sealed class WorldSnapshot
    {
        public required WorldCopyManifest Manifest { get; init; }
        public required byte[][] Data { get; init; }
        public int Omitted { get; init; }
        private (long Size, DateTime Written)[] _stamps = [];

        /// <summary>
        ///     Not on the game's thread. Null when the folder changed meanwhile: what was read may be of two saves
        /// </summary>
        public static WorldSnapshot? Read(string dir, WorldCopyManifest manifest, WorldSnapshot? last)
        {
            var listed = List(dir, out var omitted);

            // before anything is read: a guest refuses a world past these limits (WorldCopyStore.IsSafe)
            var total = listed.Sum(f => f.Info.Length);
            if (listed.Count > WorldCopyStore.MaxFiles || total > WorldCopyStore.MaxBytes) {
                throw new TooBigException(listed.Count, total);
            }

            var known = new Dictionary<string, int>();
            for (var i = 0; last is not null && i < last.Data.Length; i++) {
                known[last.Manifest.Files[i].Path] = i;
            }

            var files = new WorldCopyFile[listed.Count];
            var data = new byte[listed.Count][];
            var stamps = new (long, DateTime)[listed.Count];
            for (var i = 0; i < listed.Count; i++) {
                var (path, info) = listed[i];
                stamps[i] = (info.Length, info.LastWriteTimeUtc);

                // a file the last save left alone is neither read nor kept twice
                if (known.TryGetValue(path, out var same) && last!._stamps[same] == stamps[i]) {
                    data[i] = last.Data[same];
                    files[i] = last.Manifest.Files[same];
                    continue;
                }

                data[i] = File.ReadAllBytes(info.FullName);
                files[i] = new() {
                    Path = path,
                    Size = data[i].Length,
                    Hash = WorldCopyStore.Hash(data[i]),
                };
            }

            var after = List(dir, out _);
            if (after.Count != listed.Count ||
                after.Where((f, i) => f.Path != listed[i].Path || (f.Info.Length, f.Info.LastWriteTimeUtc) != stamps[i]).Any() ||
                files.Where((f, i) => f.Size != stamps[i].Item1).Any()) {
                return null;
            }

            manifest.Files = files;
            manifest.Incomplete = omitted > 0;
            return new() {
                Manifest = manifest,
                Data = data,
                Omitted = omitted,
                _stamps = stamps,
            };
        }

        /// <summary>
        ///     The save is past what a guest keeps: nothing was read
        /// </summary>
        public sealed class TooBigException(int files, long bytes) : Exception
        {
            public int Files { get; } = files;
            public long Bytes { get; } = bytes;
        }

        /// <summary>
        ///     The files of a save, as the game's own backup takes them: not the maps of the visit under way
        ///     (Temp), not the archive made of the folder for the Steam cloud
        /// </summary>
        /// <param name="omitted">Files a guest's disk would refuse, left out of the list: the copy lacks them</param>
        private static List<(string Path, FileInfo Info)> List(string dir, out int omitted)
        {
            var root = new DirectoryInfo(dir).FullName.TrimEnd('/', '\\');
            var all = new DirectoryInfo(root).GetFiles("*", SearchOption.AllDirectories)
                .Select(f => (Path: f.FullName.Substring(root.Length + 1).Replace('\\', '/'), Info: f))
                .Where(f => !f.Path.StartsWith("Temp/", StringComparison.OrdinalIgnoreCase) &&
                            !f.Path.Equals("cloud.zip", StringComparison.OrdinalIgnoreCase))
                .ToList();
            omitted = all.Count(f => !WorldCopyStore.IsSafePath(f.Path));
            return all
                .Where(f => WorldCopyStore.IsSafePath(f.Path))
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
        }
    }
}

#if DEBUG
/// <summary>
///     Bench (dev/_tools/worldcopy_suite.py), through the debug listener's eval
/// </summary>
public static class WorldCopyBench
{
    /// <summary>
    ///     Host: bytes per part instead of the real size, 0 for the real one. A small world is then long enough
    ///     to send for the link to be cut in the middle
    /// </summary>
    public static int PieceSize;

    /// <summary>
    ///     Guest: where its copies are, and what it is doing
    /// </summary>
    public static string Root => WorldCopyStore.Root;

    public static string State => WorldCopyReceiver.State + "; " + WorldHandover.State;

    /// <summary>
    ///     Guest that takes the world over, then host: what the takeover did, and the handover number of the world
    /// </summary>
    public static string Takeover => WorldTakeover.State;

    public static int Handover => ElinNetHost.HandoverNumber;

    /// <summary>
    ///     Who plays whom in the world loaded: the local character and its owner, then player -> character
    /// </summary>
    public static string Who => ElinNetHost.WhoPlaysWhom;
}
#endif
