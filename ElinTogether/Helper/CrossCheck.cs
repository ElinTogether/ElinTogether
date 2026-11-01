using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ElinTogether.Models;
using ElinTogether.Net;
using LZ4;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Tasks = System.Threading.Tasks;

namespace ElinTogether.Helper;

/// <summary>
///     Guest cross-checks of a peer-hosted world (PLAN_peer_hosted_server 4.5, 4.6, step P7). The keeper's visible game
///     is a plain guest, but its hidden server is the authority and could be modified. Every guest watches what it
///     can see cheaply and reports what no honest server makes, as a signed "flag" on the world's current version at
///     the ETWorld service; the admin page marks flagged versions (two independent guests: red). Nothing is ever
///     punished or blocked here: the admins decide (rollback, ban from keeping).<br />
///     What is watched:<br />
///     - each whole world copy the keeper sends after a save (WorldCopyReceiver): the player characters' gold, gold
///     bars and platinum, level, rare items (mythical and artifact), artifacts held twice, items and characters that
///     vanished, compared with the copy before (off the game's thread, only when game.txt changed);<br />
///     - the land book (Teams.Learn): a land moved from a team that still exists to another, many lands cleared at once;<br />
///     - the admin list the keeper tells (ModerationDelta): ids that the signed admin list does not have;<br />
///     - the rules and build the keeper tells (PeerRules.Check, CheckBuild): already a reason to leave, reported too.<br />
///     Evidence (what, who kept the world, its handover, before/after) is written to ElinMP/crosscheck_evidence.jsonl.
///     Only on a guest of a peer-hosted world; nothing changes anywhere else
/// </summary>
internal static class CrossCheck
{
    // ------------------------------------------------------------------------------------------- thresholds

    // the gain cap per player and hour on a server that enforces trust (CardModCurrencyDelta.HourlyGain): a player
    // online may gain OnlineCapFactor times it per hour between two copies, one offline OfflineCapFactor times it
    private static readonly (string Id, long HourlyCap)[] Currencies = [("money", 2_000_000), ("money2", 2_000), ("plat", 2_000)];
    private const double OnlineCapFactor = 5.0;
    private const double OfflineCapFactor = 0.5;

    // levels in one interval: online at least this many (from level MinLevelForJump), offline at least OfflineLevels
    private const int OnlineLevels = 15;
    private const int MinLevelForJump = 10;
    private const int OfflineLevels = 3;

    // new mythical/artifact items for one character in one interval
    private const int OnlineRare = 5;
    private const int OfflineRare = 2;

    // an offline character that had at least WipeMinThings items and kept less than half of them
    private const int WipeMinThings = 20;

    // lands of other owners cleared in one book update
    private const int MassLandLoss = 5;

    // two copies further apart than this are not compared (the guest was away, the world was reloaded)
    private const double MaxIntervalHours = 0.75;

    // a game.txt bigger than this is not read
    private const long MaxGameText = 256L * 1024 * 1024;

    // the same thing is reported once per hour; at most ReportsPerHour reports per hour from this game
    private const float RepeatSeconds = 3600f;
    private const int ReportsPerHour = 6;

    private const float SampleSeconds = 20f;
    private const long MaxEvidenceBytes = 1024 * 1024;

    /// <summary>
    ///     This game is a guest of a peer-hosted world (the keeper's own visible game too: a plain guest)
    /// </summary>
    internal static bool Active => PeerHosting.Active && !EmpServer.Requested && NetSession.Instance.Transport is ElinNetClient;

    // ------------------------------------------------------------------------------------------- presence

    // player characters this game knows of, and those seen online since the last copy compared
    private static readonly HashSet<int> _players = [];
    private static readonly HashSet<int> _seenOnline = [];
    private static float _nextSample;
    private static object? _session;

    // what the keeper's last copy said
    private static ulong _keeper;
    private static int _handover;
    private static long _saved;

    /// <summary>
    ///     Every frame of the visible game (cheap: a sample every 20 s)
    /// </summary>
    internal static void Update()
    {
        while (_answered.TryDequeue(out var then)) {
            try {
                then();
            } catch (Exception ex) {
                EmpLog.Debug(ex, "Cross-check: an answer could not be used");
            }
        }

        if (!Active || Time.unscaledTime < _nextSample || !EClass.core.IsGameStarted) {
            return;
        }

        _nextSample = Time.unscaledTime + SampleSeconds;
        var transport = NetSession.Instance.Transport;
        if (!ReferenceEquals(transport, _session)) {
            // a new link: what was seen before says nothing about who was online meanwhile
            _session = transport;
            _seenOnline.Clear();
            _previous = null;
        }

        foreach (var uid in OnlineNow()) {
            _seenOnline.Add(uid);
            _players.Add(uid);
        }

        foreach (var team in Teams.Ids()) {
            foreach (var uid in Teams.MembersOf(team)) {
                _players.Add(uid);
            }
        }
    }

