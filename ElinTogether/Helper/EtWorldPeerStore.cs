using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElinTogether.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElinTogether.Helper;

/// <summary>
///     The world store of the election (IWorldStore) on the ETWorld service the save depot names
///     (DepotPath "etworld:https://…", P2's EtWorldDepot does the signing, the key and the registration). Requests as
///     dev/etworld/API.md writes them; a save folder goes up as its files, each stored once under the SHA-256 of its
///     gzip, only the blobs this game did not send before are sent
/// </summary>
internal sealed class EtWorldPeerStore : IWorldStore
{
    private const long MaxWorld = 512L * 1024 * 1024;

    // path -> (sha256 of the file, its entry in the last version this game sent or read): not packed nor sent again
    private readonly Dictionary<string, (string Sha, JObject Entry)> _known = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool Ready => SaveDepot.EtWorldService is not null;

    private static (int Status, JObject? Json, byte[] Body) Call(string method, string path, JObject? body = null,
        byte[]? raw = null, string? signed = null, bool rawAnswer = false)
    {
        if (SaveDepot.EtWorldService is not { } service) {
            throw new IOException("no world service set");
        }

        var bytes = raw ?? (body is null ? null : Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
        return EtWorldDepot.Request(service.Address, service.KeyFile, PeerKeeper.PlayerId, Environment.UserName, method, path,
            bytes, signed, rawAnswer);
    }

    private static string Error((int Status, JObject? Json, byte[] Body) reply)
    {
        return (string?)reply.Json?["error"] ?? (reply.Status >= 500 ? throw Down(reply) : "status " + reply.Status);
    }

    private static IOException Down((int Status, JObject? Json, byte[] Body) reply)
    {
        return new IOException($"the world service answered {reply.Status} {(string?)reply.Json?["error"]}".TrimEnd());
    }

    private static ulong Steam(JToken? token)
    {
        return ulong.TryParse((string?)token, out var id) ? id : 0;
    }

    private static long Long(JToken? token)
    {
        return token is { Type: JTokenType.Integer } ? (long)token : 0;
    }

    private static StoreLease? Lease(JToken? token)
    {
        return token is not JObject lease ? null : new StoreLease {
            Key = (string?)lease["key"] ?? "",
            Steam = Steam(lease["steam"]),
            Name = (string?)lease["name"] ?? "",
            Since = Long(lease["since"]),
            Until = Long(lease["until"]),
            Handover = Long(lease["handover"]),
            Rollback = Long(lease["rollback"]),
            Succession = (lease["succession"] as JArray)?.Select(Steam).Where(s => s != 0).ToArray() ?? [],
        };
    }

    private static StoreVersion? Version(JToken? token)
    {
        return token is not JObject v ? null : new StoreVersion {
            N = Long(v["n"]),
            Handover = Long(v["handover"]),
            Reason = (string?)v["reason"] ?? "",
            Time = Long(v["time"]),
            Steam = Steam(v["steam"]),
            Name = (string?)v["name"] ?? "",
            SaveTime = (string?)v["save_time"] ?? "",
            Files = (v["files"] as JArray)?.OfType<JObject>().Select(f => new StoreFile {
                Path = (string?)f["path"] ?? "",
                Size = Long(f["size"]),
                Sha256 = (string?)f["sha256"] ?? "",
                Blob = (string?)f["blob"] ?? "",
                Enc = (string?)f["enc"] ?? "raw",
            }).ToArray() ?? [],
        };
    }

    public StoreWho Who()
    {
        var reply = Call("GET", "/v1/w/{w}/who");
        if (reply.Status != 200 || reply.Json is not { } who) {
            throw Down(reply);
        }

        var reserved = who["reserved"] as JObject;
        var you = who["you"] as JObject;
        return new() {
            Now = Long(who["now"]),
            Lease = Lease(who["lease"]),
            ReservedFor = Steam(reserved?["steam"]),
            ReservedUntil = Long(reserved?["until"]),
            Head = Version(who["head"]),
            Eligible = (bool?)you?["eligible"] ?? false,
            Why = (string?)you?["why"] ?? "",
            MyKey = (string?)you?["key"] ?? "",
        };
    }

    public StoreReply<StoreLease> Take()
    {
#if DEBUG
        const bool debug = true;
#else
        const bool debug = false;
#endif
        var reply = Call("POST", "/v1/w/{w}/take", new JObject { ["version"] = ModInfo.Version, ["debug"] = debug });
        if (reply.Status == 200 && Lease(reply.Json?["lease"]) is { } lease) {
            return StoreReply<StoreLease>.Of(lease);
        }

        return StoreReply<StoreLease>.No(Error(reply), Lease(reply.Json?["lease"]));
    }

    public StoreReply<StoreBeat> Beat(long handover, IReadOnlyList<ulong> succession)
    {
        var body = new JObject { ["handover"] = handover };
        if (succession.Count > 0) {
            body["succession"] = new JArray(succession.Take(32).Select(s => s.ToString()));
        }

        var reply = Call("POST", "/v1/w/{w}/beat", body);
        if (reply.Status == 200 && reply.Json is { } beat) {
            return StoreReply<StoreBeat>.Of(new() {
                Until = Long(beat["until"]),
                Head = Long(beat["head"]),
                Rollback = Long(beat["rollback"]),
                Cert = (string?)beat["cert"],
            });
        }

        return StoreReply<StoreBeat>.No(Error(reply), Lease(reply.Json?["lease"]));
    }

    public StoreReply<string> Cert(long handover, ulong lobby)
    {
        var reply = Call("POST", "/v1/w/{w}/cert", new JObject { ["handover"] = handover, ["lobby"] = lobby.ToString() });
        if (reply.Status == 200 && (string?)reply.Json?["cert"] is { Length: > 0 } cert) {
            return StoreReply<string>.Of(cert);
        }

        return StoreReply<string>.No(Error(reply), Lease(reply.Json?["lease"]));
    }

    public StoreReply<bool> Release(long handover, ulong next)
    {
        var body = new JObject { ["handover"] = handover };
        if (next != 0) {
            body["next"] = next.ToString();
        }

        var reply = Call("POST", "/v1/w/{w}/release", body);
        return reply.Status == 200 ? StoreReply<bool>.Of(true) : StoreReply<bool>.No(Error(reply), Lease(reply.Json?["lease"]));
    }

    public StoreVersion? Head()
    {
        var reply = Call("GET", "/v1/w/{w}/head");
        if (reply.Status == 404 && (string?)reply.Json?["error"] == "empty") {
            return null;
        }

        if (reply.Status != 200) {
            throw Down(reply);
        }

        var head = Version(reply.Json);
        lock (_gate) {
            // (what the head holds is not sent again by this game)
            foreach (var file in (reply.Json?["files"] as JArray)?.OfType<JObject>() ?? []) {
                if ((string?)file["path"] is { } path && (string?)file["sha256"] is { } sha) {
                    _known[path] = (sha, file);
                }
            }
        }

        return head;
    }

    private sealed class Piece
    {
        internal JObject Entry = null!;
        internal byte[]? Stored;
    }

    public StoreReply<long> Put(string folder, long parent, long handover, string reason, string gameDate, string saveTime)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var pieces = new List<Piece>();
        long total = 0;
        lock (_gate) {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
                var path = file.Substring(root.Length).Replace('\\', '/');
                // (the game's own scratch files are not the world, SaveDepot leaves them out the same way)
                if (path.StartsWith("Temp/") || path.EndsWith(".tmp")) {
                    continue;
                }

                var raw = File.ReadAllBytes(file);
                total += raw.Length;
                if (total > MaxWorld) {
                    return StoreReply<long>.No("too_big");
                }

                var sha = EtWorldDepot.Sha256Hex(raw);
                if (_known.TryGetValue(path, out var known) && known.Sha == sha) {
                    pieces.Add(new() { Entry = known.Entry });
                    continue;
                }

                var stored = EtWorldDepot.GzipBytes(raw);
                pieces.Add(new() {
                    Stored = stored,
                    Entry = new JObject {
                        ["path"] = path, ["size"] = raw.Length, ["sha256"] = sha, ["blob"] = EtWorldDepot.Sha256Hex(stored),
                        ["enc"] = "gzip",
                    },
                });
            }
        }

