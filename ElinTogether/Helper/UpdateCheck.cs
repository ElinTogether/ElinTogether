using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using ElinTogether.Components;
using ElinTogether.LangMod;
using ElinTogether.Patches;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Tells the player a newer Elin Together was released (user 2026-11-01). Once per game start, at most every six
///     hours (the answer is kept in EmpConfig.Client.UpdateLatest), GitHub is asked for the latest release off the main
///     thread; offline, slow or anything else wrong: nothing happens. A newer one shows on the title's Multiplayer
///     button, in the Play and Patch notes tabs, and once per version as a message. Never for a build newer than it
/// </summary>
internal static class UpdateCheck
{
    private const string Api = "https://api.github.com/repos/jeanfreger/elin-together-independence/releases/latest";
    private static readonly TimeSpan _every = TimeSpan.FromHours(6);
    private static bool _started;

    // the latest release as last read: its version (three parts) and page; null when unknown
    private static string? _version;
    private static string _url = "";

    /// <summary>The newer version to tell about ("0.26.1600"), null when this build is the latest or newer</summary>
    internal static string? Newer =>
        EmpConfig.Client.CheckUpdates.Value && _version is { } v && Compare(v, TabPatchNotes.Installed) > 0 ? v : null;

    /// <summary>The release's page, where it is downloaded</summary>
    internal static string Url => _url.StartsWith("https://github.com/", StringComparison.Ordinal)
        ? _url
        : "https://github.com/jeanfreger/elin-together-independence/releases/latest";

    /// <summary>
    ///     At the first title screen (and when the setting is switched on): the kept answer, then GitHub when it is
    ///     older than six hours. Once per game start
    /// </summary>
    internal static void Begin()
    {
        if (_started || EmpServer.Requested || !EmpConfig.Client.CheckUpdates.Value) {
            return;
        }

        _started = true;
        try {
            var kept = EmpConfig.Client.UpdateLatest.Value.Split('|');
            if (kept.Length == 3 && long.TryParse(kept[0], out var at)) {
                _version = kept[1].Length > 0 ? kept[1] : null;
                _url = kept[2];
                // (shown at once, even when GitHub is asked again below and does not answer)
                Shown();
                if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(at) < _every) {
                    return;
                }
            }
        } catch (Exception ex) {
            EmpLog.Debug(ex, "The kept update answer could not be read");
        }

        try {
            EmpMod.Instance.StartCoroutine(Ask());
        } catch (Exception ex) {
            EmpLog.Debug(ex, "The update check could not start");
        }
    }

    private static IEnumerator Ask()
    {
        var work = System.Threading.Tasks.Task.Run(Fetch);
        while (!work.IsCompleted) {
            yield return null;
        }

        if (work.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || work.Result is not { } found) {
            yield break;
        }

        _version = found.Version;
        _url = found.Url;
        EmpConfig.Client.UpdateLatest.Value = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{_version}|{_url}";
        EmpLog.Debug("Latest release: {Version} (installed {Installed})", _version, TabPatchNotes.Installed);
        Shown();
    }

    // off the main thread; null on any trouble (offline, rate limit, a tag that is no version)
    private static (string Version, string Url)? Fetch()
    {
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#pragma warning disable SYSLIB0014 // HttpWebRequest is what GitHubDepot saw working from the game's Mono
            var request = (HttpWebRequest)WebRequest.Create(Api);
#pragma warning restore SYSLIB0014
            request.UserAgent = "ElinTogether";
            request.Accept = "application/vnd.github+json";
            request.Timeout = request.ReadWriteTimeout = 5000;
            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream()!, Encoding.UTF8);
            var json = JObject.Parse(reader.ReadToEnd());
            var version = Parse((string?)json["tag_name"] ?? "");
            var url = (string?)json["html_url"] ?? "";
            if (version is null || !url.StartsWith("https://github.com/", StringComparison.Ordinal)) {
                return null;
            }

            return (version, url);
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>"independence-0.26.1597" (or "v0.26.1597") as "0.26.1597"; null when it holds no such version</summary>
    internal static string? Parse(string tag)
    {
        var start = tag.LastIndexOf('-') + 1;
        var text = tag.Substring(start).TrimStart('v', 'V');
        var parts = text.Split('.');
        return parts.Length >= 3 && parts.Take(3).All(p => p.Length > 0 && p.All(char.IsDigit))
            ? string.Join(".", parts.Take(3))
            : null;
    }

    // the two versions part by part; a missing or unreadable part counts as 0
    internal static int Compare(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < 3; i++) {
            var p = i < x.Length && long.TryParse(x[i], out var px) ? px : 0;
            var q = i < y.Length && long.TryParse(y[i], out var qy) ? qy : 0;
            if (p != q) {
                return p.CompareTo(q);
            }
        }

        return 0;
    }

    // the answer is in: the title button, and the message when it is the first time for this version
    private static void Shown()
    {
        TitleButtonPatch.ShowUpdate();
        Announce();
    }

    [ElinPostSceneInit]
    private static void OnScene(Scene.Mode mode)
    {
        if (mode == Scene.Mode.Title) {
            Announce();
        } else if (mode == Scene.Mode.StartGame && Newer is not null) {
            EmpMod.Instance.StartCoroutine(AnnounceLoaded());
        }
    }

    // a loaded game: once its map is up (the message goes to the game's log), at most half a minute later
    private static IEnumerator AnnounceLoaded()
    {
        for (var waited = 0f; waited < 30f; waited += Time.unscaledDeltaTime) {
            if (EClass.core is { IsGameStarted: true, game.isLoading: false } && EClass._zone is not null) {
                Announce();
                yield break;
            }

            yield return null;
        }
    }

    /// <summary>Once per new version: a short message, on the title screen or in a loaded game</summary>
    private static void Announce()
    {
        if (Newer is not { } newer || EmpConfig.Client.UpdateAnnounced.Value == newer) {
            return;
        }

        if (EClass.core is not { } core || (core.IsGameStarted && (core.game is null || core.game.isLoading))) {
            return;
        }

        EmpConfig.Client.UpdateAnnounced.Value = newer;
        EmpPop.Information("emp_update_toast".Loc(newer));
    }
}
