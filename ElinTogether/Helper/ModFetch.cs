using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ElinTogether.Helper.Steam;
using ElinTogether.LangMod;
using ElinTogether.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using Mod = ElinTogether.Helper.ModListFile.Mod;

namespace ElinTogether.Helper;

/// <summary>
///     The mods of the game joined, without subscribing to them (client setting FetchMods, off: then nothing in
///     here runs but Boot, which only looks for what an earlier run left). As Civilization VI does, except that
///     Elin activates its mods when it starts: the Workshop mods missing here are downloaded (the Steam account
///     subscribes to nothing), Elin restarts once with exactly the mods of that game and joins it again by itself,
///     and the player's own list is back at the start after. <br />
///     What is on the disk, in order (dev/PLAN_mods_de_l_host.md has what a crash leaves at each step): <br />
///     1. `loadorder.txt`, the player's list, gets a line that switches off every folder about to appear (Hide);
///     2. the downloads (Steam's own folder); 3. the ticket (`ElinMP/modsession.txt`: where to come back);
///     4. the player's list copied beside itself (`loadorder.elintogether-player.txt`); 5. when the game only
///     loads the Workshop folders the account is subscribed to (its "sync mods" setting), one link a mod in
///     `Package/` (a directory junction, EmpSession_number); 6. `loadorder.txt` replaced by the list of the
///     session; 7. Elin closes and starts again. <br />
///     The restarted game has read the list of the session when this mod starts in it: Boot puts the player's
///     list back at once, so the list of the session is on the disk only between two runs. Nothing is ever
///     subscribed to, unsubscribed from or deleted in the Workshop; a link is removed, never what it points to <br />
///     Security (the host names what is fetched): only Workshop numbers are read from its list; the Workshop is
///     asked about each item first (an Elin mod, not banned, size capped, one log line each); a mod with code
///     (a DLL) is loaded only with the client setting FetchCodeMods, off by default. Before anything is
///     downloaded or restarted the player is asked, in the game's own box (who the host is, a Steam friend or
///     not, each mod), and again before a restart with code mods; "Always trust this host" (client setting
///     TrustedHosts) skips that unless a code mod not accepted from that host yet is in the list
/// </summary>
internal static class ModFetch
{
    // first line of a loadorder.txt written here (no comma in it: the game takes it for nothing)
    private const string Marker = "# elintogether session";
    private const string App = "2135150";

    // as the game's own downloads: an item that neither waits nor moves, Steam silent, and never longer than this
    private const float IdleSeconds = 20f;
    private const float QuietSeconds = 90f;
    private const float TotalSeconds = 900f;
    private const int MaxMods = 200;
    private const double TicketMinutes = 30;

    // security (the host chooses what is fetched): an item and all of them together are never bigger than
    // this, a list that long is not read, and the Workshop is asked about each item before any download
    private const long MaxItemBytes = 1L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxReferenceChars = 256 * 1024;
    private const float QuerySeconds = 30f;

    private static readonly string _ticket = Path.Combine(Application.persistentDataPath, "ElinMP", "modsession.txt");

    // what was added to the game in this run, to keep switched off in the player's own list
    private static readonly List<string> _hidden = [];

    // this run was started for a session, or tried and gave up: it never restarts Elin (again)
    private static bool _done;
    private static bool _busy;

    /// <summary>
    ///     The host's ClientOnlyMods of the last join (set by the source validation request): a session list keeps
    ///     them as the player has them, the host neither needs nor refuses them
    /// </summary>
    internal static HashSet<string> ClientOnly = [];
    private static bool _applied;
    private static bool _pending;
    private static float _titleAt;
    private static string? _return;
    private static string? _wanted;

    /// <summary>
    ///     This run of Elin has the mods of a multiplayer game, not the player's own
    /// </summary>
    internal static bool Session { get; private set; }

#if DEBUG
    /// <summary>
    ///     The bench points the lists at a copy of its own (loadorder.txt in a temporary folder): the backup and the
    ///     ticket go beside it, the real files are never touched
    /// </summary>
    internal static string? BenchLoadOrder { get; set; }

    private static string LoadOrder => BenchLoadOrder ?? CorePath.PathLoadOrder;
    private static string Ticket => BenchLoadOrder is null ? _ticket : Path.Combine(Path.GetDirectoryName(BenchLoadOrder)!, "modsession.txt");
#else
    private static string LoadOrder => CorePath.PathLoadOrder;
    private static string Ticket => _ticket;
#endif
    private static string PlayerList => Path.Combine(Path.GetDirectoryName(LoadOrder)!, "loadorder.elintogether-player.txt");

    /// <summary>
    ///     As Core.StartCase and ModManager.RefreshMods decide it: the game only loads the Workshop folders the
    ///     account is subscribed to. A mod fetched without subscribing is then shown to it as a local one
    /// </summary>
    private static bool NeedsLinks => !BaseCore.IsOffline && !EClass.debug.skipModSync &&
                                      (EClass.core.config is null || EClass.core.config.other.syncMods) &&
                                      SteamUGC.GetNumSubscribedItems() != 0;