        if (pieces.Count == 0) {
            return StoreReply<long>.No("bad_field");
        }

        var since = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 0; attempt < 3; attempt++) {
            foreach (var piece in pieces.Where(p => p.Stored is not null)) {
                KeepLease(since, handover);
                var blob = (string)piece.Entry["blob"]!;
                var sent = Call("PUT", "/v1/blob/" + blob, raw: piece.Stored, signed: blob);
                if (sent.Status is not (200 or 201)) {
                    return StoreReply<long>.No(Error(sent), Lease(sent.Json?["lease"]));
                }

                piece.Stored = null;
            }

            var manifest = new JObject {
                ["parent"] = parent > 0 ? parent : null,
                ["handover"] = handover,
                ["reason"] = reason,
                ["game_date"] = gameDate.Length > 40 ? gameDate.Substring(0, 40) : gameDate,
                ["save_time"] = saveTime,
                ["note"] = "",
                ["files"] = new JArray(pieces.Select(p => p.Entry)),
            };
            var reply = Call("PUT", "/v1/w/{w}/version", manifest);
            if (reply.Status is 200 or 201) {
                lock (_gate) {
                    foreach (var piece in pieces) {
                        _known[(string)piece.Entry["path"]!] = ((string)piece.Entry["sha256"]!, piece.Entry);
                    }
                }

                return StoreReply<long>.Of(Long(reply.Json?["n"]));
            }

            var error = Error(reply);
            if (error == "missing_blobs") {
                // (unreferenced blobs the store dropped meanwhile: they go again)
                var missing = new HashSet<string>((reply.Json?["blobs"] as JArray)?.Select(b => (string?)b ?? "") ?? []);
                foreach (var piece in pieces.Where(p => missing.Contains((string)p.Entry["blob"]!))) {
                    var path = Path.Combine(root, ((string)piece.Entry["path"]!).Replace('/', Path.DirectorySeparatorChar));
                    var stored = EtWorldDepot.GzipBytes(File.ReadAllBytes(path));
                    piece.Entry = (JObject)piece.Entry.DeepClone();
                    piece.Entry["blob"] = EtWorldDepot.Sha256Hex(stored);
                    piece.Entry["enc"] = "gzip";
                    piece.Stored = stored;
                }

                continue;
            }

            return StoreReply<long>.No(error, Lease(reply.Json?["lease"]),
                error == "parent_mismatch" ? Long(reply.Json?["head"]) : error == "rolled_back" ? Long(reply.Json?["rollback"]) : 0);
        }

        return StoreReply<long>.No("missing_blobs");
    }

    // a first world on a slow line takes longer than the lease lives: renewed on the way (the beats of the game
    // wait behind this request in PeerKeeper's queue)
    private static void KeepLease(System.Diagnostics.Stopwatch since, long handover)
    {
        if (handover <= 0 || since.Elapsed.TotalSeconds < 15) {
            return;
        }

        since.Restart();
        Call("POST", "/v1/w/{w}/beat", new JObject { ["handover"] = handover });
    }

    public string Download(StoreVersion version, string folder, string? reuse, long handover)
    {
        var since = System.Diagnostics.Stopwatch.StartNew();
        // what this disk holds already, by its hash (the copy kept of the world)
        var have = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reuse is not null && Directory.Exists(reuse)) {
            foreach (var file in Directory.GetFiles(reuse, "*", SearchOption.AllDirectories)) {
                try {
                    have[EtWorldDepot.Sha256Hex(File.ReadAllBytes(file))] = file;
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // fetched instead
                }
            }
        }

        var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var file in version.Files) {
            if (!WorldCopyStore.IsSafePath(file.Path) || file.Size < 0) {
                return "the store's version names a file that cannot be written: " + file.Path;
            }

            total += file.Size;
            if (total > MaxWorld) {
                return "the store's version is too big";
            }

            var target = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                return "the store's version names a file outside the save: " + file.Path;
            }

            byte[]? raw = null;
            if (have.TryGetValue(file.Sha256, out var there)) {
                raw = File.ReadAllBytes(there);
                if (!string.Equals(EtWorldDepot.Sha256Hex(raw), file.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    raw = null;
                }
            }

            if (raw is null) {
                KeepLease(since, handover);
                var stored = Call("GET", "/v1/blob/" + file.Blob, rawAnswer: true);
                if (stored.Status != 200) {
                    return "the store did not give the file " + file.Path + ": " + stored.Status;
                }

                if (!string.Equals(EtWorldDepot.Sha256Hex(stored.Body), file.Blob, StringComparison.OrdinalIgnoreCase)) {
                    return "the store sent a file that is not what its name says: " + file.Path;
                }

                raw = file.Enc == "gzip" ? EtWorldDepot.GunzipBytes(stored.Body, file.Size) : stored.Body;
                if (!string.Equals(EtWorldDepot.Sha256Hex(raw), file.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    return "a file of the world is not what the version says: " + file.Path;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, raw);
        }

        lock (_gate) {
            foreach (var file in version.Files) {
                _known[file.Path] = (file.Sha256, new JObject {
                    ["path"] = file.Path, ["size"] = file.Size, ["sha256"] = file.Sha256, ["blob"] = file.Blob, ["enc"] = file.Enc,
                });
            }
        }

        return "";
    }
}
