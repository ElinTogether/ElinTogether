using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ElinTogether.Models;
using ElinTogether.Net;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;

namespace ElinTogether.Helper;

/// <summary>
///     The player's answers to the report form, laid out as the game's own "Report Bugs" window (user 2026-10-30):
///     a category, a one-line summary (the issue's title), the detail, whether it happens again (0 yes, 1 no, 2 not
///     tried), and whether the video and the picture of the game screen go with it
/// </summary>
internal sealed record ReportAnswers(int Category, string Summary, string Detail, int Again, bool Video, bool Picture)
{
    // what the summary and the detail need at least to send the report
    internal const int SummaryMin = 10;
    internal const int DetailMin = 30;

    // what each box takes (the detail and the context stay under the relay's 4000 characters)
    internal const int SummaryMax = 120;
    internal const int DetailMax = 1500;

    internal static readonly string[] AgainWords = ["Yes", "No", "Not tried"];

    // the categories as the issue names them; the form's labels are emp_ui_report_cat_<id>
    internal static readonly string[] CategoryIds = ["bug", "lost", "disconnect", "desync", "idea"];
    internal static readonly string[] CategoryWords = ["Bug", "Lost item", "Disconnection", "Desync", "Suggestion"];

    internal string CategoryWord => CategoryWords[Mathf.Clamp(Category, 0, CategoryWords.Length - 1)];

    internal string AgainWord => AgainWords[Mathf.Clamp(Again, 0, AgainWords.Length - 1)];

    /// <summary>The issue's title: "[Category] Summary"</summary>
    internal string Title => $"[{CategoryWord}] {BugReport.Redact(BugReport.FirstLine(Summary ?? ""))}";

    /// <returns>null when the summary and the detail are long enough, else the label ids of the short ones</returns>
    internal string[]? Missing()
    {
        var shortOnes = new[] {
            ("emp_ui_report_summary", Summary, SummaryMin), ("emp_ui_report_detail", Detail, DetailMin),
        }.Where(a => (a.Item2 ?? "").Trim().Length < a.Item3).Select(a => a.Item1).ToArray();
        return shortOnes.Length == 0 ? null : shortOnes;
    }
}

/// <summary>The host's log as a guest's report carries it, or why it does not (Note)</summary>
internal sealed class HostLog
{
    internal string Session = "";
    internal string Player = "";
    internal string Note = "";
}

/// <summary>
///     One zip a player can send when something goes wrong: the game's own log (where the game writes its
///     exceptions, which the mod's log does not have), the last logs of the mod, and a short note of the
///     situation. Until now reports came as screenshots, or with the one log that did not hold the error <br />
///     Never in it: the saves, and the settings file (it holds the depot's password or access key). The logs are
///     redacted (Redact): the report becomes a public issue
/// </summary>
internal static class BugReport
{
    // (the last two: older days only took the room of the replay's pictures under the relay's 6 MB)
    private const int ModLogs = 2;

    /// <summary>
    ///     Writes the zip and gives its path, or null with the reason logged. What needs the game is read here; the
    ///     logs are copied (and redacted line by line, tens of MB on a long day) on a worker thread
    /// </summary>
    /// <param name="video">the replay's video (false when sent: it goes to the relay as a file of its own)</param>
    /// <param name="picture">the picture of the game screen as screen.jpg (false when sent: the relay's last.jpg)</param>
    /// <param name="report">the player's answers and the automatic context (report.txt)</param>
    /// <param name="host">the host's log, for a guest's report</param>
    private static IEnumerator Make(bool video, bool picture, string? report, HostLog? host, Action<string?> done)
    {
        var data = Application.persistentDataPath;
        var info = Describe();
        var avi = video ? ReplayBuffer.Video() : null;
        var jpg = picture ? ReplayBuffer.GamePicture() : null;
        var work = System.Threading.Tasks.Task.Run(() => Write(data, info, avi, jpg, report, host));
        while (!work.IsCompleted) {
            yield return null;
        }

        // (logged here, on the game's thread: the log's enrichers read the session)
        if (work.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) {
            EmpLog.Information("Bug report written to {Path}", work.Result);
            done(work.Result);
        } else {
            EmpLog.Warning(work.Exception?.GetBaseException(), "The bug report could not be written");
            done(null);
        }
    }

