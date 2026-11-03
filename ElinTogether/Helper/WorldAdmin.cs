using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using ElinTogether.Components;
using ElinTogether.LangMod;
using ElinTogether.Net;
using HeathenEngineering.SteamworksIntegration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     The admin side of the peer-hosted public world (PLAN_peer_hosted_server 4.3, 4.4, step P6): the versions the
///     world service keeps, a rollback (a NEW version made of an old one's files: nothing is deleted), a ban from
///     keeping the world, and the signed rules, admins and bans documents. Every request is signed with the admin's
///     own Ed25519 key, ElinMP/etworld_admin.key (64 hex, made by dev/etworld/make_admin_key.py on the admin's PC):
///     read only when present, never sent, never logged, never written by the mod. The page shows only when that key
///     exists and this player's Steam id is an admin of the signed list (Shown). The requests run off the main thread;
///     what they bring back is read by the page (TabWorldAdmin), which is reopened when an answer arrives
/// </summary>
internal static class WorldAdmin
{
    internal static readonly string[] DocKinds = ["rules", "admins", "bans"];

    private static string KeyFile => Path.Combine(Application.persistentDataPath, "ElinMP",
#if DEBUG
        // bench windows share this folder and are different players
        EmpConfig.Dev.Identity.Value > 0 ? $"etworld_admin_{EmpConfig.Dev.Identity.Value}.key" :
#endif
        "etworld_admin.key");

    internal static bool HasKey
    {
        get {
            try {
                return File.Exists(KeyFile);
            } catch (Exception) {
                return false;
            }
        }
    }

    internal static ulong MySteam => NetSession.Instance.Self?.User ?? (ulong)UserData.Me;

    /// <summary>
    ///     The admin page is shown: the admin key is on this PC and this player is an admin of the signed list
    /// </summary>
    internal static bool Shown => HasKey && PeerHosting.IsSignedAdmin(MySteam);

    /// <summary>
    ///     The world service of the depot setting ("etworld:https://host[#world]"), null when the depot is not one
    /// </summary>
    internal static (string Base, string World)? Address
    {
        get {
            var root = EmpConfig.Client.DepotPath.Value.Trim();
            if (!root.StartsWith("etworld:", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }

            var address = root.Substring(8);
            var at = address.IndexOf('#');
            var world = at >= 0 ? address.Substring(at + 1) : "public";
            var url = (at >= 0 ? address.Substring(0, at) : address).Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.PathAndQuery != "/" ||
                !(uri.Scheme == "https" || uri.Scheme == "http" && uri.Host is "127.0.0.1" or "localhost") ||
                !Regex.IsMatch(world, "^[A-Za-z0-9_-]{1,40}$")) {
                return null;
            }

            return (url, world);
        }
    }

    // ------------------------------------------------------------------------------------- what the page reads

    internal sealed class VersionRow
    {
        internal long N;
        internal string Reason = "";
        internal long Time;
        internal string Key = "";
        internal string Steam = "";
        internal string Name = "";
        internal long Size;
        internal int Count;
        internal string GameDate = "";
        internal long? RollbackOf;
    }

    internal sealed class HostBan
    {
        internal string Target = "";
        internal long Time;
        internal string Reason = "";
    }

    /// <summary>
    ///     The guests' cross-check reports on one version (plan 4.5, P7): how many, from how many independent players
    ///     (never the one who kept it), and the kinds reported
    /// </summary>
    internal sealed class FlagRow
    {
        internal int Count;
        internal int Reporters;
        internal bool Strong;
        internal string Kinds = "";
        internal string Detail = "";
    }

    internal static List<VersionRow> Versions { get; private set; } = [];

    /// <summary>
    ///     Flagged versions by number (none: no report, or a service without the flag routes)
    /// </summary>
    internal static Dictionary<long, FlagRow> Flags { get; private set; } = [];
    internal static long Head { get; private set; }
    internal static string Keeper { get; private set; } = "";
    internal static List<HostBan> HostBans { get; private set; } = [];

    /// <summary>
    ///     The signed documents as they are now (the "doc" object of each), null: none yet; and their numbers
    /// </summary>
    internal static Dictionary<string, JObject?> Docs { get; } = DocKinds.ToDictionary(k => k, _ => (JObject?)null);

    internal static Dictionary<string, long> Seq { get; } = DocKinds.ToDictionary(k => k, _ => 0L);

    internal static bool Busy { get; private set; }

    /// <summary>
    ///     Read at least once since the game started
    /// </summary>
    internal static bool Loaded { get; private set; }