    private static IEnumerable<int> OnlineNow()
    {
        var session = NetSession.Instance;
        foreach (var p in session.ServerRoster) {
            yield return p.CharaUid;
        }

        foreach (var p in session.VisiblePlayers) {
            yield return p.CharaUid;
        }

        if (EClass.core.IsGameStarted && EClass.pc is { } pc) {
            yield return pc.uid;
        }
    }

    // ------------------------------------------------------------------------------------------- the world copy

    internal sealed class Facts
    {
        internal readonly Dictionary<string, long> Money = [];
        internal int Level;
        internal int Rare;
        internal int Things;
        internal readonly List<string> Artifacts = [];
    }

    internal sealed class Snapshot
    {
        internal readonly Dictionary<int, Facts> Charas = [];
        internal long Saved;
        internal int Handover;
        internal string World = "";
        internal string GameHash = "";
    }

    private static Snapshot? _previous;
    private static bool _reading;

    /// <summary>
    ///     A whole copy of the keeper's save was kept in dir (WorldCopyReceiver, on the game's thread)
    /// </summary>
    internal static void CopyKept(string dir, WorldCopyManifest manifest)
    {
        try {
            if (!Active) {
                return;
            }

            _keeper = manifest.Keeper;
            _handover = manifest.Handover;
            _saved = manifest.Saved;

            var game = manifest.Files.FirstOrDefault(f => f.Path == "game.txt");
            if (game is null || _reading) {
                return;
            }

            if (_previous is { } before && before.GameHash == game.Hash && before.World == manifest.World) {
                return;
            }

            // what the main thread knows, read now: the player characters and who was online since the last copy
            var players = new HashSet<int>(_players.Where(uid => uid != 0 && !ServerAvatar.IsUid(uid)));
            var online = new HashSet<int>(_seenOnline);
            online.UnionWith(OnlineNow());
            _seenOnline.Clear();
            _seenOnline.UnionWith(OnlineNow());
            if (players.Count == 0) {
                return;
            }

            var path = WorldCopyStore.ToLocal(dir, game.Path);
            var previous = _previous;
            var (world, saved, handover, hash, keeper) = (manifest.World, manifest.Saved, manifest.Handover, game.Hash, manifest.Keeper);
            _reading = true;
            Tasks.Task.Run(() => {
                Snapshot? now = null;
                List<Finding> found = [];
                try {
                    now = Read(path, players);
                    now.World = world;
                    now.Saved = saved;
                    now.Handover = handover;
                    now.GameHash = hash;
                    if (previous is not null && previous.World == world && handover >= previous.Handover) {
                        var hours = (saved - previous.Saved) / (double)TimeSpan.TicksPerHour;
                        if (hours > 0 && hours <= MaxIntervalHours) {
                            found = Compare(previous, now, online, hours);
                        }
                    }
                } catch (Exception ex) {
                    EmpLog.Debug(ex, "Cross-check: the world copy could not be read");
                }

                _answered.Enqueue(() => {
                    _reading = false;
                    if (now is null) {
                        return;
                    }

                    _previous = now;
                    EmpLog.Debug("Cross-check: copy of handover {Handover} read, {Charas} player characters, {Found} findings",
                        handover, now.Charas.Count, found.Count);
                    foreach (var f in found) {
                        Report(f.Kind, f.Subject, f.Before, f.After, f.Detail, keeper, handover);
                    }
                });
            });
        } catch (Exception ex) {
            _reading = false;
            EmpLog.Debug(ex, "Cross-check: the copy could not be checked");
        }
    }

    /// <summary>
    ///     The facts of the player characters in a save's game.txt: only cards.globalCharas is loaded, the rest of the
    ///     file is skipped as it streams by. Not on the game's thread
    /// </summary>
    internal static Snapshot Read(string path, HashSet<int> players)
    {
        var snapshot = new Snapshot();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxGameText) {
            throw new InvalidDataException("game.txt too big to check: " + file.Length);
        }