    // (on a worker thread: no game, no log; a failure is the task's)
    private static string Write(string data, string info, byte[]? video, byte[]? picture, string? report, HostLog? host)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var folder = Directory.Exists(desktop) ? desktop : data;
        var path = Path.Combine(folder, $"ElinTogether-rapport-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using (var file = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
            if (report is not null) {
                Add(zip, "report.txt", Encoding.UTF8.GetBytes(report));
            }

            Add(zip, "info.txt", Encoding.UTF8.GetBytes(info));
            // (redacted by the host already, and again here: the guest does not trust what came in)
            if (host is { Session.Length: > 0 }) {
                Add(zip, "host-Session.log", Encoding.UTF8.GetBytes(Redact(host.Session)));
            }

            if (host is { Player.Length: > 0 }) {
                Add(zip, "host-Player.log", Encoding.UTF8.GetBytes(Redact(host.Player)));
            }

            // the last 30 seconds as a video (sent beside the zip to the relay, kept in it for the local report)
            if (video is not null) {
                using var entry = zip.CreateEntry("replay.avi", System.IO.Compression.CompressionLevel.NoCompression).Open();
                entry.Write(video, 0, video.Length);
            }

            // the game screen when the report was opened (a JPEG: not compressed again)
            if (picture is not null) {
                using var entry = zip.CreateEntry("screen.jpg", System.IO.Compression.CompressionLevel.NoCompression).Open();
                entry.Write(picture, 0, picture.Length);
            }

            foreach (var name in new[] { "Player.log", "Player-prev.log" }) {
                AddFile(zip, Path.Combine(data, name), name);
            }

            var logs = Path.Combine(data, "ElinMP", "Logs");
            if (Directory.Exists(logs)) {
                foreach (var log in new DirectoryInfo(logs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(ModLogs)) {
                    AddFile(zip, log.FullName, "ElinMP/" + log.Name);
                }
            }
        }

        return path;
    }

    private static string Describe()
    {
        var session = NetSession.Instance;
        var text = new StringBuilder();
        text.AppendLine($"Elin Together {ModInfo.BuildVersion}");
        text.AppendLine($"Elin {EClass.core.version.GetText()}");
        // who sent it, to ask them more (user, 2026-10-27; the player is told it is public)
        text.AppendLine($"Player (Steam): {SteamName()}");
        text.AppendLine($"Written {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local), {DateTime.UtcNow:HH:mm:ss} UTC");
        text.AppendLine($"Role: {(session.Transport is null ? "no session" : session.Transport.GetType().Name)}, away: {session.IsAway}, guest: {session.IsGuest}");
        text.AppendLine($"Players: {session.CurrentPlayers.Count}");
        // the mod log has Debug lines only then (EmpLogger.Level)
        text.AppendLine($"Mod log level: {EmpLogger.Level.MinimumLevel}");
        if (EClass.core.IsGameStarted) {
            text.AppendLine($"Zone: {EClass._zone?.id} #{EClass._zone?.uid}, date raw {EClass.world.date.GetRaw()}");
        }

        try {
            // (who the ally count on screen is made of: "one too many" was reported with nothing to tell which)
            if (EClass.pc?.party is { } party) {
                text.AppendLine($"Party of {EClass.pc.uid}: count {party.Count()}, leader {party.uidLeader}, own slots {CompanionHelper.UsedAllySlots(EClass.pc)}");
                foreach (var member in party.members.Where(c => c is not null)) {
                    text.AppendLine($"  {member.uid} {member.id}: owner {member.CompanionOwnerUid}, player {member.IsPC || member.GetBool("remote_chara")}, " +
                                    $"dead {member.isDead}, zone {member.currentZone?.id}, slots {1 + member.Evalue(1431)}");
                }
            }
        } catch (Exception) {
            // noexcept
        }

        try {
            text.AppendLine("Mods: " + string.Join(", ", EClass.core.mods.packages.Where(p => p.activated).Select(p => p.id)));
        } catch (Exception) {
            // (the list of mods is not worth losing the report for)
        }

        return Redact(text.ToString());
    }