    /// <summary>
    ///     The last refusal or trouble, as a short text for the page (empty: none)
    /// </summary>
    internal static string Problem { get; private set; } = "";

    /// <summary>
    ///     The last thing done, for the page (empty: nothing yet)
    /// </summary>
    internal static string Done { get; private set; } = "";

    /// <summary>
    ///     Everything the page shows, read again
    /// </summary>
    internal static void Refresh() => Run(null, () => { });

    /// <summary>
    ///     The world goes back to version n: the service writes a NEW version with n's files (the head moves, every
    ///     version stays), the keeper is told by its next beat, announces it and leaves; the next keeper opens it.
    ///     The keys and Steam ids in ban may no longer keep the world
    /// </summary>
    internal static void Rollback(long n, IReadOnlyList<string> ban)
    {
        Run("emp_wa_done_rollback".Loc(n), () => {
            var body = new JObject {
                ["world"] = World,
                ["to"] = n,
                ["reason"] = "admin page",
                ["ban"] = new JArray(ban.Select(b => (object)b).ToArray()),
            };
            Expect(Send("POST", "/v1/admin/rollback", body), 201);
            EmpLog.Information("World admin: the world was rolled back to version {N} (bans {Bans})", n, ban.Count);
        });
    }

    /// <summary>
    ///     A key (64 hex) or "steam:id" may (on false) or may not (on true) keep the world any more; a banned keeper
    ///     loses the world at once
    /// </summary>
    internal static void Ban(IReadOnlyList<string> targets, bool on)
    {
        Run((on ? "emp_wa_done_ban" : "emp_wa_done_unban").lang(), () => {
            foreach (var target in targets) {
                Expect(Send("POST", "/v1/admin/ban", new JObject { ["target"] = target, ["on"] = on, ["reason"] = "admin page" }), 200);
            }

            EmpLog.Information("World admin: {Count} ban(s) from keeping {Change}", targets.Count, on ? "added" : "lifted");
        });
    }

    /// <summary>
    ///     The world service is told that this key is the player of this Steam id (POST /v1/admin/vouch, which
    ///     registers it if unknown): what the public server does for the players it saw over Steam (PublicVouch).
    ///     Off the game's thread, not through the page (no Busy, no refresh); <paramref name="done" /> gets the
    ///     status and the service's error code (0 and the message when the service did not answer), on that thread
    /// </summary>
    internal static void Vouch(string key, ulong steam, Action<int, string> done)
    {
        // (read here, on the game's thread, as Run does)
        if (Address is not { } address) {
            done(0, "no world service set");
            return;
        }

        var keyPath = KeyFile;
        System.Threading.Tasks.Task.Run(() => {
            try {
                (int Status, JObject? Json) reply;
                lock (_gate) {
                    _keyPath = keyPath;
                    _base = address.Base;
                    _world = address.World;
                }

                reply = Send("POST", "/v1/admin/vouch", new JObject { ["key"] = key, ["steam"] = steam.ToString(), ["on"] = true });
                done(reply.Status, (string?)reply.Json?["error"] ?? "");
            } catch (Exception ex) {
                done(0, ex.Message);
            }
        });
    }

    /// <summary>
    ///     One signed document, sent as the next number (the current one + 1, read just before): a list changed by
    ///     another admin meanwhile is refused, never overwritten
    /// </summary>
    internal static void PutDoc(string kind, JObject doc)
    {
        Run("emp_wa_done_doc".lang(), () => {
            var who = Expect(Send("GET", $"/v1/w/{World}/who", null), 200);
            var current = (long?)who["docs"]?[kind] ?? 0;
            if (current != Seq[kind]) {
                // (the page showed an older list: what it holds is not sent over the newer one)
                throw new Refused("emp_wa_err_seq".lang());
            }

            var body = new JObject { ["world"] = World, ["kind"] = kind, ["seq"] = current + 1, ["doc"] = doc };
            var reply = Send("POST", "/v1/admin/doc", body);
            if (reply.Status == 409) {
                throw new Refused("emp_wa_err_seq".lang());
            }

            Expect(reply, 201);
            EmpLog.Information("World admin: the signed {Kind} document {Seq} was sent", kind, current + 1);
        });
    }

    // ------------------------------------------------------------------------------------- the signed documents