        var compressed = StartsCompressed(file);
        file.Position = 0;
        using Stream text = compressed
            ? new LZ4Bytes.CappedStream(new LZ4Stream(file, CompressionMode.Decompress), Patches.PeerMapPatch.MaxText)
            : file;
        using var reader = new JsonTextReader(new StreamReader(text, Encoding.UTF8)) { DateParseHandling = DateParseHandling.None };
        JObject? cards = null;
        if (reader.Read() && reader.TokenType == JsonToken.StartObject) {
            while (reader.Read() && reader.TokenType == JsonToken.PropertyName) {
                var name = (string)reader.Value!;
                reader.Read();
                if (name == "cards" && reader.TokenType == JsonToken.StartObject) {
                    cards = JObject.Load(reader);
                    break;
                }

                reader.Skip();
            }
        }

        if (cards?["globalCharas"] is not JObject charas) {
            throw new InvalidDataException("no cards.globalCharas in game.txt");
        }

        foreach (var property in charas.Properties()) {
            if (!int.TryParse(property.Name, out var uid) || !players.Contains(uid) || property.Value is not JObject chara ||
                chara["A"] is not JArray ints) {
                // (a "$ref" to a character written earlier in the file is not followed: that one is not compared)
                continue;
            }

            var facts = new Facts { Level = Int(ints, 25) };
            foreach (var (id, _) in Currencies) {
                facts.Money[id] = 0;
            }

            Walk(Things(chara), facts, 0);
            snapshot.Charas[uid] = facts;
        }

