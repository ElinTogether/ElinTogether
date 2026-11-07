using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Net;
using HeathenEngineering.SteamworksIntegration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     What the world's online store says about a peer-hosted world (PLAN_peer_hosted_server, P4): whether this game
///     plays the peer-hosted public world, its admins and banned accounts, and its rules, all signed by the store so
///     that whoever keeps the world cannot change them. Steps P2/P3 (the store client and the host certificate)
///     replace <see cref="LocalPeerHosting" /> with the real one; nothing here depends on how they get it
/// </summary>
internal interface IPeerHosting
{
    /// <summary>
    ///     This game plays the peer-hosted world: hosting goes through the hidden server, the keeper is a plain guest
    /// </summary>
    bool Active { get; }

    /// <summary>
    ///     Steam ids (decimal) of the world's admins
    /// </summary>
    string[] AdminIds { get; }

    /// <summary>
    ///     Steam ids (decimal) of the world's moderators (kick and mute only, admin extras)
    /// </summary>
    string[] ModeratorIds { get; }

    /// <summary>
    ///     Steam ids (decimal) that may not join this world
    /// </summary>
    string[] BannedIds { get; }

    /// <summary>
    ///     The world's rules, null when none could be read: the world is then neither kept nor joined
    /// </summary>
    RulesSnapshot? SignedRules { get; }
}

/// <summary>
///     The rules of a peer-hosted world: host settings (only the Server section of the config, "Server.Key" -> the
///     value as the config file writes it), the message of the day and rules texts, the mod build a keeper must run
///     (empty: any). Applied by the keeper's hidden server (PeerRules.Apply), compared by every guest (PeerRules.Check)
/// </summary>
internal sealed class RulesSnapshot
{
    internal Dictionary<string, string> Settings { get; init; } = [];
    internal string Motd { get; init; } = "";
    internal string RulesText { get; init; } = "";
    internal string AllowedBuild { get; init; } = "";

    /// <summary>
    ///     The store's signature (P2/P3 check it; the local stub has none)
    /// </summary>
    internal string Signature { get; init; } = "";

    /// <summary>
    ///     SHA-256 of the whole snapshot in a fixed order, for the log and the reports
    /// </summary>
    internal string Hash => PeerRules.HashLines(Settings.OrderBy(s => s.Key, StringComparer.Ordinal)
        .Select(s => s.Key + "=" + s.Value)
        .Append("motd=" + Motd).Append("rules=" + RulesText).Append("build=" + AllowedBuild));
}

/// <summary>
///     Stand-in for the store until P2/P3: on with the hidden setting Dev.PeerHosted or the launch argument
///     -emppeerhosted, and the lists and rules read from ElinMP/peer_hosting.json, as
///     {"admins": ["7656..."], "moderators": ["..."], "banned": ["..."], "rules": {"settings": {"Server.Teams": "true"}, "motd": "", "rules": "",
///     "build": ""}}. The file is read again when it changes on the disk
/// </summary>
internal sealed class LocalPeerHosting : IPeerHosting
{
    internal const string Argument = "-emppeerhosted";

    private static readonly bool _argument = Environment.GetCommandLineArgs().Contains(Argument);

    /// <summary>
    ///     This game was started with -emppeerhosted (the hidden server of a peer-hosted world)
    /// </summary>
    internal static bool ArgumentGiven => _argument;

    private static string FilePath => Path.Combine(Application.persistentDataPath, "ElinMP", "peer_hosting.json");

    private DateTime _readAt = DateTime.MinValue;
    private string[] _admins = [];
    private string[] _moderators = [];
    private string[] _banned = [];
    private RulesSnapshot? _rules;

    public bool Active => _argument || EmpConfig.Dev.PeerHosted.Value || PeerHosting.PublicRoot is not null;

    public string[] AdminIds {
        get {
            Read();
            return _admins;
        }
    }

    public string[] ModeratorIds {
        get {
            Read();
            return _moderators;
        }
    }

    public string[] BannedIds {
        get {
            Read();
            return _banned;
        }
    }

    public RulesSnapshot? SignedRules {
        get {
            Read();
            return _rules;
        }
    }

