using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElinTogether.Helper;

/// <summary>
///     The save depot kept by the append-only world service (dev/etworld, "etworld:https://address"). It answers the
///     requests of SaveDepot (WHO, TAKE, PUT, BEAT, RELEASE, MODS) like GitHubDepot: the world travels as the same
///     archive, and here it is cut into its files, each stored once under the SHA-256 of its bytes. A version is the
///     list of those files; only what the service does not have yet is sent, only what the local copy does not have
///     is fetched. Who keeps the world is a lease of the service (45 s, renewed by BEAT), and a version is accepted
///     only when it follows the one the service has (its parent is the head): that refusal is the lock, as GitHub's
///     `sha` is. The service never overwrites or deletes anything a player sent. <br />
///     Every request is signed with this install's Ed25519 key (kept in ElinMP, never sent; the service knows its
///     public half, registered with the Steam id at the first contact). Nothing of the game in here: the test
///     (dev/_tools/etworld_depot_cli) compiles this file and Ed25519.cs alone
/// </summary>
internal static class EtWorldDepot
{
    // a blob of the service is at most 32 MB; what comes back is held to a little more
    private const int MaxBlob = 40 * 1024 * 1024;
    private const int MaxJson = 8 * 1024 * 1024;
    private const long MaxWorld = 512L * 1024 * 1024;

    private static readonly object _gate = new();

    private static string _base = "";
    private static string _world = "public";
    private static byte[]? _seed;
    private static string _pub = "";
    private static string _keyFile = "";

    // the service's clock minus this one, learned from a refusal "clock"
    private static long _skew;

    // the mod's version, said when the lease is taken
    private static string _build = "";
    private static bool _registered;

    // the lease this game holds (its handover number), 0: none
    private static long _handover;

    // the version this game took or last sent, 0: none
    private static long _version;

    // the files of that version, by path: what the next version does not send again
    private static Dictionary<string, JObject> _files = new();

    // the world was written by someone else while this game kept it: nothing more goes there until it is taken again
    private static bool _changed;

    // the host certificate of this lease (plan P3, API.md 4.1): from CERT, renewed by every BEAT; null without one
    private static volatile string? _cert;

    /// <summary>
    ///     The version of the depot's world this game plays from, as a text (for SaveDepot's marker). Null: none
    /// </summary>
    internal static string? Version => _version > 0 ? _version.ToString() : null;

    /// <summary>
    ///     Where the player who keeps the world is joined. The service does not say yet (the lobby comes with the
    ///     host certificate, plan step P3): empty, and the player is told to join through Steam
    /// </summary>
    internal static string HeldJoin => "";

    /// <summary>
    ///     The public half of this install's key, once a request was made (64 hex)
    /// </summary>
    internal static string PublicKey => _pub;

    /// <summary>
    ///     The world of the service this game uses ("public" unless the address says "#other")
    /// </summary>
    internal static string WorldName => _world;

    /// <summary>
    ///     The last host certificate the service gave this game's lease for its Steam lobby (plan P3), null: none
    /// </summary>
    internal static string? Certificate => _cert;

