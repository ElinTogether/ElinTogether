using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Configuration;
using ElinTogether.Components;
using ElinTogether.Net;

namespace ElinTogether.Helper;

/// <summary>
///     The signed rules of a peer-hosted world (PeerHosting): the keeper's hidden server plays them whatever its own
///     config file says (Apply at the start, Enforce every tick), and every guest compares the rules it is told with
///     them (Check): a keeper whose rules differ is left at once, with the keys that differ in the log
/// </summary>
internal static class PeerRules
{
    /// <summary>
    ///     The config entry a snapshot key names ("Server.Key"); only the Server section can be pinned
    /// </summary>
    internal static ConfigEntryBase? Entry(string key)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0 || key.Substring(0, dot) != "Server") {
            return null;
        }

        var config = EmpMod.Instance.Config;
        var definition = new ConfigDefinition("Server", key.Substring(dot + 1));
        return config.ContainsKey(definition) ? config[definition] : null;
    }

    private static object? Parse(ConfigEntryBase entry, string value)
    {
        try {
            return TomlTypeConverter.ConvertToValue(value, entry.SettingType);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Peer-hosted world: the signed value {Value} of {Key} cannot be read", value, entry.Definition.Key);
            return null;
        }
    }

    private static string Canon(object? value)
    {
        return value is null ? "" : TomlTypeConverter.CanConvert(value.GetType())
            ? TomlTypeConverter.ConvertToString(value, value.GetType())
            : value.ToString();
    }

    /// <summary>
    ///     The snapshot's settings as config entries and values (unknown keys and unreadable values left out, logged)
    /// </summary>
    private static Dictionary<ConfigEntryBase, object> Values(RulesSnapshot rules)
    {
        var values = new Dictionary<ConfigEntryBase, object>();
        foreach (var setting in rules.Settings) {
            var key = setting.Key;
            var raw = setting.Value;
            if (Entry(key) is not { } entry) {
                EmpLog.Warning("Peer-hosted world: the signed rules name {Key}, which is no host setting of this version", key);
                continue;
            }

            if (Parse(entry, raw) is { } value) {
                values[entry] = value;
            }
        }

        return values;
    }

    /// <summary>
    ///     On the keeper's hidden server: the signed values over the config, in memory only (the keeper's own file is
    ///     not written: its friends' games keep their settings). The count of values that were different
    /// </summary>
    internal static int Apply()
    {
        if (!EmpServer.Requested || !PeerHosting.Active || PeerHosting.Current.SignedRules is not { } rules) {
            return 0;
        }

        var diff = Values(rules).Where(v => !Equals(v.Key.BoxedValue, v.Value)).ToArray();
        if (diff.Length == 0) {
            return 0;
        }

        var config = EmpMod.Instance.Config;
        var save = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;
        try {
            foreach (var change in diff) {
                change.Key.BoxedValue = change.Value;
            }
        } finally {
            config.SaveOnConfigSet = save;
        }

        if (diff.Any(c => c.Key == EmpConfig.Server.SourceValidationSet)) {
            NetSession.Instance.Transport?.CreateValidation();
        }

        EmpLog.Information("Peer-hosted world: the signed rules {Hash} were written over this game's config ({Keys})", rules.Hash,
            string.Join(", ", diff.Select(d => d.Key.Definition.Key + "=" + Canon(d.Value))));
        return diff.Length;
    }

    /// <summary>
    ///     On the keeper's hidden server, every tick: a value changed since (the config file edited and reloaded, a
    ///     console) is put back, and the players are told the rules again
    /// </summary>
    internal static void Enforce(ElinNetHost host)
    {
        if (Apply() > 0) {
            ElinNetHost.AdminLog("peer-hosted: a host setting differed from the signed rules, put back");
            host.UpdateRemoteSessionRules();
        }
    }

    /// <summary>
    ///     The motd or rules text of the signed rules, null when this is not a peer-hosted world
    /// </summary>
    internal static string? Text(string file)
    {
        if (!PeerHosting.Active || PeerHosting.Current.SignedRules is not { } rules) {
            return null;
        }

        return file switch {
            "motd.txt" => Normalize(rules.Motd),
            "rules.txt" => Normalize(rules.RulesText),
            _ => null,
        };
    }

    private static string Normalize(string text)
    {
        text = text.Replace("\r", "").Trim();
        return text.Length > ServerInfo.MaxLength ? text.Substring(0, ServerInfo.MaxLength) : text;
    }

    /// <summary>
    ///     What a guest is told of a host setting, as the host makes it from its config (NetSessionRules.Default):
    ///     a few are made from two settings or bounded. Null when the snapshot does not say enough to know it
    /// </summary>
    private static object? Told(ConfigEntryBase entry, IReadOnlyDictionary<ConfigEntryBase, object> signed)
    {
        if (!signed.TryGetValue(entry, out var value)) {
            return null;
        }

        if (entry == EmpConfig.Server.WorldCopy) {
            return signed.TryGetValue(EmpConfig.Server.Takeover, out var takeover) ? (bool)value || (bool)takeover : null;
        }

        if (entry == EmpConfig.Server.TeamBoxes) {
            return signed.TryGetValue(EmpConfig.Server.Teams, out var teams) ? (bool)value && (bool)teams : null;
        }

        if (entry == EmpConfig.Server.CatchUpDays) {
            return Math.Max(1, (int)value);
        }

        if (entry == EmpConfig.Server.TeamMaxSize || entry == EmpConfig.Server.IdleMinutes || entry == EmpConfig.Server.LivingWorldMinutes) {
            return Math.Max(0, (int)value);
        }

        return value;
    }

    /// <summary>
    ///     On a guest of a peer-hosted world: null when the rules this keeper sent are the signed ones, else why not.
    ///     Compared: every signed setting the rules tell (what a guest is not told cannot be checked from here), the
    ///     message of the day and the rules text, as two hashes
    /// </summary>
    internal static string? Check(NetSessionRules received)
    {
        if (PeerHosting.Current.SignedRules is not { } rules) {
            return "no signed rules to compare with";
        }

        var signed = Values(rules);
        var told = SettingsTable.FromRules(received);
        var expected = new List<string>();
        var got = new List<string>();
        var differ = new List<string>();
        foreach (var entry in signed.Keys.OrderBy(e => e.Definition.Key, StringComparer.Ordinal)) {
            var want = Told(entry, signed);
            var have = told.Raw(entry);
            if (want is null || have is null) {
                continue;
            }

            var key = entry.Definition.Key;
            expected.Add(key + "=" + Canon(want));
            got.Add(key + "=" + Canon(have));
            if (Canon(want) != Canon(have)) {
                differ.Add($"{key} {Canon(have)} (signed {Canon(want)})");
            }
        }

        expected.Add("motd=" + Normalize(rules.Motd));
        expected.Add("rules=" + Normalize(rules.RulesText));
        got.Add("motd=" + Normalize(received.Motd ?? ""));
        got.Add("rules=" + Normalize(received.RulesText ?? ""));
        if (Normalize(received.Motd ?? "") != Normalize(rules.Motd)) {
            differ.Add("motd");
        }

        if (Normalize(received.RulesText ?? "") != Normalize(rules.RulesText)) {
            differ.Add("rules text");
        }

        var want2 = HashLines(expected);
        var have2 = HashLines(got);
        if (want2 == have2) {
            EmpLog.Debug("Peer-hosted world: the keeper's rules match the signed ones ({Hash}, {Count} settings)", have2, expected.Count - 2);
            return null;
        }

        EmpLog.Warning("Peer-hosted world: the keeper's rules {Have} are not the signed {Want}: {Differ}", have2, want2,
            string.Join(", ", differ));
        return string.Join(", ", differ.Take(4)) + (differ.Count > 4 ? " …" : "");
    }

    /// <summary>
    ///     On a guest of a peer-hosted world: null when the keeper runs the build the signed rules allow (or they
    ///     allow any), else why not
    /// </summary>
    internal static string? CheckBuild(string hostModVersion)
    {
        if (PeerHosting.Current.SignedRules is not { } rules) {
            return "no signed rules to compare with";
        }

        return rules.AllowedBuild.Length == 0 || rules.AllowedBuild == hostModVersion
            ? null
            : $"mod {hostModVersion} (signed {rules.AllowedBuild})";
    }

    internal static string HashLines(IEnumerable<string> lines)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}