    private void Read()
    {
        try {
            var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            if (stamp == _readAt) {
                return;
            }

            _readAt = stamp;
            _admins = [];
            _moderators = [];
            _banned = [];
            _rules = null;
            if (stamp == DateTime.MinValue) {
                EmpLog.Warning("Peer-hosted world: {File} is missing, no admins, no bans, no rules", FilePath);
                return;
            }

            var root = JObject.Parse(File.ReadAllText(FilePath));
            _admins = Ids(root["admins"]);
            _moderators = Ids(root["moderators"]);
            _banned = Ids(root["banned"]);
            if (root["rules"] is JObject rules) {
                _rules = new() {
                    Settings = (rules["settings"] as JObject)?.Properties().ToDictionary(p => p.Name, p => p.Value.ToString()) ?? [],
                    Motd = rules.Value<string>("motd") ?? "",
                    RulesText = rules.Value<string>("rules") ?? "",
                    AllowedBuild = rules.Value<string>("build") ?? "",
                    Signature = rules.Value<string>("signature") ?? "",
                };
            }

            EmpLog.Information("Peer-hosted world: {Admins} admins, {Banned} banned, rules {Rules}", _admins.Length, _banned.Length,
                _rules?.Hash ?? "none");
        } catch (Exception ex) {
            // (a broken file is no rules at all: nothing is kept or joined on a guess)
            EmpLog.Warning(ex, "Peer-hosted world: {File} could not be read", FilePath);
            _admins = [];
            _moderators = [];
            _banned = [];
            _rules = null;
        }
    }

    private static string[] Ids(JToken? token)
    {
        return token is JArray list ? [..list.Select(t => t.ToString().Trim()).Where(t => ulong.TryParse(t, out var id) && id != 0)] : [];
    }
}

/// <summary>
///     The lists and rules signed by the world's admins and kept by the ETWorld service (dev/etworld/API.md 5.1),
///     each checked against the admin keys built into the mod (SignedDocs), so neither the service nor the keeper can
///     change them. Read when the save depot is an "etworld:" service and the mod knows at least one admin key;
///     otherwise the local stand-in answers (no admin key is built in until the service is deployed). The documents
///     are fetched off the game's thread at the first use and every 5 minutes, kept on the disk
///     (ElinMP/etworld_docs_[world].json, checked again when read back); until one is held there are no rules, so the
///     world is neither kept nor joined
/// </summary>
internal sealed class StorePeerHosting : IPeerHosting
{
    private const double RefreshSeconds = 300;
    private const double RetrySeconds = 30;

    // the public halves of the world admins' keys, 64 hex each; the private seeds stay on the lead dev's PC
    // (ElinMP/etworld_admin.key and an offline copy), never in the repository or a release (made 2026-11-03)
    private static readonly string[] AdminKeysHex = ["250fe36322971ef5e094139f69a3a0ef359bd2d2b3c1e0533f45cc74b9382052"];

    private static string[]? _adminKeys;

    private readonly LocalPeerHosting _local = new();
    private readonly object _gate = new();
    private SignedDocs? _docs;
    private string _cacheFile = "";
    private double _nextRefresh;
    private bool _refreshing;
    private bool _triedOnce;
    private bool _toldLocal;

