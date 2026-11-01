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
///     {"admins": ["7656..."], "banned": ["..."], "rules": {"settings": {"Server.Teams": "true"}, "motd": "", "rules": "",
///     "build": ""}}. The file is read again when it changes on the disk
/// </summary>
internal sealed class LocalPeerHosting : IPeerHosting
{
    internal const string Argument = "-emppeerhosted";

    private static readonly bool _argument = Environment.GetCommandLineArgs().Contains(Argument);

    private static string FilePath => Path.Combine(Application.persistentDataPath, "ElinMP", "peer_hosting.json");

    private DateTime _readAt = DateTime.MinValue;
    private string[] _admins = [];
    private string[] _banned = [];
    private RulesSnapshot? _rules;

    public bool Active => _argument || EmpConfig.Dev.PeerHosted.Value;

    public string[] AdminIds {
        get {
            Read();
            return _admins;
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
            _banned = [];
            _rules = null;
            if (stamp == DateTime.MinValue) {
                EmpLog.Warning("Peer-hosted world: {File} is missing, no admins, no bans, no rules", FilePath);
                return;
            }

            var root = JObject.Parse(File.ReadAllText(FilePath));
            _admins = Ids(root["admins"]);
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
///     The peer-hosted mode as the rest of the mod asks about it
/// </summary>
internal static class PeerHosting
{
    /// <summary>
    ///     Replaced by the store client (P2/P3)
    /// </summary>
    internal static IPeerHosting Current { get; set; } = new LocalPeerHosting();

    internal static bool Active => Current.Active;

    internal static HashSet<ulong> AdminSet() => Parse(Current.AdminIds);

    internal static HashSet<ulong> BanSet() => Parse(Current.BannedIds);

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

        return session.Transport is ElinNetHost { IsZoneSession: false } ||
               session.Transport is ElinNetClient && Models.ModerationDelta.AdminIds.Contains(me);
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