    /// <summary>
    ///     When this mod starts: the game has read loadorder.txt. The list of a session goes away, the player's
    ///     own comes back, the links of a session that is over are removed, and the ticket is read once
    /// </summary>
    internal static void Boot()
    {
        try {
            Session = PutPlayerListBack();

            if (Session) {
                // closing its mod viewer writes the list of this run, the session's, over the player's
                new Harmony(ModInfo.Guid + ".modsession").Patch(
                    AccessTools.Method(typeof(ModManager), nameof(ModManager.SaveLoadOrder)),
                    new(typeof(ModFetch), nameof(KeepPlayerList)));
            } else {
                RemoveLinks();
            }

            if (File.Exists(Ticket)) {
                var lines = File.ReadAllLines(Ticket);
                // read once, whatever it says: a restart is never followed by another
                File.Delete(Ticket);
                var fresh = lines.Length > 1 && long.TryParse(lines[0], out var ticks) &&
                            Math.Abs((DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMinutes) < TicketMinutes;
                EmpLog.Information("Elin was restarted for the mods of a game: back to {Where} ({State}, session list {Session})",
                    lines.ElementAtOrDefault(1), fresh ? "now" : "too long ago", Session);
                _done = true;
                _wanted = string.Join("\n", lines.Skip(2));
                if (fresh && EmpConfig.Client.FetchMods.Value) {
                    _return = lines[1];
                }
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not put the player's mod list back");
        }

        _done |= Session;
        _pending = Session || _return is not null || _wanted is not null;
    }

    /// <summary>
    ///     Called every frame. Once the title screen is up: what this run is said, and the game is joined again
    /// </summary>
    internal static void Update()
    {
        if (!_pending) {
            return;
        }

        if (EClass.core.IsGameStarted || EClass.ui == null || EClass.ui.GetLayer<LayerTitle>() == null) {
            _titleAt = 0f;
            return;
        }

        if (_titleAt <= 0f) {
            _titleAt = Time.unscaledTime + 2f;
            return;
        }

        if (Time.unscaledTime < _titleAt) {
            return;
        }

        _pending = false;
        // (the mods are all activated by now: what the restart was for, against what this run has)
        if (_wanted is not null) {
            ModList.Compare(ModListFile.Parse(_wanted), "the list this restart was for");
            _wanted = null;
        }

        if (Session) {
            EmpPop.Information("emp_ui_mods_session".lang());
        }

        if (_return is { } where) {
            _return = null;
            Return(where);
        }
    }

    /// <summary>
    ///     The game closes: the links of the session go (the next start removes those a crash leaves), and what
    ///     was fetched without a restart stays switched off in the player's list
    /// </summary>
    internal static void OnQuit()
    {
        try {
            if (Session) {
                RemoveLinks();
            } else if (_hidden.Count > 0 && !_applied) {
                // (the game's mod viewer rewrites the list from the mods it knows: those lines would be gone)
                Hide([]);
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not tidy the mods of the session at the exit");
        }
    }

    /// <summary>
    ///     Fetches what is missing, then restarts Elin with the mods of the reference and comes back to
    ///     <paramref name="returnTo" />. False: nothing is done here (setting off, a run that restarted already,
    ///     Steam away, a list that cannot be trusted) and the caller goes on as without it
    /// </summary>
    /// <param name="reference">the list of the game, as modlist.txt</param>
    /// <param name="host">the Steam account of the host naming these mods (0: unknown, never trusted)</param>
    internal static bool Begin(string reference, ModList.Diff diff, string returnTo, ulong host = 0, string hostName = "")
    {
        if (!EmpConfig.Client.FetchMods.Value || _done || _busy || returnTo.Length == 0 || BaseCore.IsOffline ||
            reference == _declined ||
            ModManager.disableMod || EmpServer.Requested || diff.Missing.Count > MaxMods ||
            reference.Length > MaxReferenceChars || diff.Missing.Any(m => !IsWorkshopId(m.Workshop))) {
            return false;
        }

        _busy = true;
        EmpLog.Information("Fetching {Count} mods of the game, then restarting to come back to {Where}",
            diff.Missing.Count, returnTo);
        EmpMod.Instance.StartCoroutine(Fetch(reference, diff, returnTo, host, hostName));
        return true;
    }

    /// <summary>
    ///     The host refused this game for mods of ours it does not have, and nothing is missing here (user
    ///     2026-10-30: the official server plays with this mod and YK Framework only). The player is asked in the
    ///     game's own box: yes, Elin restarts once with the mods of the host (its own look-only mods and what this
    ///     mod runs on stay), joins again by itself, and the player's own list is back for the next start; no,
    ///     <paramref name="refusal" /> is shown as without this. False: nothing is done here (as Begin)
    /// </summary>
    internal static bool BeginWithout(string reference, ModList.Diff diff, string returnTo, string refusal)
    {
        if (!EmpConfig.Client.FetchMods.Value || _done || _busy || returnTo.Length == 0 || diff.Missing.Count > 0 ||
            diff.Extra.Count == 0 || ModManager.disableMod || EmpServer.Requested || reference.Length > MaxReferenceChars) {
            return false;
        }

        _busy = true;
        EmpLog.Information("The game was refused for mods it does not have here: {Mods}. Asking to restart without them, then come back to {Where}",
            diff.Extra.Select(ModList.Name).ToList(), returnTo);
        var names = ModListFile.Parse(reference).Select(ModList.Name).Where(n => n.Length > 0).Prepend("Elin Together").ToList();
        var text = "emp_ui_mods_only_server".Loc(string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", ..." : ""));
        EmpMod.Instance.StartCoroutine(AskWithout(text, reference, returnTo, refusal));
        return true;
    }

    private static IEnumerator AskWithout(string text, string reference, string returnTo, string refusal)
    {
        // the caller closes its link with the host first
        yield return null;

        var choice = Choice.None;
        var box = Dialog.Choice(text, d => {
            d.AddButton("yes".lang(), () => choice = Choice.Accept);
            d.AddButton("no".lang(), () => choice = Choice.Cancel);
        });
        // (closed any other way, Escape or a click outside: no)
        box.SetOnKill(() => choice = choice == Choice.None ? Choice.Cancel : choice);
        while (choice == Choice.None) {
            yield return null;
        }

        EmpLog.Information("Restart without the mods the host does not have: the player chose {Choice}", choice);
        if (choice != Choice.Accept) {
            _busy = false;
            Dialog.Ok(refusal);
            yield break;
        }

        var failure = EClass.core.IsGameStarted ? "the player started a game" : Apply(reference, returnTo, [], false, []);
        if (failure is not null) {
            _busy = false;
            _done = true;
            EmpLog.Warning("Elin was not restarted without the mods the host does not have: {Why}", failure);
            Dialog.Ok(refusal);
            yield break;
        }

        yield return EmpMod.Instance.StartCoroutine(Restart());
    }

#if DEBUG
    /// <summary>
    ///     The list of the bench's session, without a restart: what Apply writes for these mods (the bench, with
    ///     BenchLoadOrder set)
    /// </summary>
    internal static string? WriteSessionOnly(string reference)
    {
        var failure = Apply(reference, "", [], false, []);
        _applied = false;
        return failure;
    }
#endif

    /// <summary>
    ///     What Boot does with the lists: a session's list on the disk is replaced by the player's own copy (or
    ///     goes, when the copy is gone). True: the list the game read was a session's
    /// </summary>
    internal static bool PutPlayerListBack()
    {
        var session = File.Exists(LoadOrder) && File.ReadAllLines(LoadOrder).FirstOrDefault() == Marker;
        if (!Restore() && session) {
            // the player's copy is gone: no list at all is every mod on, the game's own default
            File.Delete(LoadOrder);
        }

        return session;
    }

    private static IEnumerator Fetch(string reference, ModList.Diff diff, string returnTo, ulong host, string hostName)
    {
        // the caller closes its link with the host first
        yield return null;

        var failure = "error";
        // what the player is told when nothing is fetched
        var told = "emp_ui_mods_fetch_failed".lang();
        var line = "";
        Action kill = () => { };
        var folders = new Dictionary<string, string>();
        var results = new Dictionary<ulong, EResult>();
        Action<DownloadItemResult_t> onResult = result => {
            if (result.m_unAppID.m_AppId.ToString() == App) {
                results[result.m_nPublishedFileId.m_PublishedFileId] = result.m_eResult;
            }
        };

        try {
            line = "emp_ui_mods_fetching".Loc(0, diff.Missing.Count, "", 0);
            kill = EGui.CreatePopup(() => new(line), _ => !_busy).Kill;
            var links = NeedsLinks;
            var root = WorkshopRoot();
            var packages = EClass.core.mods.packages;
            // a mod that is on this PC, switched off in the player's list, is only switched on (not the link
            // of an earlier session: those are removed when this mod starts)
            var queue = diff.Missing
                .Where(m => !packages.Any(p => ModList.WorkshopOf(p) == m.Workshop && p.installed && p.dirInfo is not null &&
                                               !IsLink(p) && Directory.Exists(p.dirInfo.FullName)))
                .ToList();

            // security: the Workshop says what each item is before anything is downloaded (an item of Elin, a
            // mod, not banned, not too big). One that does not pass and nothing at all is fetched
            var details = new Dictionary<ulong, SteamUGCDetails_t>();
            for (var at = 0; at < queue.Count; at += 50) {
                // (50: the most one query of the Workshop answers)
                var batch = queue.Skip(at).Take(50).Select(m => new PublishedFileId_t(ulong.Parse(m.Workshop))).ToArray();
                var handle = SteamUGC.CreateQueryUGCDetailsRequest(batch, (uint)batch.Length);
                var answered = false;
                var ok = false;
                var call = CallResult<SteamUGCQueryCompleted_t>.Create((r, io) => {
                    answered = true;
                    ok = !io && r.m_eResult == EResult.k_EResultOK;
                });
                call.Set(SteamUGC.SendQueryUGCRequest(handle));
                var asked = Time.realtimeSinceStartup;
                while (!answered && Time.realtimeSinceStartup - asked < QuerySeconds) {
                    yield return null;
                }

                for (uint i = 0; ok && i < batch.Length; i++) {
                    if (SteamUGC.GetQueryUGCResult(handle, i, out var found)) {
                        details[found.m_nPublishedFileId.m_PublishedFileId] = found;
                    }
                }

                call.Dispose();
                SteamUGC.ReleaseQueryUGCRequest(handle);
            }

            long sum = 0;
            foreach (var mod in queue) {
                if (!details.TryGetValue(ulong.Parse(mod.Workshop), out var item)) {
                    failure = $"the Workshop did not say what {mod.Workshop} is";
                    yield break;
                }

                var refused = Refused(item);
                // one line an item: what it is and who put it on the Workshop
                EmpLog.Information("Workshop item {Workshop} \"{Title}\" by {Author} ({Owner}), {Size} bytes: {Verdict}",
                    mod.Workshop, ModListFile.Clean(item.m_rgchTitle ?? ""), Author(item.m_ulSteamIDOwner),
                    item.m_ulSteamIDOwner, item.m_nFileSize, refused ?? "may be fetched");
                if (refused is not null) {
                    failure = $"{mod.Workshop} is {refused}";
                    yield break;
                }

                sum += item.m_nFileSize;
            }

            if (sum > MaxTotalBytes) {
                failure = $"the mods of the game weigh {sum} bytes, more than {MaxTotalBytes}";
                yield break;
            }

            // security (advice of the community, approved by the project lead): mods may be malicious, the
            // player says yes before anything is downloaded or restarted. A trusted host is not asked again
            // unless it names a code mod not accepted from it yet (those already on this PC are known now,
            // those downloaded are asked about before the restart)
            hostName = HostName(host, hostName);
            var friend = IsFriend(host);
            var onDisk = diff.Missing.Except(queue).ToList();
            var knownCode = onDisk
                .Where(m => packages.FirstOrDefault(p => ModList.WorkshopOf(p) == m.Workshop && p.dirInfo is not null && !IsLink(p)) is { } pkg &&
                            HasCode(pkg.dirInfo.FullName))
                .Select(m => m.Workshop)
                .ToList();
            if (IsTrusted(host) && knownCode.All(Approved(host).Contains)) {
                EmpLog.Information("Mods of the host {Host} ({HostId}, friend {Friend}): trusted host, no new code mod, not asked",
                    hostName, host, friend);
            } else {
                foreach (var owner in details.Values.Select(d => d.m_ulSteamIDOwner).Distinct()) {
                    SteamFriends.RequestUserInformation(new(owner), true);
                }

                // (the names of the authors, when Steam has them in time)
                var named = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - named < 3f && details.Values.Any(d => Author(d.m_ulSteamIDOwner).Length == 0)) {
                    yield return null;
                }

                kill();
                kill = () => { };
                var text = AskText(hostName, friend, queue, onDisk, knownCode, details);
                var choice = Choice.None;
                Ask(text, friend, host != 0, "emp_ui_mods_ask_download", c => choice = c);
                while (choice == Choice.None) {
                    yield return null;
                }

                EmpLog.Information("Mods of the host {Host} ({HostId}, friend {Friend}), {Count} to download, {Code} with code known: the player chose {Choice}",
                    hostName, host, friend, queue.Count, knownCode.Count, choice);
                if (choice is Choice.Without or Choice.Cancel) {
                    failure = "";
                    Decline(choice, reference, returnTo);
                    yield break;
                }

                // (accepted from a trusted host: those code mods are accepted from it from now on)
                if (choice == Choice.Trust || IsTrusted(host)) {
                    Trust(host, knownCode);
                }

                line = "emp_ui_mods_fetching".Loc(0, queue.Count, "", 0);
                kill = EGui.CreatePopup(() => new(line), _ => !_busy).Kill;
            }

            // the code mods downloaded below, asked about before Elin restarts with them
            var fetchedCode = new List<(string Workshop, string Title, string Author, long Size)>();

            // before anything arrives: the player's own list hides it, a crash from here on leaves its game as it was
            Hide(queue.Select(m => Path.Combine(root, m.Workshop)).Concat(links ? queue.Select(m => LinkPath(m.Workshop)) : []));

            SteamCallback<DownloadItemResult_t>.Add(onResult);
            var started = Time.realtimeSinceStartup;
            for (var i = 0; i < queue.Count; i++) {
                var mod = queue[i];
                var number = ulong.Parse(mod.Workshop);
                var id = new PublishedFileId_t(number);
                if (!SteamUGC.DownloadItem(id, true)) {
                    failure = $"Steam did not start the download of {mod.Workshop}";
                    yield break;
                }

                var moved = Time.realtimeSinceStartup;
                ulong got = 0;
                while (true) {
                    var now = Time.realtimeSinceStartup;
                    var state = (EItemState)SteamUGC.GetItemState(id);
                    var waiting = (state & EItemState.k_EItemStateDownloadPending) != 0;
                    var coming = (state & EItemState.k_EItemStateDownloading) != 0;
                    var percent = 0UL;
                    if (results.TryGetValue(number, out var result) && result != EResult.k_EResultOK) {
                        failure = $"the download of {mod.Workshop} failed ({result})";
                        yield break;
                    }

                    if ((state & EItemState.k_EItemStateInstalled) != 0 && !waiting && !coming &&
                        (state & EItemState.k_EItemStateNeedsUpdate) == 0 &&
                        SteamUGC.GetItemInstallInfo(id, out _, out var folder, 1024, out _) && !string.IsNullOrEmpty(folder)) {
                        // what the list of another player names is a mod of Elin, or it is not loaded
                        if (!File.Exists(Path.Combine(folder, "package.xml"))) {
                            failure = $"{mod.Workshop} is not a mod of Elin";
                            yield break;
                        }

                        // security: a mod with code (a DLL) runs it on this PC when Elin starts. Only with the
                        // client setting FetchCodeMods (off); else it stays switched off and nothing is restarted
                        var code = HasCode(folder);
                        var item = details[number];
                        var title = ModListFile.Clean(item.m_rgchTitle ?? "");
                        EmpLog.Information("Fetched the Workshop item {Workshop} \"{Title}\" by {Author} ({Owner}) into {Folder}, code (DLL): {Code}",
                            mod.Workshop, title, Author(item.m_ulSteamIDOwner), item.m_ulSteamIDOwner, folder, code);
                        if (code && !EmpConfig.Client.FetchCodeMods.Value) {
                            failure = $"{mod.Workshop} has code (a DLL) and the setting FetchCodeMods is off";
                            told = "emp_ui_mods_code_refused".Loc(title.Length > 0 ? title : mod.Workshop);
                            yield break;
                        }

                        if (code) {
                            fetchedCode.Add((mod.Workshop, title, Author(item.m_ulSteamIDOwner), item.m_nFileSize));
                        }

                        folders[mod.Workshop] = folder;
                        break;
                    }

                    if (coming && SteamUGC.GetItemDownloadInfo(id, out var have, out var total)) {
                        percent = total > 0 ? have * 100 / total : 0;
                        // security: never more than the cap, whatever the Workshop said
                        if (total > MaxItemBytes || have > MaxItemBytes) {
                            failure = $"the download of {mod.Workshop} is bigger than {MaxItemBytes} bytes";
                            yield break;
                        }

                        if (have != got) {
                            got = have;
                            moved = now;
                        }
                    }

                    if ((!waiting && !coming && now - moved > IdleSeconds) || now - moved > QuietSeconds ||
                        now - started > TotalSeconds) {
                        failure = $"the download of {mod.Workshop} does not move";
                        yield break;
                    }

                    if (Input.GetKey(KeyCode.Escape) || EClass.core.IsGameStarted) {
                        failure = "cancelled by the player";
                        yield break;
                    }

                    line = "emp_ui_mods_fetching".Loc(i + 1, queue.Count, ModList.Name(mod), percent);
                    yield return null;
                }
            }

            // the code of these runs at the restart: asked about now that the files say it (nothing of them is
            // loaded yet, they are switched off in the player's list), unless the trusted host had them accepted
            if (fetchedCode.Count > 0 && !(IsTrusted(host) && fetchedCode.All(c => Approved(host).Contains(c.Workshop)))) {
                kill();
                kill = () => { };
                var text = new System.Text.StringBuilder();
                text.AppendLine((friend ? "emp_ui_mods_ask_code_found" : "emp_ui_mods_ask_code_found_stranger").Loc(hostName));
                text.AppendLine();
                foreach (var c in fetchedCode) {
                    text.AppendLine("  " + Row(c.Title.Length > 0 ? c.Title : c.Workshop, c.Author, c.Size) + "  " +
                                    "emp_ui_mods_ask_has_code".lang().TagColor(_colorCode));
                }

                text.AppendLine();
                text.Append((friend ? "emp_ui_mods_ask_warning".lang() : "emp_ui_mods_ask_warning".lang().TagColor(_colorCode)));
                var choice = Choice.None;
                Ask(text.ToString(), friend, false, "emp_ui_mods_ask_restart", c => choice = c);
                while (choice == Choice.None) {
                    yield return null;
                }

                EmpLog.Information("Code mods {Mods} downloaded for the host {Host} ({HostId}, friend {Friend}): the player chose {Choice}",
                    string.Join(", ", fetchedCode.Select(c => c.Workshop)), hostName, host, friend, choice);
                if (choice is Choice.Without or Choice.Cancel) {
                    failure = "";
                    Decline(choice, reference, returnTo);
                    yield break;
                }

                if (IsTrusted(host)) {
                    Trust(host, fetchedCode.Select(c => c.Workshop));
                }

                line = "emp_ui_mods_fetching".Loc(queue.Count, queue.Count, "", 100);
                kill = EGui.CreatePopup(() => new(line), _ => !_busy).Kill;
            }

            // (Elin is about to close: never under a game the player started meanwhile)
            // client setting KeepMods: the player keeps the mods of this game. Its Steam account subscribes to
            // them and they are switched on in its own list, so the next start of Elin has them and joining this
            // game again needs no restart (without it they are fetched, hidden, and Elin restarts at every start)
            var keep = new List<string>();
            if (EmpConfig.Client.KeepMods.Value && !EClass.core.IsGameStarted) {
                foreach (var mod in diff.Missing) {
                    var folder = folders.TryGetValue(mod.Workshop, out var fetched)
                        ? fetched
                        : packages.FirstOrDefault(p => ModList.WorkshopOf(p) == mod.Workshop && p.dirInfo is not null && !IsLink(p))?.dirInfo.FullName;
                    if (folder is null) {
                        continue;
                    }

                    keep.Add(folder);
                    SteamUGC.SubscribeItem(new PublishedFileId_t(ulong.Parse(mod.Workshop)));
                    EmpLog.Information("Keeping the mod {Workshop} of the game: subscribed, on in the player's own list", mod.Workshop);
                }
            }

            failure = EClass.core.IsGameStarted ? "the player started a game" :
                Apply(reference, returnTo, folders, links, keep) ?? "";
        } finally {
            SteamCallback<DownloadItemResult_t>.Remove(onResult);
            if (failure.Length > 0) {
                GiveUp(failure, returnTo, told);
            }

            kill();
        }

        yield return EmpMod.Instance.StartCoroutine(Restart());
    }

    /// <summary>
    ///     The list of the session is written: Elin closes, and starts again by itself where it can
    /// </summary>
    private static IEnumerator Restart()
    {
        if (!_applied) {
            yield break;
        }

        var restarts = Relaunch();
        EmpPop.Block((restarts ? "emp_ui_mods_restart" : "emp_ui_mods_restart_manual").lang());
        yield return new WaitForSecondsRealtime(restarts ? 3f : 8f);
        if (EClass.core.IsGameStarted) {
            // the player started a game meanwhile: it goes on, and its own list is back
            _applied = false;
            try {
                File.Delete(Ticket);
                Restore();
                RemoveLinks();
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Could not undo the mod list of the session");
            }

            yield break;
        }

        Application.Quit();
    }

    /// <summary>
    ///     Nothing was changed but Steam's own folder (hidden in the player's list): said, and the game is joined
    ///     as without this, where the message that names the mods is shown if it is refused
    /// </summary>
    private static void GiveUp(string why, string returnTo, string told)
    {
        _busy = false;
        _done = true;
        EmpLog.Warning("The mods of the game were not fetched: {Why}", why);
        EmpPop.Block(told);
        EClass.core.actionsNextFrame.Add(() => {
            if (!EClass.core.IsGameStarted) {
                Return(returnTo);
            }
        });
    }

    private enum Choice
    {
        None,
        Accept,
        Trust,
        Without,
        Cancel,
    }

    private static readonly Color _colorCode = new(0.6f, 0.05f, 0.05f);
    private const int AskRows = 12;

    // the mods the player chose to join without: not asked about again in this run (the join goes on as when
    // the setting FetchMods is off, where the host's own rules say whether a game with other mods is allowed)
    private static string? _declined;

    /// <summary>
    ///     The game's own box, never answered by a timer. Closed any other way (Escape, a click outside) is
    ///     Cancel. For a host that is not a Steam friend, Cancel comes first (where the selection starts)
    /// </summary>
    private static void Ask(string text, bool friend, bool canTrust, string accept, Action<Choice> chose)
    {
        var picked = Choice.None;
        var buttons = new List<(string Label, Choice Choice)> { (accept.lang(), Choice.Accept) };
        if (canTrust) {
            buttons.Add(("emp_ui_mods_ask_trust".lang(), Choice.Trust));
        }

        buttons.Add(("emp_ui_mods_ask_without".lang(), Choice.Without));
        buttons.Add(("emp_ui_mods_ask_cancel".lang(), Choice.Cancel));
        if (!friend) {
            buttons.Reverse();
        }

        var box = Dialog.Choice(text, d => {
            foreach (var (label, choice) in buttons) {
                d.AddButton(label, () => picked = choice);
            }
        });
        box.SetOnKill(() => chose(picked == Choice.None ? Choice.Cancel : picked));
    }

    private static string AskText(string hostName, bool friend, List<Mod> queue, List<Mod> onDisk, List<string> knownCode,
        Dictionary<ulong, SteamUGCDetails_t> details)
    {
        var code = EmpConfig.Client.FetchCodeMods.Value;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(friend ? "emp_ui_mods_ask_friend".Loc(hostName) : "emp_ui_mods_ask_stranger".Loc(hostName).TagColor(_colorCode));
        sb.AppendLine();
        sb.AppendLine((friend ? "emp_ui_mods_ask_warning".lang() : "emp_ui_mods_ask_warning".lang().TagColor(_colorCode)));
        sb.AppendLine();

        var rows = new List<string>();
        foreach (var mod in queue) {
            var item = details[ulong.Parse(mod.Workshop)];
            var title = ModListFile.Clean(item.m_rgchTitle ?? "");
            rows.Add(Row(title.Length > 0 ? title : ModList.Name(mod), Author(item.m_ulSteamIDOwner), item.m_nFileSize) + "  " +
                     (code ? "emp_ui_mods_ask_code_later" : "emp_ui_mods_ask_code_later_blocked").lang().TagColor(Color.gray));
        }

        foreach (var mod in onDisk) {
            var has = knownCode.Contains(mod.Workshop);
            rows.Add(ModList.Name(mod) + "  " + "emp_ui_mods_ask_on_pc".lang().TagColor(Color.gray) +
                     (has ? "  " + (code ? "emp_ui_mods_ask_has_code" : "emp_ui_mods_ask_has_code_blocked").lang().TagColor(_colorCode) : ""));
        }

        foreach (var row in rows.Take(AskRows)) {
            sb.Append("  ").AppendLine(row);
        }

        if (rows.Count > AskRows) {
            sb.Append("  ... ").AppendLine("emp_ui_source_mismatch_more".Loc(rows.Count - AskRows).TagColor(Color.gray));
        }

        if (rows.Count == 0) {
            sb.AppendLine("emp_ui_mods_ask_nothing".lang());
        }

        sb.AppendLine();
        sb.Append((code ? "emp_ui_mods_ask_code_on" : "emp_ui_mods_ask_code_off").lang());
        return sb.ToString();
    }

    private static string Row(string title, string author, long size)
    {
        return "emp_ui_mods_ask_row".Loc(title, author.Length > 0 ? author : "?",$"{size / 1048576.0:0.0}");
    }

    /// <summary>
    ///     Join without them: the game is joined again as without this (once, the host's rules decide).
    ///     Cancel: nothing more, the player is where it was
    /// </summary>
    private static void Decline(Choice choice, string reference, string returnTo)
    {
        _busy = false;
        if (choice != Choice.Without) {
            return;
        }

        _declined = reference;
        EClass.core.actionsNextFrame.Add(() => {
            if (!EClass.core.IsGameStarted) {
                Return(returnTo);
            }
        });
    }

    private static string HostName(ulong host, string given)
    {
        given = ModListFile.Clean(given ?? "");
        return given.Length > 0 ? given : host != 0 && Author(host) is { Length: > 0 } name ? name : host.ToString();
    }

    private static bool IsFriend(ulong host)
    {
        try {
            return host != 0 && SteamFriends.GetFriendRelationship(new(host)) == EFriendRelationship.k_EFriendRelationshipFriend;
        } catch (Exception) {
            return false;
        }
    }

    // client setting TrustedHosts: "steamid:codemod,codemod;steamid:"
    private static Dictionary<ulong, HashSet<string>> TrustedHosts()
    {
        var hosts = new Dictionary<ulong, HashSet<string>>();
        foreach (var entry in (EmpConfig.Client.TrustedHosts.Value ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries)) {
            var parts = entry.Split([':'], 2);
            if (ulong.TryParse(parts[0].Trim(), out var id) && id != 0) {
                hosts[id] = new(parts.ElementAtOrDefault(1)?.Split([','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()).Where(IsWorkshopId) ?? []);
            }
        }

        return hosts;
    }

    private static bool IsTrusted(ulong host) => host != 0 && TrustedHosts().ContainsKey(host);

    private static HashSet<string> Approved(ulong host) => TrustedHosts().TryGetValue(host, out var code) ? code : [];

    private static void Trust(ulong host, IEnumerable<string> code)
    {
        if (host == 0) {
            return;
        }

        var hosts = TrustedHosts();
        if (!hosts.TryGetValue(host, out var approved)) {
            hosts[host] = approved = [];
            EmpLog.Information("The host {HostId} is trusted from now on: its mods are fetched without asking, but new code mods", host);
        }

        approved.UnionWith(code);
        EmpConfig.Client.TrustedHosts.Value = string.Join(";", hosts.Select(h => $"{h.Key}:{string.Join(",", h.Value)}"));
    }

    /// <returns>null when the list of the session is written, else why not (and everything is as it was)</returns>
    private static string? Apply(string reference, string returnTo, Dictionary<string, string> folders, bool links, List<string> keep)
    {
        try {
            // (where Steam really put them, should it not be where it was expected)
            Hide(folders.Values);
            // before the player's list is kept aside: what it keeps is on in it
            Show(keep);

            if (!File.Exists(LoadOrder)) {
                File.WriteAllText(LoadOrder, "");
            } else if (File.ReadAllLines(LoadOrder).FirstOrDefault() == Marker) {
                throw new IOException("the list of an earlier session is still in place");
            }

            File.Copy(LoadOrder, PlayerList, true);

            if (links) {
                foreach (var (workshop, folder) in folders) {
                    if (!Link(LinkPath(workshop), folder)) {
                        throw new IOException("no link for " + workshop);
                    }
                }
            }

            Write(LoadOrder, SessionList(ModListFile.Parse(reference), reference.Contains(ModList.LookOwn)));
            // last: a ticket without the list of the session would bring the player back without its mods
            Directory.CreateDirectory(Path.GetDirectoryName(Ticket)!);
            File.WriteAllText(Ticket, $"{DateTime.UtcNow.Ticks}\n{returnTo}\n{reference}");
            _applied = true;
            return null;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not write the mod list of the session");
            try {
                File.Delete(Ticket);
                Restore();
                RemoveLinks();
            } catch (Exception undo) {
                // the next start does the same (Boot)
                EmpLog.Warning(undo, "Could not undo the mod list of the session");
            }

            return ex.Message;
        }
    }

    /// <summary>
    ///     loadorder.txt for one session: every mod the game knows, in the player's order, on when the reference
    ///     has it. This mod and what it runs on stay as the player has them, and the mods that only change the look
    ///     when the game says each player keeps their own. What was fetched is not listed: a mod that is not
    ///     listed is on
    /// </summary>
    internal static List<string> SessionList(List<Mod> reference, bool ownLooks)
    {
        var packages = EClass.core.mods.packages
            .Where(p => !p.builtin && p.dirInfo is not null && !IsLink(p) && ModLoadOrderPreset.IsValidId(p.id))
            .ToList();
        var workshops = new HashSet<string>(reference.Where(m => m.Workshop.Length > 0).Select(m => m.Workshop));
        var ids = new HashSet<string>(reference.Where(m => m.Workshop.Length == 0 && m.Id.Length > 0).Select(m => BaseModPackage.NormalizeId(m.Id)));

        // never switched off: this mod, the mods whose libraries it is built on (those that come neither with the
        // game nor with this mod: YK Framework), and what those declare they need
        var own = Path.GetDirectoryName(EmpMod.Assembly.Location) ?? "";
        var needed = EmpMod.Assembly.GetReferencedAssemblies()
            .Select(a => a.Name + ".dll")
            .Where(dll => !File.Exists(Path.Combine(Application.dataPath, "Managed", dll)) &&
                          !File.Exists(Path.Combine(BepInEx.Paths.BepInExAssemblyDirectory, dll)) &&
                          !File.Exists(Path.Combine(own, dll)))
            .ToList();
        // nor the mod that brings the language the player reads the game in (French...): switched off, the game
        // fell back to the English texts but still looked for that language's files, and every talk to a
        // character threw (DirectoryNotFoundException on Lang/EN/Dialog/dialog.xlsx, a real session, 0.26.597)
        var lang = global::Lang.langCode ?? "";
        var speaks = lang.Length == 0 || global::Lang.IsBuiltin(lang)
            ? []
            : packages.Where(p => Directory.Exists(Path.Combine(p.dirInfo.FullName, "Lang", lang))).ToList();
        var kept = new HashSet<string>();
        var todo = new Stack<string?>(packages
            .Where(p => needed.Any(dll => File.Exists(Path.Combine(p.dirInfo.FullName, dll))))
            .Concat(speaks)
            .Select(p => BaseModPackage.NormalizeId(p.id))
            .Append(ModInfo.Guid));
        while (todo.Count > 0) {
            if (todo.Pop() is not { } id || !kept.Add(id)) {
                continue;
            }

            foreach (var needs in packages.Where(p => BaseModPackage.NormalizeId(p.id) == id).SelectMany(p => p.dependency ?? [])) {
                foreach (var other in needs ?? []) {
                    todo.Push(BaseModPackage.NormalizeId(other));
                }
            }
        }

        var lines = new List<string> { Marker };
        foreach (var package in packages) {
            var id = BaseModPackage.NormalizeId(package.id);
            var workshop = ModList.WorkshopOf(package);
            var on = kept.Contains(id) || (ownLooks && ModList.OnlyLook(package)) ||
                     ClientOnlyMods.Lists(ClientOnly, package.id, workshop) ? package.willActivate :
                workshop.Length > 0 ? workshops.Contains(workshop) : ids.Contains(id);
            lines.Add($"{package.dirInfo.FullName},{(on ? 1 : 0)},{package.id.Trim()}");
        }

        return lines;
    }

    /// <summary>
    ///     The player's own list gets a line "folder,0" for each of these folders that it does not name yet (and
    ///     for those hidden earlier in this run): what is fetched never loads in a game that is not the session
    /// </summary>
    private static void Hide(IEnumerable<string> folders)
    {
        _hidden.AddRange(folders.Select(Path.GetFullPath).Where(f => !_hidden.Contains(f, StringComparer.OrdinalIgnoreCase)));
        var lines = File.Exists(LoadOrder) ? File.ReadAllLines(LoadOrder).ToList() : [];
        var count = lines.Count;
        foreach (var folder in _hidden) {
            var start = folder.Replace('\\', '/') + ",";
            if (!lines.Any(l => l.Replace('\\', '/').StartsWith(start, StringComparison.OrdinalIgnoreCase))) {
                lines.Add(folder + ",0");
            }
        }

        if (lines.Count > count) {
            Write(LoadOrder, lines);
        }
    }

    /// <summary>
    ///     The player's own list has these folders switched on, and they are no longer hidden at the exit: the
    ///     mods of a game the player chose to keep (KeepMods)
    /// </summary>
    private static void Show(List<string> folders)
    {
        if (folders.Count == 0) {
            return;
        }

        var kept = folders.Select(Path.GetFullPath).ToList();
        _hidden.RemoveAll(h => kept.Contains(h, StringComparer.OrdinalIgnoreCase));
        var lines = File.Exists(LoadOrder) ? File.ReadAllLines(LoadOrder).ToList() : [];
        foreach (var folder in kept) {
            var start = folder.Replace('\\', '/') + ",";
            var at = lines.FindIndex(l => l.Replace('\\', '/').StartsWith(start, StringComparison.OrdinalIgnoreCase));
            if (at < 0) {
                lines.Add(folder + ",1");
                continue;
            }

            // "folder,0" or "folder,0,id": only the switch changes
            var rest = lines[at].Substring(start.Length);
            var comma = rest.IndexOf(',');
            lines[at] = folder + ",1" + (comma < 0 ? "" : rest.Substring(comma));
        }

        Write(LoadOrder, lines);
    }

    /// <summary>
    ///     The player's list, kept aside, back in its place. False: none was kept
    /// </summary>
    private static bool Restore()
    {
        if (!File.Exists(PlayerList)) {
            return false;
        }

        File.Copy(PlayerList, LoadOrder, true);
        File.Delete(PlayerList);
        EmpLog.Information("The player's own mod list is back in loadorder.txt");
        return true;
    }

    // beside the file, then over it: a write cut short leaves the list as it was
    private static void Write(string file, List<string> lines)
    {
        File.WriteAllLines(file + ".tmp", lines);
        if (File.Exists(file)) {
            File.Replace(file + ".tmp", file, null);
        } else {
            File.Move(file + ".tmp", file);
        }
    }

    private static string WorkshopRoot()
    {
        return EClass.core.mods.dirWorkshop?.FullName ??
               Path.GetFullPath(Path.Combine(CorePath.rootExe, "../../workshop/content/" + App));
    }

    private static string LinkPath(string workshop)
    {
        // security: the name of the link is only ever "EmpSession_" and digits, so it stays in Package/
        if (!IsWorkshopId(workshop)) {
            throw new IOException("not a Workshop number: " + workshop);
        }

        return Path.GetFullPath(Path.Combine(BaseModManager.rootMod, ModList.SessionFolder + workshop));
    }

    /// <summary>
    ///     A directory junction in Package/ to a folder of the Workshop: the game loads it as a local mod. No
    ///     right is needed to make one, and nothing is copied
    /// </summary>
    private static bool Link(string link, string target)
    {
        var there = Path.Combine(link, "package.xml");
        if (File.Exists(there)) {
            return true;
        }

        // (the folder is Steam's own: a quote in it would end the argument of the command)
        if (link.Contains('"') || target.Contains('"')) {
            return false;
        }

        Unlink(link);
        Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")?.WaitForExit(10000);
        return File.Exists(there);
    }

    /// <summary>
    ///     A Workshop number as the host may name one: digits only, not zero (a folder name and a command
    ///     argument are made from it)
    /// </summary>
    internal static bool IsWorkshopId(string? workshop)
    {
        return workshop is { Length: > 0 and <= 20 } && workshop.All(c => c is >= '0' and <= '9') &&
               ulong.TryParse(workshop, out var id) && id != 0;
    }

    /// <summary>
    ///     Why the Workshop item may not be fetched, null when it may: an item of Elin (its consumer app), a
    ///     mod (not a collection, a guide, a screenshot...), not banned, not over the size cap
    /// </summary>
    private static string? Refused(SteamUGCDetails_t item)
    {
        return item.m_eResult != EResult.k_EResultOK ? $"not on the Workshop ({item.m_eResult})" :
            item.m_nConsumerAppID.m_AppId.ToString() != App ? $"an item of another game ({item.m_nConsumerAppID.m_AppId})" :
            item.m_bBanned ? "banned on the Workshop" :
            item.m_eFileType != EWorkshopFileType.k_EWorkshopFileTypeCommunity ? $"not a mod ({item.m_eFileType})" :
            item.m_nFileSize < 0 || item.m_nFileSize > MaxItemBytes ? $"too big ({item.m_nFileSize} bytes)" :
            null;
    }

    // the name the author goes by on Steam, when Steam knows it already (else its Steam id says who)
    private static string Author(ulong owner)
    {
        try {
            return ModListFile.Clean(SteamFriends.GetFriendPersonaName(new(owner)) ?? "");
        } catch (Exception) {
            return "";
        }
    }

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".dll", ".cs", ".csx", ".lua", ".py", ".js", ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".sh", ".jar"
    };

    /// <summary>
    ///     The mod has code the game or a loader would run: a DLL, a source file the game compiles (.cs), a script
    ///     another loader runs (.lua, .py, .js, .csx) or a program (.exe, .bat, .cmd, .ps1, .vbs), anywhere in its
    ///     folder. In doubt (unreadable), it has
    /// </summary>
    private static bool HasCode(string folder)
    {
        try {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(f => CodeExtensions.Contains(Path.GetExtension(f)));
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not read the files of {Folder}", folder);
            return true;
        }
    }

    private static bool IsLink(BaseModPackage package)
    {
        return package.dirInfo.Name.StartsWith(ModList.SessionFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveLinks()
    {
        foreach (var link in Directory.GetDirectories(BaseModManager.rootMod, ModList.SessionFolder + "*")) {
            Unlink(link);
        }
    }

    /// <summary>
    ///     Only ever a link, and only the link: a real folder of that name is not ours, and deleting without
    ///     going into it cannot touch what the link points to
    /// </summary>
    private static void Unlink(string link)
    {
        try {
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) {
                Directory.Delete(link, false);
                EmpLog.Information("Removed the link {Link} of a mod fetched for a session", link);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // not there, or held: it is switched off in the player's list, and the next start tries again
        }
    }

    /// <summary>
    ///     Elin allows one instance, and a player's install only starts through Steam: something small waits for
    ///     this game to be gone, then asks Steam for it. False: the player starts Elin again (the ticket is there)
    /// </summary>
    private static bool Relaunch()
    {
        // a copy that starts without Steam (the bench, a developer's): Steam would start another Elin than this one
        if (File.Exists(CorePath.rootExe + "steam_appid.txt")) {
            return false;
        }

        var pid = Process.GetCurrentProcess().Id;
        return Run("cmd.exe",
            $"/c for /l %i in (1,1,120) do @(tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && ping -n 2 127.0.0.1 >nul || " +
            $"(ping -n 3 127.0.0.1 >nul & start \"\" \"steam://rungameid/{App}\" & exit))") is not null;
    }

    private static Process? Run(string exe, string args)
    {
        try {
            var start = new ProcessStartInfo(exe, args) {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // the mod loader marks this process as done, a game inheriting the mark starts without any mod
            foreach (var name in start.EnvironmentVariables.Keys.Cast<string>().ToArray()) {
                if (name.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase)) {
                    start.EnvironmentVariables.Remove(name);
                }
            }

            return Process.Start(start);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not start {Exe}", exe);
            return null;
        }
    }

    private static void Return(string where)
    {
        EmpLog.Information("Coming back to the game by itself: {Where}", where);
        var words = where.Split([' '], 2);
        var session = NetSession.Instance;
        try {
            switch (words[0]) {
                case "depot" when SaveDepot.Enabled:
                    SaveDepot.Take();
                    break;
                case "lobby" when words.Length > 1 && ulong.TryParse(words[1], out var lobby) && lobby != 0:
                    session.Lobby.ConnectLobby(lobby);
                    break;
                case "address" when words.Length > 1:
                    session.InitializeComponent<ElinNetClient>().ConnectAddress(words[1]);
                    break;
#if DEBUG
                case "port" when words.Length > 1 && ushort.TryParse(words[1], out var port):
                    session.InitializeComponent<ElinNetClient>().ConnectLocalPort(port);
                    break;
#endif
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not come back to the game by itself");
        }
    }

    // prefix of ModManager.SaveLoadOrder, in a session only (Boot)
    private static bool KeepPlayerList()
    {
        EmpPop.Block("emp_ui_mods_session_locked".lang());
        return false;
    }
}
