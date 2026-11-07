using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Models;
using STask = System.Threading.Tasks.Task;

namespace ElinTogether.Net;

/// <summary>
///     The guest's side of the world copy: after each of its saves the host says what its save folder holds, the
///     guest asks for the files that changed and writes them, as they come, in the incoming folder of that world.
///     Lives outside the connection component: what was received survives a lost link (the host is asked for
///     the rest only), and a copy whose last file came in is finished even if the host is gone by then
/// </summary>
internal static class WorldCopyReceiver
{
    // the save being received, the files of it still to come, and the one being written
    private static WorldCopyManifest? _incoming;
    private static HashSet<int> _missing = [];
    private static FileStream? _stream;
    private static int _file = -1;

    // paths of the incoming folder that are whole -> their hash, as the host gave it
    private static readonly Dictionary<string, string> _received = [];
    private static string _folder = "";
    private static string? _previous;
    private static bool _badPrevious;

    // a copy is being closed off the game's thread, and the save offered meanwhile
    private static System.Threading.Tasks.Task<WorldCopyStore.Closed>? _closing;
    private static WorldCopyManifest? _waiting;

    /// <summary>
    ///     Every file of a save came in and they are being checked: a whole copy more in a moment
    /// </summary>
    internal static bool IsClosing => _closing is not null;

    internal static void Offer(WorldCopyManifest offer)
    {
        if (!WorldCopyStore.IsSafe(offer)) {
            EmpLog.Warning("World copy refused: {Files} files that cannot be kept", offer.Files?.Length ?? 0);
            return;
        }

        // the folder is named after the host: a host that claims another id would overwrite, or push out, the copies
        // this guest keeps of that other host's world. The id is the one of the peer we are connected to, or a server's
        // reserved one (ServerAvatar.HostUser: a hidden server plays on its keeper's account, and every keeper of a
        // peer-hosted world names its copies by it). No Steam account has that id: claiming it overwrites no player's
        if (NetSession.Instance.Transport is not ElinNetClient { Host: { } peer } ||
            (offer.Host != (ulong)peer.User && offer.Host != Steam.SteamNetPeer.DevIdentityBase + Helper.ServerAvatar.ServerIdentity &&
             // (a link by the local port, the same PC: it carries no Steam id, every copy was refused there)
             (ulong)peer.User != 0)) {
            EmpLog.Warning("World copy refused: it claims host {Claimed}, the connected host is another one", offer.Host);
            return;
        }

        WorldHandover.Remember(offer);

        if (_closing is not null) {
            _waiting = offer;
            return;
        }

        Drop();

        var folder = WorldCopyStore.Folder(offer);
        if (folder != _folder) {
            _folder = folder;
            _received.Clear();
            _badPrevious = false;
        }

        var copies = _badPrevious ? [] : WorldCopyStore.Copies(folder);
        var (previous, kept) = copies.FirstOrDefault();
        if (kept is not null && kept.Saved == offer.Saved && kept.Handover == offer.Handover) {
            return;
        }

        var unchanged = kept?.Files.ToDictionary(f => f.Path, f => (f.Size, f.Hash)) ?? [];
        // a file an older copy holds as it is now (a save that went back, a file changed then changed back): taken from
        // it here instead of from the keeper, checked by its hash when the copy closes like any received file
        var older = new Dictionary<(string, long, string), string>();
        foreach (var (dir, manifest) in copies.Skip(1)) {
            foreach (var f in manifest.Files) {
                older.TryAdd((f.Path, f.Size, f.Hash), dir);
            }
        }

        var reused = 0;
        _missing = [];
        for (var i = 0; i < offer.Files.Length; i++) {
            var file = offer.Files[i];
            if (_received.TryGetValue(file.Path, out var hash) && hash == file.Hash) {
                continue;
            }

            _received.Remove(file.Path);
            if (file.Size > 0 && !(unchanged.TryGetValue(file.Path, out var had) && had == (file.Size, file.Hash))) {
                if (older.TryGetValue((file.Path, file.Size, file.Hash), out var from) && WorldCopyStore.Reuse(folder, from, file)) {
                    _received[file.Path] = file.Hash;
                    reused++;
                    continue;
                }

                _missing.Add(i);
            }
        }

        _incoming = offer;
        _previous = previous;
        if (reused > 0) {
            EmpLog.Information("World copy: {Reused} file(s) taken from an older copy here, not asked of the keeper", reused);
        }

        if (_missing.Count == 0) {
            Close();
            return;
        }

        EmpLog.Debug("World copy: asking the host for {Count} of {Files} files ({Reused} taken from an older copy here)",
            _missing.Count, offer.Files.Length, reused);
        (NetSession.Instance.Transport as ElinNetClient)?.Host.Send(new WorldCopyWant {
            Saved = offer.Saved,
            Files = [.._missing],
        });
    }