    static EtWorldDepot()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }

    /// <summary>
    ///     A version number as SaveDepot keeps it in its marker
    /// </summary>
    internal static bool IsVersion(string text)
    {
        return long.TryParse(text, out var n) && n > 0;
    }

    /// <summary>
    ///     One request, answered like SaveDepot.Ask. Refusals: "held name", "empty", "changed", "rolled back", "too big", "banned",
    ///     "too_new", "not_vouched", "debug_build", "reserved", "steam_mismatch", "address". The service not answering
    ///     is an IOException, with what it said
    /// </summary>
    /// <param name="address">what follows "etworld:": https://host[/][#world]</param>
    /// <param name="keyFile">where this install's key is kept; made at the first request</param>
    /// <param name="local">the local copy of the world: on TAKE, its files the version has are not fetched again</param>
    /// <param name="descends">PUT of a save kept from an earlier session: the version it was played from</param>
    internal static (bool Ok, string Text, byte[]? Body) Ask(string address, string keyFile, ulong steam, string name,
        string command, byte[]? body = null, string? local = null, string? descends = null, bool debug = false,
        string build = "")
    {
        lock (_gate) {
            try {
                Use(address, keyFile);
                _build = build.Length > 40 ? build.Substring(0, 40) : build;
                var words = command.Split(' ');
                return words[0] switch {
                    "WHO" => Who(steam, name),
                    "TAKE" => Take(steam, name, local, debug),
                    "PUT" => Put(steam, name, body, descends, words.Length > 1 ? words[1] : "periodic", debug),
                    "BEAT" => Beat(steam, name, debug),
                    "RELEASE" => Release(steam, name),
                    "CERT" => Cert(steam, name, words.Length > 1 ? words[1] : ""),
                    "MODS" => (true, "", null),
                    _ => throw new No("unknown"),
                };
            } catch (No no) {
                return (false, no.Message, null);
            } catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException or
                                             NullReferenceException or InvalidDataException) {
                throw new IOException("the world service sent something unreadable: " + ex.Message);
            }
        }
    }

    private static void Use(string address, string keyFile)
    {
        var at = address.IndexOf('#');
        var world = at >= 0 ? address.Substring(at + 1) : "public";
        var url = (at >= 0 ? address.Substring(0, at) : address).Trim().TrimEnd('/');
        // https only; plain http for a service on this machine (the bench's fake)
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.PathAndQuery != "/" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.Host is "127.0.0.1" or "localhost") ||
            !System.Text.RegularExpressions.Regex.IsMatch(world, "^[A-Za-z0-9_-]{1,40}$")) {
            throw new No("address");
        }

        if (url != _base || world != _world) {
            _base = url;
            _world = world;
            _registered = _changed = false;
            _handover = _version = 0;
            _files = new();
        }

        if (keyFile != _keyFile || _seed is null) {
            _keyFile = keyFile;
            _seed = LoadKey(keyFile);
            _pub = Hex(Ed25519.PublicKey(_seed));
            _registered = false;
        }
    }

    /// <summary>
    ///     The key of this install: 32 random bytes in hex, made the first time and kept. A key file that cannot be
    ///     read is never replaced (the service knows this install by it): the request fails instead
    /// </summary>
    private static byte[] LoadKey(string file)
    {
        if (File.Exists(file)) {
            var text = File.ReadAllText(file).Trim();
            if (text.Length != 64) {
                throw new IOException("the world service key file is damaged: " + file);
            }

            return Unhex(text);
        }

        var seed = Ed25519.NewSeed();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        // beside, then moved: a game closed while writing leaves no half key
        File.WriteAllText(file + ".new", Hex(seed));
        File.Move(file + ".new", file);
        return seed;
    }

    // ------------------------------------------------------------------------------------------------- requests

    private static (bool, string, byte[]?) Who(ulong steam, string name)
    {
        var who = Call("GET", $"/v1/w/{_world}/who", null, steam, name);
        var lease = who.Json?["lease"] as JObject;
        if (who.Status != 200) {
            throw Down(who);
        }

        return (true, lease is not null && (string?)lease["key"] != _pub ? Holder(lease) : "", null);
    }

    private static (bool, string, byte[]?) Take(ulong steam, string name, string? local, bool debug)
    {
        _changed = false;
        _version = 0;
        _files = new();
        var head = Lease(steam, name, debug);
        if (head is null) {
            Free(steam, name);
            throw new No("empty");
        }

        try {
            var manifest = Get($"/v1/w/{_world}/head", steam, name);
            var world = Fetch(manifest, local, steam, name);
            _version = (long)manifest["n"]!;
            Remember(manifest);
            return (true, "", world);
        } catch (Exception) {
            Free(steam, name);
            throw;
        }
    }

    private static (bool, string, byte[]?) Put(ulong steam, string name, byte[]? body, string? descends, string reason,
        bool debug)
    {
        if (body is null || body.Length == 0) {
            throw new No("empty");
        }

        if (_changed) {
            throw new No("changed");
        }

        // the lease first: a player who does not keep the world writes none
        var lent = false;
        if (_handover == 0) {
            var head = Lease(steam, name, debug);
            if (_version == 0) {
                // a save kept from an earlier session goes only over the version it came from; a save put in the
                // depot while nobody keeps it follows whatever is there (the history keeps it)
                lent = descends is not null && IsVersion(descends);
                _version = lent ? long.Parse(descends!) : (long?)head?["n"] ?? 0;
            }

            // the world moved on while this game did not keep it: nothing is sent (unless the newer version is
            // this install's own, whose answer was lost)
            var there = (long?)head?["n"] ?? 0;
            if (there != _version && (string?)head?["key"] != _pub) {
                _changed = !lent;
                Free(steam, name);
                if (lent) {
                    _version = 0;
                }

                throw new No("changed");
            }

            _version = there;
        }

        var files = Cut(body);
        try {
            for (var attempt = 0; attempt < 3; attempt++) {
                SendBlobs(files, steam, name, all: false);
                var manifest = new JObject {
                    ["parent"] = _version > 0 ? _version : null,
                    ["handover"] = _handover,
                    ["reason"] = reason,
                    ["files"] = new JArray(files.Select(f => f.Entry)),
                    ["save_time"] = DateTime.UtcNow.ToString("o"),
                    ["note"] = "saved by " + name,
                };
                var reply = Call("PUT", $"/v1/w/{_world}/version", Bytes(manifest), steam, name);
                var error = (string?)reply.Json?["error"];
                if (reply.Status is 200 or 201) {
                    _version = (long)reply.Json!["n"]!;
                    _files = files.ToDictionary(f => (string)f.Entry["path"]!, f => f.Entry);
                    lent = false;
                    return (true, "", null);
                }

                switch (error) {
                    case "missing_blobs":
                        // the service dropped some unreferenced ones meanwhile: they all go
                        SendBlobs(files, steam, name, all: true);
                        continue;
                    case "parent_mismatch":
                        // a version of this lease (only this key writes under it) whose answer was lost: ours
                        var there = (long?)reply.Json?["head"] ?? 0;
                        if (there > 0 && Get($"/v1/w/{_world}/version/{there}", steam, name) is var made &&
                            (string?)made["key"] == _pub && (long?)made["handover"] == _handover) {
                            _version = there;
                            continue;
                        }

                        // someone else kept the world and saved meanwhile: their version stays, ours on this PC
                        _changed = true;
                        Free(steam, name);
                        throw new No("changed");
                    case "not_holder" or "handover_mismatch":
                        // cut off longer than the lease: taken back if nobody wrote since
                        Retake(steam, name, debug);
                        continue;
                    case "rolled_back":
                        _changed = true;
                        throw new No("rolled back");
                    case "too_big":
                        throw new No("too big");
                    case "banned":
                        throw new No("banned");
                    default:
                        throw Down(reply);
                }
            }

            throw new IOException("the world service keeps refusing the version");
        } finally {
            // a save of an earlier session that did not go: nothing of it stays behind (the lease neither), the
            // next try starts over from the lease
            if (lent) {
                _version = 0;
                _changed = false;
                Free(steam, name);
            }
        }
    }

    private static (bool, string, byte[]?) Beat(ulong steam, string name, bool debug)
    {
        if (_changed) {
            throw new No("changed");
        }

        if (_handover == 0) {
            // nothing taken or put by this game: a beat takes nothing either
            if (_version > 0) {
                Retake(steam, name, debug);
            }

            return (true, "", null);
        }

        var reply = Call("POST", $"/v1/w/{_world}/beat", Bytes(new JObject { ["handover"] = _handover }), steam, name);
        var error = (string?)reply.Json?["error"];
        if (reply.Status == 200) {
            if (reply.Json!["rollback"] is { Type: JTokenType.Integer }) {
                // an admin set the world back: nothing more of this session goes there (plan 4.3)
                _changed = true;
                _cert = null;
                throw new No("rolled back");
            }

            // (a holder that told its lobby gets a fresh certificate with every beat)
            if (reply.Json["cert"] is { Type: JTokenType.String } cert) {
                _cert = (string?)cert;
            }

            return (true, "", null);
        }

        if (error == "not_holder") {
            Retake(steam, name, debug);
            return (true, "", null);
        }

        throw error == "banned" ? new No("banned") : Down(reply);
    }

    /// <summary>
    ///     A host certificate naming this game's Steam lobby (plan P3): its text, written in the lobby by the game
    /// </summary>
    private static (bool, string, byte[]?) Cert(ulong steam, string name, string lobby)
    {
        if (_handover == 0) {
            throw new No("not_holder");
        }

        var reply = Call("POST", $"/v1/w/{_world}/cert", Bytes(new JObject { ["handover"] = _handover, ["lobby"] = lobby }),
            steam, name);
        if (reply.Status == 200 && (string?)reply.Json?["cert"] is { Length: > 0 } cert) {
            _cert = cert;
            return (true, cert, null);
        }

        var error = (string?)reply.Json?["error"];
        throw error is "not_holder" or "banned" or "no_service_key" or "bad_field" ? new No(error) : Down(reply);
    }

    private static (bool, string, byte[]?) Release(ulong steam, string name)
    {
        Free(steam, name);
        _changed = false;
        _version = 0;
        _files = new();
        return (true, "", null);
    }

    /// <summary>
    ///     The lease was lost (cut off longer than it lives): taken again when nobody wrote since this game's version,
    ///     else "held name" (someone keeps it) or "changed" (someone kept it and saved)
    /// </summary>
    private static void Retake(ulong steam, string name, bool debug)
    {
        _handover = 0;
        var head = Lease(steam, name, debug);
        var there = (long?)head?["n"] ?? 0;
        // (a newer version of this install's own, whose answer was lost: ours)
        if (there != _version && _version > 0 && (string?)head?["key"] != _pub) {
            _changed = true;
            Free(steam, name);
            throw new No("changed");
        }

        _version = there;
    }

    /// <summary>
    ///     Takes the lease (or keeps it). The head's summary, null when the world has no version yet
    /// </summary>
    private static JObject? Lease(ulong steam, string name, bool debug)
    {
        var reply = Call("POST", $"/v1/w/{_world}/take", Bytes(new JObject { ["version"] = _build, ["debug"] = debug }), steam, name);
        var error = (string?)reply.Json?["error"];
        if (reply.Status == 200) {
            var handover = (long)reply.Json!["lease"]!["handover"]!;
            if (handover != _handover) {
                // (a new lease: the certificate of the old one names its handover)
                _cert = null;
            }

            _handover = handover;
            return reply.Json["head"] as JObject;
        }

        throw error switch {
            "held" => new No("held " + Holder(reply.Json?["lease"] as JObject)),
            "reserved" or "banned" or "too_new" or "not_vouched" or "debug_build" => new No(error),
            _ => Down(reply),
        };
    }

    private static void Free(ulong steam, string name)
    {
        if (_handover == 0) {
            return;
        }

        try {
            Call("POST", $"/v1/w/{_world}/release", Bytes(new JObject { ["handover"] = _handover }), steam, name);
        } catch (IOException) {
            // the lease ends by itself
        }

        _handover = 0;
        _cert = null;
    }

    private static string Holder(JObject? lease)
    {
        var who = (string?)lease?["name"];
        return who is { Length: > 0 } ? who : (string?)lease?["steam"] ?? "?";
    }

    // --------------------------------------------------------------------------------------------- the files

    private sealed class Piece
    {
        internal JObject Entry = null!;
        internal byte[] Raw = null!;
        internal byte[]? Stored;
    }

    /// <summary>
    ///     The archive SaveDepot made, cut into its files. A file the last version has (same path, same bytes) keeps
    ///     its stored name and is not packed again
    /// </summary>
    private static List<Piece> Cut(byte[] archive)
    {
        var files = new List<Piece>();
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries) {
            var path = entry.FullName.Replace('\\', '/');
            if (path.EndsWith("/")) {
                continue;
            }

            using var from = entry.Open();
            using var raw = new MemoryStream();
            from.CopyTo(raw);
            var bytes = raw.ToArray();
            var sha = Sha(bytes);
            if (_files.TryGetValue(path, out var old) && (string?)old["sha256"] == sha) {
                files.Add(new Piece { Entry = old, Raw = bytes });
                continue;
            }

            var stored = Gzip(bytes);
            files.Add(new Piece {
                Raw = bytes,
                Stored = stored,
                Entry = new JObject {
                    ["path"] = path, ["size"] = bytes.Length, ["sha256"] = sha, ["blob"] = Sha(stored), ["enc"] = "gzip",
                },
            });
        }

        return files;
    }

    /// <param name="all">every blob of the version, not only the ones the last version did not have</param>
    private static void SendBlobs(List<Piece> files, ulong steam, string name, bool all)
    {
        var sent = new HashSet<string>();
        var since = System.Diagnostics.Stopwatch.StartNew();
        foreach (var file in files) {
            if (file.Stored is null && !all) {
                continue;
            }

            // a first world on a slow line takes longer than the lease lives: it is kept on the way (the beats
            // of the game wait behind this request)
            if (since.Elapsed.TotalSeconds > 15 && _handover > 0) {
                since.Restart();
                Call("POST", $"/v1/w/{_world}/beat", Bytes(new JObject { ["handover"] = _handover }), steam, name);
            }

            var blob = (string)file.Entry["blob"]!;
            if (!sent.Add(blob)) {
                continue;
            }

            var bytes = file.Stored ?? ((string?)file.Entry["enc"] == "gzip" ? Gzip(file.Raw) : file.Raw);
            if (Sha(bytes) != blob) {
                // packed again by another runtime, other bytes: stored under their own name
                file.Entry = (JObject)file.Entry.DeepClone();
                file.Entry["blob"] = blob = Sha(bytes);
            }

            var reply = Call("PUT", "/v1/blob/" + blob, bytes, steam, name, blob);
            if (reply.Status is not (200 or 201)) {
                throw (string?)reply.Json?["error"] switch {
                    "too_big" => new No("too big"),
                    "not_holder" => new No("changed"),
                    _ => Down(reply),
                };
            }

            file.Stored = null;
        }
    }

    /// <summary>
    ///     The world of a version, as the archive SaveDepot unpacks. What the local copy holds already (same bytes,
    ///     wherever in the folder) is read there; the rest is fetched, and checked against its name
    /// </summary>
    private static byte[] Fetch(JObject manifest, string? local, ulong steam, string name)
    {
        var have = new Dictionary<string, string>();
        if (local is not null && Directory.Exists(local)) {
            foreach (var file in Directory.GetFiles(local, "*", SearchOption.AllDirectories)) {
                try {
                    have[Sha(File.ReadAllBytes(file))] = file;
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // fetched instead
                }
            }
        }

        long total = 0;
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
            foreach (var file in (JArray)manifest["files"]!) {
                var path = (string)file["path"]!;
                var sha = (string)file["sha256"]!;
                var size = (long)file["size"]!;
                if (size < 0) {
                    throw new IOException("the world service sent a version with a negative size: " + path);
                }

                total += size;
                if (total > MaxWorld) {
                    throw new No("too big");
                }

                byte[]? raw = null;
                if (have.TryGetValue(sha, out var there)) {
                    try {
                        raw = File.ReadAllBytes(there);
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        raw = null;
                    }
                }

                if (raw is null || Sha(raw) != sha) {
                    var blob = (string)file["blob"]!;
                    var stored = Call("GET", "/v1/blob/" + blob, null, steam, name, raw: true);
                    if (stored.Status != 200) {
                        throw Down(stored);
                    }

                    if (Sha(stored.Body) != blob) {
                        throw new IOException("the world service sent a file that is not what its name says: " + path);
                    }

                    raw = (string?)file["enc"] == "gzip" ? Gunzip(stored.Body, (long)file["size"]!) : stored.Body;
                    if (Sha(raw) != sha) {
                        throw new IOException("a file of the world is not what the version says: " + path);
                    }
                }

                var entry = zip.CreateEntry(path);
                using var to = entry.Open();
                to.Write(raw, 0, raw.Length);
            }
        }

        return bytes.ToArray();
    }

    private static void Remember(JObject manifest)
    {
        _files = ((JArray)manifest["files"]!).Cast<JObject>().ToDictionary(f => (string)f["path"]!, f => f);
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var to = new MemoryStream();
        using (var gz = new GZipStream(to, CompressionLevel.Optimal, true)) {
            gz.Write(raw, 0, raw.Length);
        }

        return to.ToArray();
    }

    private static byte[] Gunzip(byte[] stored, long size)
    {
        using var from = new GZipStream(new MemoryStream(stored), CompressionMode.Decompress);
        using var to = new MemoryStream();
        var piece = new byte[81920];
        for (var n = from.Read(piece, 0, piece.Length); n > 0; n = from.Read(piece, 0, piece.Length)) {
            to.Write(piece, 0, n);
            // (a small blob that unfolds into a full disk is not a file of the world)
            if (to.Length > size) {
                throw new IOException("a file of the world is bigger than the version says");
            }
        }

        return to.ToArray();
    }

    // --------------------------------------------------------------------------------------------- the wire

    private static JObject Get(string path, ulong steam, string name)
    {
        var reply = Call("GET", path, null, steam, name);
        if (reply.Status != 200 || reply.Json is null) {
            throw (string?)reply.Json?["error"] == "empty" ? new No("empty") : Down(reply);
        }

        return reply.Json;
    }

    /// <summary>
    ///     One signed request; registers this install's key first when the service does not know it, and once more
    ///     with the service's clock when this machine's is off
    /// </summary>
    /// <param name="signed">the body hash that is signed: the blob's name for a blob sent, else the SHA-256 of the body</param>
    private static (int Status, JObject? Json, byte[] Body) Call(string method, string path, byte[]? body, ulong steam,
        string name, string? signed = null, bool raw = false)
    {
        if (!_registered) {
            Register(steam, name);
        }

        for (var attempt = 0; ; attempt++) {
            var reply = Send(method, path, body, signed, raw);
            var error = reply.Status == 401 ? (string?)reply.Json?["error"] : null;
            if (attempt == 0 && error == "clock" && (long?)reply.Json?["now"] is { } now) {
                _skew = now - Now(0);
                continue;
            }

            if (attempt == 0 && error == "unregistered") {
                Register(steam, name);
                continue;
            }

            return reply;
        }
    }

    private static void Register(ulong steam, string name)
    {
        var body = Bytes(new JObject { ["steam"] = steam.ToString(), ["name"] = name });
        for (var attempt = 0; attempt < 2; attempt++) {
            var reply = Send("POST", "/v1/register", body, null, false);
            if (reply.Status is 200 or 201) {
                _registered = true;
                return;
            }

            var error = (string?)reply.Json?["error"];
            if (attempt == 0 && error == "clock" && (long?)reply.Json?["now"] is { } now) {
                _skew = now - Now(0);
                continue;
            }

            // this key was registered with another Steam id (another account on this PC): said, never forced
            throw error == "steam_mismatch" ? new No("steam_mismatch") : Down(reply);
        }
    }

    private static long Now(long skew)
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds + skew;
    }

    private static (int Status, JObject? Json, byte[] Body) Send(string method, string path, byte[]? body,
        string? signed, bool raw)
    {
        var content = body ?? [];
        var time = Now(_skew).ToString();
        var nonce = Nonce();
        var message = $"ETW1\n{method}\n{path}\n{signed ?? Sha(content)}\n{time}\n{nonce}";
        var sig = Hex(Ed25519.Sign(_seed!, Encoding.UTF8.GetBytes(message)));
        try {
#pragma warning disable SYSLIB0014 // HttpWebRequest is what was seen working from the game's Mono
            var request = (HttpWebRequest)WebRequest.Create(_base + path);
#pragma warning restore SYSLIB0014
            request.Method = method;
            request.UserAgent = "ElinTogether";
            request.Headers["X-ET-Key"] = _pub;
            request.Headers["X-ET-Time"] = time;
            request.Headers["X-ET-Nonce"] = nonce;
            request.Headers["X-ET-Sig"] = sig;
            // (a file gets a second more for every 50 KB)
            request.Timeout = request.ReadWriteTimeout = 30000 + content.Length / 50;
            request.AllowAutoRedirect = false;
            if (method != "GET") {
                request.ContentType = raw || path.StartsWith("/v1/blob/") ? "application/octet-stream" : "application/json";
                request.ContentLength = content.Length;
                using var to = request.GetRequestStream();
                to.Write(content, 0, content.Length);
            }

            HttpWebResponse response;
            try {
                response = (HttpWebResponse)request.GetResponse();
            } catch (WebException ex) when (ex.Response is HttpWebResponse refused) {
                response = refused;
            }

            using (response) {
                var status = (int)response.StatusCode;
                var limit = raw ? MaxBlob : MaxJson;
                using var answer = new MemoryStream();
                using (var from = response.GetResponseStream()) {
                    var piece = new byte[81920];
                    for (var n = from.Read(piece, 0, piece.Length); n > 0; n = from.Read(piece, 0, piece.Length)) {
                        answer.Write(piece, 0, n);
                        if (answer.Length > limit) {
                            throw new IOException("the world service sent more than a file of the world");
                        }
                    }
                }

                var bytes = answer.ToArray();
                JObject? json = null;
                if (!(raw && status == 200) && bytes.Length > 0) {
                    try {
                        json = JToken.Parse(Encoding.UTF8.GetString(bytes)) as JObject;
                    } catch (JsonException) {
                        // a page of a proxy, not the service: told by its status
                    }
                }

                return (status, json, bytes);
            }
        } catch (WebException ex) {
            throw new IOException("the world service does not answer: " + ex.Status);
        }
    }

    private static IOException Down((int Status, JObject? Json, byte[] Body) reply)
    {
        var error = (string?)reply.Json?["error"] ?? "";
        return new IOException($"the world service answered {reply.Status} {error}".TrimEnd());
    }

    private static byte[] Bytes(JObject json)
    {
        return Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
    }

    private static string Nonce()
    {
        var bytes = new byte[18];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }

    private static string Sha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    private static string Hex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    private static byte[] Unhex(string text)
    {
        var bytes = new byte[text.Length / 2];
        for (var i = 0; i < bytes.Length; i++) {
            bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private sealed class No : Exception
    {
        internal No(string why) : base(why)
        {
        }
    }
}