    private static string SteamName()
    {
        try {
            return SteamFriends.GetPersonaName();
        } catch (Exception) {
            // (Steam not running)
            return "-";
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal).Open();
        entry.Write(bytes, 0, bytes.Length);
    }

    // (the game and the mod keep their logs open: read without asking for them alone)
    private static void AddFile(ZipArchive zip, string path, string name)
    {
        if (!File.Exists(path)) {
            return;
        }

        try {
            // line by line, each redacted (the report is public)
            using var source = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
            using var entry = new StreamWriter(zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            while (source.ReadLine() is { } line) {
                entry.WriteLine(Redact(line));
            }
        } catch (IOException) {
            // noexcept
        }
    }

    // ---------------------------------------------------------------- what may not be public

    // a SteamID64 (individual accounts all start 7656119...)
    private static readonly Regex SteamId = new(@"(?<!\d)7656\d{13}(?!\d)", RegexOptions.Compiled);

    private static readonly Regex Ipv4 = new(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])", RegexOptions.Compiled);

    // full (4 groups and more) or with "::" (a clock "23:00:10" has two groups of digits only: not taken)
    private static readonly Regex Ipv6 = new(
        @"(?<![\w:])(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}(?![\w:])|(?<![\w:])(?:[0-9A-Fa-f]{1,4}:){1,6}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?(?![\w:])|(?<![\w:])::(?:[0-9A-Fa-f]{1,4})(?::[0-9A-Fa-f]{1,4}){0,6}(?![\w:])",
        RegexOptions.Compiled);

    // C:\Users\<name>\ (also with / or the doubled \\ of a JSON log); a name with spaces only up to the next separator
    private static readonly Regex UserPath = new(@"([A-Za-z]:(?:\\{1,2}|/)Users(?:\\{1,2}|/))(?:[^\\/"":\r\n]{1,40}?(?=[\\/])|[^\\/""\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    ///     What a public report must not show: Steam ids (17 digits from 7656) -> 7656***, IP addresses -> ***.***.***.***
    ///     (v6: ***:***), Windows user folders C:\Users\name\ -> C:\Users\***\ . Applied to every log a report carries,
    ///     the player's own and the host's. E.g. "peer 76561198012345678 at 192.168.1.20 C:\Users\Marc\AppData" gives
    ///     "peer 7656*** at ***.***.***.*** C:\Users\***\AppData"; "0.26.1555", "23:00:10" and "#109775242796802616"
    ///     stay as they are
    /// </summary>
    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) {
            return text;
        }

        if (text.Contains("7656")) {
            text = SteamId.Replace(text, "7656***");
        }

        if (text.IndexOf('.') >= 0) {
            text = Ipv4.Replace(text, "***.***.***.***");
        }

        if (text.Contains("::") || text.Count(c => c == ':') >= 3) {
            text = Ipv6.Replace(text, "***:***");
        }

        if (text.IndexOf("Users", StringComparison.OrdinalIgnoreCase) >= 0) {
            text = UserPath.Replace(text, "$1***");
        }