    /// <summary>
    ///     The admin keys signed documents are checked against (built in; Debug builds also read the bench's
    ///     ElinMP/etworld_admin_test.pub, one key per line)
    /// </summary>
    internal static IReadOnlyCollection<string> AdminKeys
    {
        get {
            if (_adminKeys is not null) {
                return _adminKeys;
            }

            var keys = new List<string>(AdminKeysHex.Select(k => k.ToLowerInvariant()));
#if DEBUG
            try {
                var path = Path.Combine(Application.persistentDataPath, "ElinMP", "etworld_admin_test.pub");
                if (File.Exists(path)) {
                    keys.AddRange(File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant())
                        .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^[0-9a-f]{64}$")));
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                EmpLog.Warning(ex, "The bench's world admin keys could not be read");
            }
#endif
            return _adminKeys = [..keys.Distinct()];
        }
    }

    public bool Active => _local.Active;

    public string[] AdminIds => Docs() is { } docs ? SignedDocs.SteamIds(docs.Get("admins")?.Doc, "admins") : _local.AdminIds;

    public string[] ModeratorIds => Docs() is { } docs ? SignedDocs.SteamIds(docs.Get("admins")?.Doc, "moderators") : _local.ModeratorIds;

    public string[] BannedIds => Docs() is { } docs ? SignedDocs.SteamIds(docs.Get("bans")?.Doc, "world") : _local.BannedIds;

    public RulesSnapshot? SignedRules
    {
        get {
            if (Docs() is not { } docs) {
                return _local.SignedRules;
            }

            if (docs.Get("rules") is not { } held) {
                return null;
            }

            // (read every tick by the keeper's hidden server: made once per document)
            if (_rulesOf != held) {
                var (settings, motd, text, build) = SignedDocs.Rules(held.Doc);
                _rules = new() {
                    Settings = settings,
                    Motd = motd,
                    RulesText = text,
                    AllowedBuild = build,
                    Signature = $"admin {held.Signer.Substring(0, 8)} #{held.Seq}",
                };
                _rulesOf = held;
            }

            return _rules;
        }
    }

    private SignedDocs.Held? _rulesOf;
    private RulesSnapshot? _rules;

    /// <summary>
    ///     The signed documents held (a copy safe to read), a refresh started when due; null when this game reads the
    ///     local stand-in instead (no store, or no admin key known)
    /// </summary>
    private SignedDocs? Docs()
    {
        if (SaveDepot.EtWorldService is not { } service || AdminKeys.Count == 0) {
            if (!_toldLocal && Active) {
                _toldLocal = true;
                EmpLog.Information("Peer-hosted world: {Why}, the lists and rules are read from ElinMP/peer_hosting.json",
                    AdminKeys.Count == 0 ? "no world admin key is built in" : "no world service is set");
            }

            return null;
        }

        var world = World(service.Address);
        SignedDocs docs;
        string cache;
        bool now;
        lock (_gate) {
            if (_docs is null || _docs.World != world) {
                _cacheFile = Path.Combine(Application.persistentDataPath, "ElinMP", $"etworld_docs_{world}.json");
                _docs = new(world);
                try {
                    if (File.Exists(_cacheFile)) {
                        foreach (var why in _docs.FromCache(File.ReadAllText(_cacheFile), AdminKeys)) {
                            EmpLog.Warning("Peer-hosted world: a kept signed document was refused, {Why}", why);
                        }
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    EmpLog.Warning(ex, "Peer-hosted world: the kept signed documents could not be read");
                }

                _nextRefresh = 0;
            }

            docs = _docs;
            cache = _cacheFile;
            if (_refreshing || Clock < _nextRefresh) {
                return docs;
            }

            _refreshing = true;
            // the hidden server holding nothing and never asked yet (a machine that never read them): asked now, on
            // this thread, so it does not refuse to open "no rules" while the answer travels; once per process at
            // most, bounded by the request's own time-out. Never in a visible game (no frozen screen)
            now = !_triedOnce && EmpServer.Requested && SignedDocs.Kinds.All(k => docs.Get(k) is null);
            _triedOnce = true;
        }

        var me = PeerKeeper.PlayerId;
        if (now) {
            Refresh(service, me, docs, cache);
        } else {
            System.Threading.Tasks.Task.Run(() => Refresh(service, me, docs, cache));
        }

        return docs;
    }

    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // seconds since the mod loaded, on any thread
    private static double Clock => _clock.Elapsed.TotalSeconds;

    private static string World(string address)
    {
        var at = address.IndexOf('#');
        return at >= 0 ? address.Substring(at + 1) : "public";
    }

    /// <summary>
    ///     Off the game's thread: each document fetched and offered (a refused one is logged, the one held stays)
    /// </summary>
    private void Refresh((string Address, string KeyFile) service, ulong me, SignedDocs docs, string cache)
    {
        var ok = true;
        try {
            var changed = false;
            foreach (var kind in SignedDocs.Kinds) {
                var reply = EtWorldDepot.Request(service.Address, service.KeyFile, me, Environment.UserName, "GET", "/v1/w/{w}/doc/" + kind);
                if (reply.Status == 404) {
                    continue;
                }

                if (reply.Status != 200 || reply.Json is null) {
                    ok = false;
                    EmpLog.Debug("Peer-hosted world: the signed {Kind} could not be read ({Status})", kind, reply.Status);
                    continue;
                }

                lock (_gate) {
                    var before = docs.Seq(kind);
                    if (docs.Offer(kind, reply.Json, AdminKeys) is { } why) {
                        EmpLog.Warning("Peer-hosted world: the service's signed {Kind} was refused, {Why}", kind, why);
                    } else if (docs.Seq(kind) != before) {
                        changed = true;
                        EmpLog.Information("Peer-hosted world: signed {Kind} #{Seq} taken", kind, docs.Seq(kind));
                    }
                }
            }

            if (changed) {
                string text;
                lock (_gate) {
                    text = docs.ToCache();
                }

                File.WriteAllText(cache + ".tmp", text);
                if (File.Exists(cache)) {
                    File.Delete(cache);
                }

                File.Move(cache + ".tmp", cache);
            }
        } catch (System.IO.IOException ex) {
            // the world service is down or unreachable: a plain line, the kept documents stay (no stack in Player.log)
            ok = false;
            EmpLog.Information("Peer-hosted world: the signed documents could not be read: {Why}", ex.Message);
        } catch (Exception ex) {
            ok = false;
            EmpLog.Information(ex, "Peer-hosted world: the signed documents could not be read");
        } finally {
            lock (_gate) {
                _refreshing = false;
                _nextRefresh = Clock + (ok ? RefreshSeconds : RetrySeconds);
            }
        }
    }
}

/// <summary>
///     The peer-hosted mode as the rest of the mod asks about it
/// </summary>
internal static class PeerHosting
{
    /// <summary>
    ///     The signed documents of the world service, or the local stand-in while no service or admin key is known
    /// </summary>
    internal static IPeerHosting Current { get; set; } = new StorePeerHosting();

    internal static bool Active => Current.Active;

    // the public world's ETWorld service, built in: empty until the worker is deployed (dev/etworld/API.md section 9);
    // then the public server card opens the peer-hosted world with no setting and no question (lead dev 2026-11-03)
    private const string PublicServiceAddress = "";

#if DEBUG
    private static string? _publicService;
    // (Environment.TickCount, not Unity's clock: the store's requests read it off the game's thread)
    private static int _publicReadAt;
#endif

    /// <summary>
    ///     The built-in public world service ("https://host"), empty when there is none (Debug builds also read the
    ///     bench's ElinMP/etworld_public_test.txt, one address, again every 2 s)
    /// </summary>
    internal static string PublicService
    {
        get {
#if DEBUG
            if (_publicService is null || unchecked(Environment.TickCount - _publicReadAt) > 2000) {
                _publicReadAt = Environment.TickCount;
                var address = PublicServiceAddress;
                try {
                    var path = Path.Combine(Application.persistentDataPath, "ElinMP", "etworld_public_test.txt");
                    if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } test) {
                        address = test;
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    EmpLog.Warning(ex, "The bench's public world service could not be read");
                }

                _publicService = address;
            }

            return _publicService;
#else
            return PublicServiceAddress;
#endif
        }
    }

    /// <summary>
    ///     The player opened the public world from its card in this run; cleared when this game starts anything else
    ///     (another save, another game's lobby, a hidden server on another save)
    /// </summary>
    internal static bool PublicEntered { get; set; }

    /// <summary>
    ///     The card opens the public world through the built-in service: there is one, and the depot setting does not
    ///     name another world service (a group's own etworld: depot comes first)
    /// </summary>
    internal static bool PublicAvailable => PublicService.Length > 0 && !SaveDepot.EtWorldConfigured;

    /// <summary>
    ///     "etworld:" + the built-in service while this game plays the public world (the card was used, or this is
    ///     the hidden server started for it with -emppeerhosted), null otherwise: then nothing of the peer mode runs
    /// </summary>
    internal static string? PublicRoot => PublicAvailable && (PublicEntered || LocalPeerHosting.ArgumentGiven)
        ? "etworld:" + PublicService
        : null;

    /// <summary>
    ///     A scene came up: a game played on another save than the peer world's, as its host or alone, or as a guest of
    ///     a lobby that does not even claim to be the public server (a Steam invite to a friend's game), ends the public
    ///     world mode of this run. The keeper's own game (its hidden server, no lobby) and a keeper's lobby keep it;
    ///     the other joins of the Lobby tab end it at the click (TabPlay)
    /// </summary>
    [ElinPostSceneInit]
    private static void LeavePublicOnOtherGame(Scene.Mode mode)
    {
        if (!PublicEntered || mode != Scene.Mode.StartGame || !EClass.core.IsGameStarted) {
            return;
        }

        var session = NetSession.Instance;
        var other = session.Transport is ElinNetClient
            ? session.Lobby.Current is { IsValid: true } lobby && !Net.Steam.OfficialServer.Claims(lobby)
            : Game.id != PeerKeeper.SaveId;
        if (other) {
            PublicEntered = false;
            EmpLog.Information("Public world mode ended: this game plays another world");
        }
    }

    internal static HashSet<ulong> AdminSet() => Parse(Current.AdminIds);

    internal static HashSet<ulong> BanSet() => Parse(Current.BannedIds);

    /// <summary>
    ///     The signed moderators, admins left out (an admin needs no moderator role)
    /// </summary>
    internal static HashSet<ulong> ModeratorSet()
    {
        var moderators = Parse(Current.ModeratorIds);
        moderators.ExceptWith(AdminSet());
        return moderators;
    }

    internal static bool IsSignedAdmin(ulong user) => AdminSet().Contains(user);

    internal static bool IsBanned(ulong user) => BanSet().Contains(user);

    /// <summary>
    ///     This game may use the admin tools of the session it plays in: the host of an ordinary session, or an admin
    ///     its host told; in a peer-hosted world only an admin of the signed list, whoever keeps the world (the
    ///     keeper's own game is a guest of its hidden server: never "the host")
    /// </summary>
    internal static bool IAmAdmin()
    {
        var session = NetSession.Instance;
        ulong me = session.Self?.User ?? UserData.Me;
        if (Active) {
            return session.Transport is ElinNetClient && IsSignedAdmin(me) && Models.ModerationDelta.AdminIds.Contains(me);
        }

        return session.Transport is ElinNetHost || Models.ModerationDelta.AdminIds.Contains(me);
    }

    private static HashSet<ulong> Parse(string[]? ids)
    {
        var set = new HashSet<ulong>();
        foreach (var id in ids ?? []) {
            if (ulong.TryParse(id, out var user) && user != 0) {
                set.Add(user);
            }
        }

        return set;
    }
}