    internal static void Piece(WorldCopyPiece piece)
    {
        if (_incoming is not { } offer || piece.Saved != offer.Saved) {
            return;
        }

        try {
            if (piece.Data is not { Length: > 0 } data || !_missing.Contains(piece.File)) {
                throw new InvalidDataException($"part of file {piece.File}, not asked for");
            }

            var file = offer.Files[piece.File];
            if (_stream is null) {
                var path = WorldCopyStore.ToLocal(Path.Combine(_folder, WorldCopyStore.Incoming), file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                _file = piece.File;
            }

            if (_file != piece.File || piece.Offset != _stream.Position || piece.Offset + data.Length > file.Size) {
                throw new InvalidDataException($"part of file {piece.File} at {piece.Offset}, expected file {_file} at {_stream.Position}");
            }

            _stream.Write(data, 0, data.Length);
            if (_stream.Position < file.Size) {
                return;
            }

            _stream.Dispose();
            _stream = null;

            // its hash is checked with all the others when the copy is closed
            _received[file.Path] = file.Hash;
            _missing.Remove(piece.File);

            if (_missing.Count == 0) {
                Close();
            }
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
            // the files that are whole stay: the host's next save asks for the others only
            EmpLog.Warning("World copy interrupted, the previous one stays: {Why}", ex.Message);
            Drop();
        }
    }

    private static void Drop()
    {
        _stream?.Dispose();
        _stream = null;
        _incoming = null;
    }

    private static void Close()
    {
        var (folder, manifest, previous) = (_folder, _incoming!, _previous);
        var received = new HashSet<string>(_received.Keys);

        // closed or not, the incoming folder is gone after this
        _incoming = null;
        _received.Clear();

        _closing = STask.Run(() => WorldCopyStore.Close(folder, manifest, received, previous));
        EmpMod.Instance.StartCoroutine(Closing(folder, manifest));
    }

    private static IEnumerator Closing(string folder, WorldCopyManifest manifest)
    {
        while (!_closing!.IsCompleted) {
            yield return null;
        }

        // (a file taken from an older copy that was not right fails the copy as a received one would: the next one
        // asks the keeper for everything, never the same bad file again)
        _badPrevious = _closing.IsFaulted || _closing.Result is WorldCopyStore.Closed.BadPrevious or WorldCopyStore.Closed.Failed;
        if (_closing.IsFaulted) {
            EmpLog.Warning(_closing.Exception!, "World copy not kept");
        } else if (_closing.Result == WorldCopyStore.Closed.Kept) {
            // a peer-hosted world: the guest compares this save with the one before (PLAN_peer_hosted_server 4.5)
            Helper.CrossCheck.CopyKept(WorldCopyStore.DirOf(folder, manifest), manifest);
        }

        _closing = null;

        var waiting = _waiting;
        _waiting = null;
        if (waiting is not null && NetSession.Instance.Transport is ElinNetClient) {
            Offer(waiting);
        }
    }

#if DEBUG
    internal static string State =>
        $"{(_closing is not null ? "closing" : _incoming is not null ? "receiving" : "idle")} missing={(_incoming is null ? 0 : _missing.Count)} received={_received.Count}";
#endif
}