    /// <summary>
    ///     The rules document made from this game's own World rules (the Server section of its config, as the config
    ///     file writes each value) and its message of the day and rules files, in the format PeerHosting reads
    ///     (RulesSnapshot): {"rules": {"Server.Key": "value"}, "motd", "text", "build", "hash"}
    /// </summary>
    internal static JObject RulesFromThisGame(string build)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in SettingsTable.HostRules().Where(e => e.Definition.Section == "Server")) {
            settings["Server." + entry.Definition.Key] = Text(entry);
        }

        var snapshot = new RulesSnapshot {
            Settings = new(settings),
            Motd = ServerInfo.ReadLocalFile("motd.txt"),
            RulesText = ServerInfo.ReadLocalFile("rules.txt"),
            AllowedBuild = build,
        };
        return new() {
            ["rules"] = new JObject(settings.Select(s => new JProperty(s.Key, s.Value))),
            ["motd"] = snapshot.Motd,
            ["text"] = snapshot.RulesText,
            ["build"] = build,
            ["hash"] = snapshot.Hash,
        };
    }

    private static string Text(ConfigEntryBase entry)
    {
        var value = entry.BoxedValue;
        return value is null ? "" : TomlTypeConverter.CanConvert(entry.SettingType)
            ? TomlTypeConverter.ConvertToString(value, entry.SettingType)
            : value.ToString();
    }

    /// <summary>
    ///     The setting keys whose value differs between two rules documents (a missing one counts), and the texts
    /// </summary>
    internal static List<string> RulesDiffer(JObject? signed, JObject mine)
    {
        var a = signed?["rules"] as JObject ?? new JObject();
        var b = mine["rules"] as JObject ?? new JObject();
        var keys = a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name)).OrderBy(k => k, StringComparer.Ordinal);
        var differ = keys.Where(k => (string?)a[k] != (string?)b[k]).Select(k => k.StartsWith("Server.") ? k.Substring(7) : k).ToList();
        foreach (var field in new[] { "motd", "text", "build" }) {
            if (((string?)signed?[field] ?? "") != ((string?)mine[field] ?? "")) {
                differ.Add(field);
            }
        }

        return differ;
    }

    /// <summary>
    ///     A list of Steam ids of a document ("admins", "host"...), as decimal texts
    /// </summary>
    internal static List<string> Ids(JObject? doc, string field)
    {
        return doc?[field] is JArray list ? [..list.Select(t => t.ToString().Trim()).Where(t => t.Length > 0).Distinct()] : [];
    }

    /// <summary>
    ///     A Steam id (17 digits as Steam shows them; any decimal id), a "steam:" id or a key of 64 hex
    /// </summary>
    internal static string? Target(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.StartsWith("steam:")) {
            text = text.Substring(6);
        }

        return Regex.IsMatch(text, "^[0-9a-f]{64}$") ? text : ulong.TryParse(text, out var id) && id != 0 ? id.ToString() : null;
    }

    // ------------------------------------------------------------------------------------- the work

    // what the requests need from the game, read on the main thread when a request starts (Unity's paths, Steam and
    // the session are not read from the worker thread); one request at a time
    private static string _keyPath = "";
    private static string _base = "";
    private static string _world = "public";
    private static ulong _steam;
    private static string _name = "";

    private static string World => _world;

    /// <summary>
    ///     One piece of work off the main thread, then everything read again; one at a time
    /// </summary>
    private static void Run(string? done, Action work)
    {
        if (Busy) {
            return;
        }

        Busy = true;
        Problem = "";
        try {
            _keyPath = KeyFile;
            _base = Address?.Base ?? "";
            _world = Address?.World ?? "public";
            _steam = MySteam;
            var name = UserData.Me.Name;
            _name = name is { Length: > 0 } ? name.Length > 32 ? name.Substring(0, 32) : name : "admin";
            EmpMod.Instance.StartCoroutine(Wait(done, work));
        } catch (Exception ex) {
            Busy = false;
            EmpLog.Information(ex, "World admin: the request could not start");
        }
    }

    private sealed class Read
    {
        internal List<VersionRow> Versions = [];
        internal long Head;
        internal string Keeper = "";
        internal List<HostBan> HostBans = [];
        internal Dictionary<long, FlagRow> Flags = [];
        internal readonly Dictionary<string, JObject?> Docs = [];
        internal readonly Dictionary<string, long> Seq = [];
    }

    private static IEnumerator Wait(string? done, Action work)
    {
        var address = Address;
        string? problem = null;
        Read? read = null;
        var task = System.Threading.Tasks.Task.Run(() => {
            if (address is null) {
                throw new Refused("emp_wa_err_address".lang());
            }

            try {
                work();
                problem = null;
            } catch (Refused refused) {
                problem = refused.Message;
            } catch (IOException ex) {
                problem = ex.Message;
            }

            // (read again even after a refusal: the page then shows what the service has now)
            read = Load();
        });
        while (!task.IsCompleted) {
            yield return null;
        }

        Busy = false;
        if (task.Exception?.GetBaseException() is { } failure) {
            Problem = failure is Refused or IOException ? failure.Message : "emp_wa_err_answer".lang();
            if (failure is not Refused) {
                EmpLog.Warning("World admin: {Why}", failure.Message);
            }
        } else {
            Problem = problem ?? "";
            if (problem is null && done is not null) {
                Done = done;
            }
        }

        if (read is not null) {
            Versions = read.Versions;
            Head = read.Head;
            Keeper = read.Keeper;
            HostBans = read.HostBans;
            Flags = read.Flags;
            foreach (var kind in DocKinds) {
                Docs[kind] = read.Docs.TryGetValue(kind, out var doc) ? doc : null;
                Seq[kind] = read.Seq.TryGetValue(kind, out var seq) ? seq : 0;
            }

            Loaded = true;
        }

        LayerElinTogether.Instance?.Reopen();
    }

    private static Read Load()
    {
        var read = new Read();
        var who = Expect(Send("GET", $"/v1/w/{World}/who", null), 200);
        // ("lease": null when nobody keeps it)
        read.Keeper = (string?)(who["lease"] as JObject)?["name"] ?? "";
        foreach (var kind in DocKinds) {
            read.Seq[kind] = (long?)who["docs"]?[kind] ?? 0;
        }

        var list = Expect(Send("GET", $"/v1/w/{World}/versions?limit=50", null), 200);
        read.Head = (long?)list["head"] ?? 0;
        foreach (var v in list["versions"] as JArray ?? []) {
            read.Versions.Add(new() {
                N = (long?)v["n"] ?? 0,
                Reason = (string?)v["reason"] ?? "",
                Time = (long?)v["time"] ?? 0,
                Key = (string?)v["key"] ?? "",
                Steam = (string?)v["steam"] ?? "",
                Name = (string?)v["name"] ?? "",
                Size = (long?)v["size"] ?? 0,
                Count = (int?)v["count"] ?? 0,
                GameDate = (string?)v["game_date"] ?? "",
                RollbackOf = v["rollback_of"] is { Type: JTokenType.Integer } of ? (long)of : null,
            });
        }

        // (a service from before the cross-checks answers 404: no flags shown)
        var flags = Send("GET", $"/v1/admin/flags?world={World}&limit=50", null);
        if (flags.Status == 200 && flags.Json?["flags"] is JArray flagged) {
            foreach (var f in flagged) {
                var items = f["items"] as JArray ?? [];
                read.Flags[(long?)f["n"] ?? 0] = new() {
                    Count = (int?)f["count"] ?? 0,
                    Reporters = (int?)f["reporters"] ?? 0,
                    Strong = (bool?)f["strong"] ?? false,
                    Kinds = string.Join(", ", items.Select(i => (string?)i["kind"] ?? "").Where(k => k.Length > 0).Distinct()),
                    Detail = items.FirstOrDefault() is { } first
                        ? $"{(string?)first["subject"]}: {(string?)first["before"]} -> {(string?)first["after"]}"
                        : "",
                };
            }
        }

        var bans = Expect(Send("GET", "/v1/admin/hostbans", null), 200);
        foreach (var b in bans["hostbans"] as JArray ?? []) {
            read.HostBans.Add(new() {
                Target = (string?)b["target"] ?? "",
                Time = (long?)b["time"] ?? 0,
                Reason = (string?)b["reason"] ?? "",
            });
        }

        foreach (var kind in DocKinds) {
            if (read.Seq[kind] == 0) {
                continue;
            }

            var reply = Send("GET", $"/v1/w/{World}/doc/{kind}", null);
            if (reply.Status == 404) {
                continue;
            }

            var envelope = Expect(reply, 200);
            // the exact body the admin signed: its "doc" is the document
            var body = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String((string?)envelope["body"] ?? "")));
            read.Docs[kind] = body["doc"] as JObject;
            read.Seq[kind] = (long?)envelope["seq"] ?? read.Seq[kind];
        }

        return read;
    }

    // ------------------------------------------------------------------------------------- the wire

    private static readonly object _gate = new();
    private static long _skew;

    private sealed class Refused(string why) : Exception(why);

    private static JObject Expect((int Status, JObject? Json) reply, int status)
    {
        if (reply.Status == status && reply.Json is not null) {
            return reply.Json;
        }

        var error = (string?)reply.Json?["error"] ?? "";
        throw new Refused(error switch {
            "not_admin" => "emp_wa_err_not_admin".lang(),
            "same_as_head" => "emp_wa_err_same".lang(),
            "no_version" => "emp_wa_err_no_version".lang(),
            "seq" => "emp_wa_err_seq".lang(),
            "rate_limited" => "emp_wa_err_busy".lang(),
            _ => "emp_wa_err_status".Loc(reply.Status, error),
        });
    }

    /// <summary>
    ///     One request signed with the admin key; the key is registered with this Steam id when the service does not
    ///     know it yet (the world's routes need it), and the request made again once with the service's clock
    /// </summary>
    private static (int Status, JObject? Json) Send(string method, string path, JObject? json)
    {
        lock (_gate) {
            var seed = LoadKey();
            var pub = Hex(Ed25519.PublicKey(seed));
            try {
                var body = json is null ? [] : Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
                for (var attempt = 0; ; attempt++) {
                    var reply = Once(seed, pub, method, path, body);
                    var error = reply.Status == 401 ? (string?)reply.Json?["error"] : null;
                    if (attempt < 2 && error == "clock" && (long?)reply.Json?["now"] is { } now) {
                        _skew = now - Now(0);
                        continue;
                    }

                    if (attempt < 2 && error == "unregistered") {
                        var me = new JObject { ["steam"] = _steam.ToString(), ["name"] = _name };
                        var registered = Once(seed, pub, "POST", "/v1/register", Encoding.UTF8.GetBytes(me.ToString(Formatting.None)));
                        if (registered.Status is not (200 or 201)) {
                            return registered;
                        }

                        continue;
                    }

                    return reply;
                }
            } finally {
                Array.Clear(seed, 0, seed.Length);
            }
        }
    }

    /// <summary>
    ///     The admin's seed, read each time and cleared after use (never kept, logged or written by the mod)
    /// </summary>
    private static byte[] LoadKey()
    {
        string text;
        try {
            text = File.ReadAllText(_keyPath).Trim();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new Refused("emp_wa_err_key".lang());
        }

        if (!Regex.IsMatch(text, "^[0-9a-fA-F]{64}$")) {
            throw new Refused("emp_wa_err_key".lang());
        }

        var seed = new byte[32];
        for (var i = 0; i < 32; i++) {
            seed[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
        }

        return seed;
    }

    private static long Now(long skew)
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + skew;
    }

    private static (int Status, JObject? Json) Once(byte[] seed, string pub, string method, string path, byte[] body)
    {
        if (_base.Length == 0) {
            throw new Refused("emp_wa_err_address".lang());
        }
        var time = Now(_skew).ToString();
        var nonce = Nonce();
        using var sha = SHA256.Create();
        var message = $"ETW1\n{method}\n{path}\n{Hex(sha.ComputeHash(body))}\n{time}\n{nonce}";
        var sig = Hex(Ed25519.Sign(seed, Encoding.UTF8.GetBytes(message)));
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#pragma warning disable SYSLIB0014 // HttpWebRequest is what was seen working from the game's Mono
            var request = (HttpWebRequest)WebRequest.Create(_base + path);
#pragma warning restore SYSLIB0014
            request.Method = method;
            request.UserAgent = "ElinTogether";
            request.Headers["X-ET-Key"] = pub;
            request.Headers["X-ET-Time"] = time;
            request.Headers["X-ET-Nonce"] = nonce;
            request.Headers["X-ET-Sig"] = sig;
            request.Timeout = request.ReadWriteTimeout = 30000;
            request.AllowAutoRedirect = false;
            if (method != "GET") {
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                using var to = request.GetRequestStream();
                to.Write(body, 0, body.Length);
            }

            HttpWebResponse response;
            try {
                response = (HttpWebResponse)request.GetResponse();
            } catch (WebException ex) when (ex.Response is HttpWebResponse refused) {
                response = refused;
            }

            using (response) {
                using var reader = new StreamReader(response.GetResponseStream()!, Encoding.UTF8);
                var text = reader.ReadToEnd();
                JObject? json = null;
                try {
                    json = text.Length > 0 ? JToken.Parse(text) as JObject : null;
                } catch (JsonException) {
                    // a page of a proxy, not the service: told by its status
                }

                return ((int)response.StatusCode, json);
            }
        } catch (WebException ex) {
            throw new IOException("emp_wa_err_offline".Loc(ex.Status));
        }
    }

    private static string Nonce()
    {
        var bytes = new byte[18];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }

    private static string Hex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