        return snapshot;
    }

    private static bool StartsCompressed(Stream file)
    {
        int b;
        while ((b = file.ReadByte()) != -1) {
            if (b is 9 or 10 or 13 or 32) {
                continue;
            }

            return !(b is 34 or 45 or (>= 48 and <= 57) or 91 or 102 or 110 or 116 or 123);
        }

        return false;
    }

    private static int Int(JArray ints, int at)
    {
        return at < ints.Count && ints[at].Type == JTokenType.Integer ? (int)ints[at] : 0;
    }

    // a card's things: an array, or {"$values": [...]} when its type was written
    private static JArray? Things(JObject card)
    {
        return card["C"] switch {
            JArray list => list,
            JObject { } wrapped => wrapped["$values"] as JArray,
            _ => null,
        };
    }

    private static void Walk(JArray? things, Facts facts, int depth)
    {
        if (things is null || depth > 8) {
            return;
        }

        foreach (var token in things) {
            if (token is not JObject thing || thing["A"] is not JArray ints) {
                continue;
            }

            facts.Things++;
            var id = (string?)thing["B"] ?? "";
            var num = Math.Max(1, Int(ints, 6));
            if (facts.Money.ContainsKey(id)) {
                facts.Money[id] += num;
            }

            // Card.rarity is _ints[19] / 100: Mythical 3, Artifact 4
            var rarity = Int(ints, 19) / 100;
            if (rarity >= 3) {
                facts.Rare++;
            }

            if (rarity >= 4 && id.Length > 0) {
                facts.Artifacts.Add(id);
            }

            Walk(Things(thing), facts, depth + 1);
        }
    }

    internal sealed record Finding(string Kind, string Subject, string Before, string After, string Detail);

    /// <summary>
    ///     What changed between two copies that no honest server makes (thresholds above). Not on the game's thread
    /// </summary>
    internal static List<Finding> Compare(Snapshot before, Snapshot now, HashSet<int> online, double hours)
    {
        var found = new List<Finding>();
        var span = Math.Max(1.0, Math.Ceiling(hours));
        var minutes = $"{hours * 60:0} min";

        foreach (var (id, cap) in Currencies) {
            // gold that left other player characters may have reached this one (trade, mail, a bank deposit)
            long pool = 0;
            foreach (var (uid, was) in before.Charas) {
                if (now.Charas.TryGetValue(uid, out var isNow)) {
                    pool += Math.Max(0, was.Money[id] - isNow.Money[id]);
                }
            }

            foreach (var (uid, was) in before.Charas) {
                if (!now.Charas.TryGetValue(uid, out var isNow)) {
                    continue;
                }

                var gain = isNow.Money[id] - was.Money[id];
                var limit = (long)(cap * (online.Contains(uid) ? OnlineCapFactor : OfflineCapFactor) * span);
                if (gain - pool > limit) {
                    found.Add(new("gold", $"chara {uid} {id}", was.Money[id].ToString(), isNow.Money[id].ToString(),
                        $"+{gain} in {minutes}, {(online.Contains(uid) ? "online" : "offline")}, limit {limit}, others lost {pool}"));
                }
            }
        }

        foreach (var (uid, was) in before.Charas) {
            var on = online.Contains(uid);
            if (!now.Charas.TryGetValue(uid, out var isNow)) {
                if (!on) {
                    found.Add(new("wipe", $"chara {uid}", $"{was.Things} things", "gone", "an offline player's character vanished"));
                }

                continue;
            }

            var levels = isNow.Level - was.Level;
            if (on ? levels >= OnlineLevels && was.Level >= MinLevelForJump : levels >= OfflineLevels) {
                found.Add(new("level", $"chara {uid}", was.Level.ToString(), isNow.Level.ToString(),
                    $"+{levels} levels in {minutes}, {(on ? "online" : "offline")}"));
            }

            var rare = isNow.Rare - was.Rare;
            if (rare >= (on ? OnlineRare : OfflineRare)) {
                found.Add(new("item", $"chara {uid}", $"{was.Rare} rare", $"{isNow.Rare} rare",
                    $"+{rare} mythical/artifact items in {minutes}, {(on ? "online" : "offline")}"));
            }

            if (!on && was.Things >= WipeMinThings && isNow.Things * 2 < was.Things) {
                found.Add(new("wipe", $"chara {uid}", $"{was.Things} things", $"{isNow.Things} things",
                    "an offline player lost more than half of its items"));
            }
        }

        var gone = before.Charas.Keys.Count(uid => !now.Charas.ContainsKey(uid) && online.Contains(uid));
        if (gone >= 2) {
            found.Add(new("wipe", "player characters", before.Charas.Count.ToString(), now.Charas.Count.ToString(),
                $"{gone} online players' characters vanished in {minutes}"));
        }

        // an artifact is unique: held more often than before by the player characters
        var had = Count(before);
        foreach (var (id, n) in Count(now)) {
            if (n >= 2 && n > (had.TryGetValue(id, out var m) ? m : 0)) {
                found.Add(new("dupe", "artifact " + id, (had.TryGetValue(id, out var k) ? k : 0).ToString(), n.ToString(),
                    "an artifact held more than once by the player characters"));
            }
        }

        return found;
    }

    private static Dictionary<string, int> Count(Snapshot snapshot)
    {
        var counts = new Dictionary<string, int>();
        foreach (var id in snapshot.Charas.Values.SelectMany(f => f.Artifacts)) {
            counts[id] = (counts.TryGetValue(id, out var n) ? n : 0) + 1;
        }

        return counts;
    }

    // ------------------------------------------------------------------------------------------- live checks

    /// <summary>
    ///     A guest is told the land book again (Teams.Learn, before it replaces the one it held): lands taken from an
    ///     owner that still exists, or many lands cleared at once
    /// </summary>
    internal static void Lands(IReadOnlyDictionary<int, int> oldZones, TeamsDelta told)
    {
        try {
            if (!Active || oldZones.Count == 0) {
                return;
            }

            bool Exists(int owner) => owner < 0 || told.TeamList.TryGetValue(owner, out var team) && team.Length > 2;
            int TeamOf(int chara) => told.TeamList.FirstOrDefault(t => t.Value.Skip(2).Contains(chara)).Key;

            var cleared = 0;
            foreach (var (zone, was) in oldZones) {
                if (was == 0) {
                    continue;
                }

                var now = told.Zones.TryGetValue(zone, out var owner) ? owner : 0;
                if (now == was) {
                    continue;
                }

                if (now == 0) {
                    if (Exists(was)) {
                        cleared++;
                    }

                    continue;
                }

                // a player alone joined a team (its lands follow it), or a team that is gone handed its lands on
                var legit = was < 0 ? TeamOf(-was) == now : !Exists(was);
                if (!legit) {
                    Report("land", $"land {zone}", $"owner {was}", $"owner {now}", "a land moved from an owner that still exists");
                }
            }

            if (cleared >= MassLandLoss) {
                Report("wipe", "lands", $"{oldZones.Count(z => z.Value != 0)} owned", $"{told.Zones.Count(z => z.Value != 0)} owned",
                    $"{cleared} lands of existing owners cleared at once");
            }
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Cross-check: the land book could not be compared");
        }
    }

    /// <summary>
    ///     The keeper told the admins (ModerationDelta): ids the signed list does not have
    /// </summary>
    internal static void Admins(IEnumerable<ulong> told)
    {
        try {
            if (!Active) {
                return;
            }

            var signed = PeerHosting.AdminSet();
            var extra = told.Where(id => !signed.Contains(id)).OrderBy(id => id).ToArray();
            if (extra.Length > 0) {
                Report("admin", "admins", $"{signed.Count} signed", string.Join(",", extra.Take(3)),
                    $"{extra.Length} admin(s) the signed list does not have");
            }
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Cross-check: the admin list could not be compared");
        }
    }

    /// <summary>
    ///     The keeper's rules or build are not the signed ones (the guest leaves it; reported first)
    /// </summary>
    internal static void Rules(string what, string differ)
    {
        if (Active) {
            Report("rules", what, "signed " + (PeerHosting.Current.SignedRules?.Hash ?? "none"), differ, "the keeper's " + what + " differ from the signed snapshot");
        }
    }

    // ------------------------------------------------------------------------------------------- reports

    private static readonly ConcurrentQueue<Action> _answered = new();
    private static readonly Dictionary<string, float> _reported = [];
    private static readonly List<float> _sent = [];
    private static Tasks.Task _sending = Tasks.Task.CompletedTask;

    private static string EvidenceFile => Path.Combine(Application.persistentDataPath, "ElinMP",
#if DEBUG
        EmpConfig.Dev.Identity.Value > 0 ? $"crosscheck_evidence_{EmpConfig.Dev.Identity.Value}.jsonl" :
#endif
        "crosscheck_evidence.jsonl");

    /// <summary>
    ///     Something no honest keeper does: kept as evidence, and flagged on the world's current version (once an hour
    ///     for the same thing, a few an hour in all). Never blocks or punishes anything
    /// </summary>
    internal static void Report(string kind, string subject, string before, string after, string detail, ulong keeper = 0, int handover = -1)
    {
        keeper = keeper != 0 ? keeper : _keeper;
        handover = handover >= 0 ? handover : _handover;
        var now = Time.realtimeSinceStartup;
        var key = kind + "|" + subject;
        if (_reported.TryGetValue(key, out var at) && now - at < RepeatSeconds) {
            return;
        }

        _sent.RemoveAll(t => now - t > 3600f);
        if (_sent.Count >= ReportsPerHour) {
            EmpLog.Debug("Cross-check: {Kind} {Subject} not reported, {Count} reports this hour already", kind, subject, _sent.Count);
            return;
        }

        _reported[key] = now;
        _sent.Add(now);

        var evidence = new JObject {
            ["time"] = DateTime.UtcNow.ToString("o"),
            ["kind"] = kind,
            ["subject"] = subject,
            ["before"] = before,
            ["after"] = after,
            ["detail"] = detail,
            ["keeper"] = keeper.ToString(),
            ["handover"] = handover,
            ["copy_saved"] = _saved > 0 ? new DateTime(_saved, DateTimeKind.Utc).ToString("o") : "",
            ["world"] = EtWorldDepot.WorldName,
        };
        EmpLog.Warning("Cross-check: the keeper {Keeper} (handover {Handover}) made an impossible change: {Kind} {Subject} {Before} -> {After} ({Detail})",
            keeper, handover, kind, subject, before, after, detail);
        Keep(evidence);

        if (SaveDepot.EtWorldService is not { } service) {
            return;
        }

        var body = new JObject {
            ["kind"] = kind,
            ["subject"] = Cut(subject, 80),
            ["before"] = Cut(before, 80),
            ["after"] = Cut(after, 80),
            ["detail"] = Cut(detail, 200),
        };
        if (handover >= 0) {
            body["handover"] = handover;
        }

        if (keeper != 0) {
            body["keeper"] = keeper.ToString();
        }

        var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
        var steam = PeerKeeper.PlayerId;
        _sending = _sending.ContinueWith(_ => {
            try {
                var reply = EtWorldDepot.Request(service.Address, service.KeyFile, steam, Environment.UserName, "POST", "/v1/w/{w}/flag", bytes);
                _answered.Enqueue(() => {
                    if (reply.Status is 200 or 201) {
                        EmpLog.Information("Cross-check: {Kind} flagged on version {Version} ({Count} reports, {Reporters} players)",
                            kind, (long?)reply.Json?["version"] ?? 0, (int?)reply.Json?["count"] ?? 0, (int?)reply.Json?["reporters"] ?? 0);
                    } else {
                        EmpLog.Warning("Cross-check: the world service did not take the report ({Status} {Error})", reply.Status,
                            (string?)reply.Json?["error"] ?? "");
                    }
                });
            } catch (Exception ex) {
                var why = ex.GetBaseException().Message;
                _answered.Enqueue(() => EmpLog.Warning("Cross-check: the report could not be sent: {Why}", why));
            }
        }, Tasks.TaskScheduler.Default);
    }

    private static string Cut(string text, int max)
    {
        return text.Length > max ? text.Substring(0, max) : text;
    }

    private static void Keep(JObject evidence)
    {
        try {
            var file = EvidenceFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && new FileInfo(file).Length > MaxEvidenceBytes) {
                File.Copy(file, file + ".old", true);
                File.Delete(file);
            }

            File.AppendAllText(file, evidence.ToString(Formatting.None) + "\n");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Debug(ex, "Cross-check: the evidence could not be written");
        }
    }
}