        return text;
    }

    // ---------------------------------------------------------------- the host's log in a guest's report (user 2026-10-30)

    // how long a guest waits for the host's answer before sending without it
    private const float HostLogWait = 10f;

    // what the host sends: the last 10 minutes of its session log, 300 KB at most, and the end of its Player.log
    private const int HostSessionMost = 300 * 1024;
    private const int HostPlayerMost = 100 * 1024;
    private static readonly TimeSpan HostSessionSpan = TimeSpan.FromMinutes(10);

    // once a minute per guest (a log is a few hundred KB for the host to read and send)
    private const float HostLogEvery = 60f;
    private static readonly Dictionary<int, float> HostLogAsked = [];

    private static int _nonce;
    private static HostLogAnswer? _answer;

    // the last answer, kept a minute: "Prepare" then "Send" do not ask twice (the host would refuse the second)
    private static HostLog? _lastHostLog;
    private static float _lastHostLogAt = float.MinValue;

    /// <summary>On the host: the answer to a guest's request, the logs if the host's box allows it</summary>
    internal static HostLogAnswer HostLogFor(int peer, int nonce)
    {
        if (!EmpConfig.Server.ShareLogForReports.Value) {
            return new() { Nonce = nonce, Refused = "the host does not share its log (its rule \"Share the host's log with bug reports\" is off)" };
        }

        var now = Time.unscaledTime;
        if (HostLogAsked.TryGetValue(peer, out var last) && now - last < HostLogEvery) {
            return new() { Nonce = nonce, Refused = "the host's log was already asked for less than a minute ago" };
        }

        HostLogAsked[peer] = now;
        var data = Application.persistentDataPath;
        var session = new DirectoryInfo(Path.Combine(data, "ElinMP", "Logs")) is { Exists: true } logs
            ? logs.GetFiles("Session_*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        var sessionText = session is not null && Tail(session.FullName, HostSessionMost) is { } s ? LastMinutes(Encoding.UTF8.GetString(s)) : "";
        var playerText = Tail(Path.Combine(data, "Player.log"), HostPlayerMost) is { } p ? Encoding.UTF8.GetString(p) : "";
        EmpLog.Information("Sent the end of the host's log to peer {Peer} for a bug report ({Session} + {Player} characters)",
            peer, sessionText.Length, playerText.Length);
        return new() { Nonce = nonce, Session = Redact(sessionText), Player = Redact(playerText) };
    }

    // the session log's lines of the last minutes (each line is JSON from {"@t":"<UTC time>"); the first, cut, dropped
    private static string LastMinutes(string text)
    {
        var lines = text.Split('\n').Skip(1);
        var since = DateTime.UtcNow - HostSessionSpan;
        var kept = new StringBuilder();
        foreach (var line in lines) {
            if (line.StartsWith("{\"@t\":\"", StringComparison.Ordinal) && line.IndexOf('"', 7) is > 7 and var end &&
                DateTime.TryParse(line.Substring(7, end - 7), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) && at < since) {
                continue;
            }

            kept.Append(line).Append('\n');
        }

        return kept.ToString();
    }

    /// <summary>On a guest: the host's answer came</summary>
    internal static void OnHostLog(HostLogAnswer answer)
    {
        if (answer.Nonce == _nonce) {
            _answer = answer;
        }
    }

    /// <summary>
    ///     A guest asks the host for its log and waits HostLogWait seconds at most; the host and a solo player have
    ///     nothing to ask. Always gives a HostLog, its Note saying what happened
    /// </summary>
    private static IEnumerator FetchHostLog(Action<HostLog> done)
    {
        var session = NetSession.Instance;
        if (session.Transport is not ElinNetClient client) {
            done(new() { Note = session.Transport is null ? "not in a multiplayer game" : "the reporter is the host: its own logs are attached" });
            yield break;
        }

        if (!session.Rules.ShareLogForReports) {
            done(new() { Note = "not attached: the host's rule \"Share the host's log with bug reports\" is off" });
            yield break;
        }

        if (_lastHostLog is { Note: "attached" } && Time.unscaledTime - _lastHostLogAt < HostLogEvery) {
            done(_lastHostLog);
            yield break;
        }

        _answer = null;
        var nonce = ++_nonce;
        client.Delta.AddRemote(new HostLogRequest { Nonce = nonce });
        var until = Time.unscaledTime + HostLogWait;
        while (_answer is null && Time.unscaledTime < until) {
            yield return null;
        }

        var answer = _answer;
        HostLog log;
        if (answer is null) {
            log = new() { Note = $"not attached: the host did not answer within {HostLogWait:F0} s" };
        } else if (answer.Refused is not null) {
            log = new() { Note = $"not attached: {answer.Refused}" };
        } else {
            log = new() { Session = answer.Session, Player = answer.Player, Note = "attached" };
            _lastHostLog = log;
            _lastHostLogAt = Time.unscaledTime;
        }

        EmpLog.Information("Bug report: the host's log {Note}", log.Note);
        done(log);
    }

    // ---------------------------------------------------------------- the report's text

    // what the relay takes of the text (dev/relay/worker.js clean(report.text, 4000))
    private const int MaxText = 3900;

    /// <summary>
    ///     The issue's text: the category, the summary, whether it happens again and the detail first (the form's
    ///     order), then what goes with it (video, picture, the host's log), then the automatic context (what the player
    ///     did last, the windows, under the mouse, the zone)
    /// </summary>
    internal static string Compose(ReportAnswers answers, HostLog host)
    {
        static string Cut(string text, int most) => text.Length <= most ? text : text.Substring(0, most) + "…";

        var head = new StringBuilder();
        head.AppendLine($"**Category:** {answers.CategoryWord}").AppendLine();
        head.AppendLine($"**Summary:** {Cut((answers.Summary ?? "").Trim().Replace('\n', ' '), ReportAnswers.SummaryMax)}").AppendLine();
        head.AppendLine($"**Does it happen again?** {answers.AgainWord}").AppendLine();
        head.AppendLine("#### Detail").AppendLine().AppendLine(Cut((answers.Detail ?? "").Trim(), ReportAnswers.DetailMax)).AppendLine();
        head.AppendLine(!answers.Video ? "**Video:** not attached (the player's box was off)"
            : ReplayBuffer.HasFrames ? "**Video:** attached (the last 30 seconds before the report was opened)"
            : "**Video:** none (the screen recording is off or had no picture yet)").AppendLine();
        head.AppendLine(!answers.Picture ? "**Game picture:** not attached (the player's box was off)"
            : ReplayBuffer.HasGamePicture ? "**Game picture:** attached (the game screen just before the report was opened)"
            : "**Game picture:** none (the screen recording is off or had no picture yet)").AppendLine();
        head.AppendLine(host.Note == "attached"
            ? "**Host's log:** attached in report.zip as host-Session.log (its last 10 minutes) and host-Player.log (Steam ids, IP addresses and user names removed)"
            : $"**Host's log:** {host.Note}").AppendLine();

        // the world's rules that differ from the defaults (user 2026-10-30), as the game the report comes from knows them
        head.AppendLine($"**World rules (differ from the defaults):** {Cut(Components.SettingsTable.ReportSummary(), 1200)}").AppendLine();

        // the context takes what room is left (the player's words are never cut for it)
        var room = MaxText - head.Length - 60;
        var context = "#### Context (automatic)\n\n```\n" + Cut(ReportContext.Text(), Math.Max(room, 200)).Replace("```", "'''") + "\n```\n";
        return Redact(head + context);
    }

    // ---------------------------------------------------------------- sent from the game (user, 2026-10-27)

    /// <summary>
    ///     The relay that turns a report into a GitHub issue (dev/relay/worker.js): it holds the GitHub key, the game
    ///     never does. Empty until it is online: the report then opens the issue page, filled, in the browser
    /// </summary>
    internal const string Relay = "https://elin-report.yhhhuii095.workers.dev";

    private const string Issues = "https://github.com/jeanfreger/elin-together-independence/issues/new";

    // what the relay takes at most for the logs
    private const int MaxZip = 6 * 1024 * 1024;

    /// <summary>
    ///     The player's answers and its logs go to the developers: a GitHub issue made by the relay, its link given
    ///     back; or, without the relay, the issue page opened filled in the browser and the zip shown to drag in.
    ///     A guest first asks the host for its log (HostLogWait seconds at most). Refused (done(null)) while an
    ///     answer is too short
    /// </summary>
    internal static void Send(ReportAnswers answers, Action<string?> done)
    {
        if (answers.Missing() is not null) {
            done(null);
            return;
        }

        EmpMod.Instance.StartCoroutine(SendFlow(answers, done));
    }

    private static IEnumerator SendFlow(ReportAnswers answers, Action<string?> done)
    {
        HostLog host = new();
        yield return FetchHostLog(h => host = h);
        var words = Compose(answers, host);
        // (the video goes beside the zip to the relay: not twice)
        string? zip = null;
        yield return Make(false, false, words, host, z => zip = z);

        if (Relay.Length == 0) {
            OpenIssuePage(answers.Title, words, zip);
            done(null);
            yield break;
        }

        yield return Post(answers, words, zip, done);
    }

    /// <summary>"Prepare a bug report": the same answers and the host's log, in a zip on the desktop only</summary>
    internal static void Prepare(ReportAnswers answers, Action<string?> done)
    {
        EmpMod.Instance.StartCoroutine(PrepareFlow(answers, done));
    }

    private static IEnumerator PrepareFlow(ReportAnswers answers, Action<string?> done)
    {
        HostLog host = new();
        yield return FetchHostLog(h => host = h);
        yield return Make(answers.Video, answers.Picture, Compose(answers, host), host, done);
    }

    private static IEnumerator Post(ReportAnswers answers, string words, string? zip, Action<string?> done)
    {
        var title = answers.Title;
        string payload;
        try {
            var bytes = zip is not null && new FileInfo(zip).Length <= MaxZip ? File.ReadAllBytes(zip) : null;
            payload = JsonConvert.SerializeObject(new {
                title,
                text = words,
                info = Describe(),
                zip = bytes is null ? "" : Convert.ToBase64String(bytes),
                version = ModInfo.BuildVersion,
                // shown in the issue itself (user, 2026-10-27: everything in the report, public): the replay's sheet and
                // its last pictures as images, the ends of the two logs as text
                files = ShownFiles(answers),
            });
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "The bug report could not be read to send it");
            OpenIssuePage(title, words, zip);
            done(null);
            yield break;
        }

        using var request = new UnityWebRequest(Relay.TrimEnd('/') + "/report", "POST");
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("X-Emp-Report", "elin-together");
        request.timeout = 60;
        yield return request.SendWebRequest();

        string? url = null;
        if (request.result == UnityWebRequest.Result.Success) {
            try {
                url = JsonConvert.DeserializeAnonymousType(request.downloadHandler.text, new { url = "" })?.url;
            } catch (JsonException) {
            }
        }

        if (url is null) {
            EmpLog.Warning("The relay did not take the bug report ({Code}: {Error}), the issue page opens instead",
                request.responseCode, request.error);
            OpenIssuePage(title, words, zip);
        } else {
            EmpLog.Information("Bug report sent: {Url}", url);
        }

        done(url);
    }

    // what the issue can hold as text: the end of each log (where the bug is; the zip has them whole)
    private const int LogTail = 24 * 1024;

    private static List<object> ShownFiles(ReportAnswers answers)
    {
        var files = new List<object>();
        if (answers.Video && ReplayBuffer.Video() is { } video) {
            files.Add(new { name = "replay.avi", content = Convert.ToBase64String(video) });
        }

        // the game screen just before the report was opened, shown in the issue (the relay's name for it: last.jpg)
        if (answers.Picture && ReplayBuffer.GamePicture() is { } picture) {
            files.Add(new { name = "last.jpg", content = Convert.ToBase64String(picture) });
        }

        var data = Application.persistentDataPath;
        var modLog = new DirectoryInfo(Path.Combine(data, "ElinMP", "Logs")) is { Exists: true } logs
            ? logs.GetFiles("Session*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        foreach (var (path, name) in new[] { (Path.Combine(data, "Player.log"), "Player.log"), (modLog?.FullName, "ElinTogether.log") }) {
            if (path is not null && Tail(path, LogTail) is { } text) {
                files.Add(new { name, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Redact(Encoding.UTF8.GetString(text)))) });
            }
        }

        return files;
    }

    private static byte[]? Tail(string path, int most)
    {
        try {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(source.Length, most);
            source.Seek(-length, SeekOrigin.End);
            var bytes = new byte[length];
            var read = 0;
            while (read < length && source.Read(bytes, read, length - read) is > 0 and var n) {
                read += n;
            }

            return bytes;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    internal static string FirstLine(string words)
    {
        var line = words.Trim().Split('\n')[0].Trim();
        return line.Length > 80 ? line.Substring(0, 80) + "…" : line;
    }

    /// <summary>
    ///     Without the relay: GitHub's new issue page, filled (the player signs in and clicks Submit), and the zip shown
    ///     to drag into it
    /// </summary>
    private static void OpenIssuePage(string title, string words, string? zip)
    {
        var body = $"### What happened\n\n{words}\n\n### The game\n\n```\n{Describe()}```\n\n(Drag the zip ElinTogether-rapport-*.zip from your desktop here.)";
        if (body.Length > 6000) {
            body = body.Substring(0, 6000);
        }

        Application.OpenURL($"{Issues}?labels=bug&title={Uri.EscapeDataString("[In-game report] " + title)}&body={Uri.EscapeDataString(body)}");
        if (zip is not null) {
            Util.ShowExplorer(zip);
        }
    }
}
