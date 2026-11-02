using System;
using System.Diagnostics;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using UnityEngine;

namespace ElinTogether;

/// <summary>
///     What a player who hosts no longer does by hand: the session opens with the world, and the world is saved
///     every 2 minutes while someone else plays in it, so that a crash of the host costs everyone 2 minutes at
///     most. Not for a server (-empserver): EmpServer opens and saves by itself
/// </summary>
internal static class EmpAutoHost
{
    private const float SaveSeconds = 120f;
    private const float SlowSaveSeconds = 300f;
    private const float RetrySeconds = 10f;
    private const long SlowSaveMs = 500;
    private const float BackupSeconds = 3600f;
    private const float ForceSeconds = 30f;
    private const float RegularForceSeconds = 120f;

    private static float _nextSave;
    private static float _forceAt;
    private static bool _guests;
    private static bool _owed;
    private static bool _slow;
    private static string? _backupId;
    private static float _nextBackup;

#if DEBUG
    // the windows of the bench (-empmute) open their session themselves, on the local port a second window can
    // join: nothing opens by itself there unless the bench asks (emp.auto_open 1), and then on that port
    private static readonly bool _bench = Array.IndexOf(Environment.GetCommandLineArgs(), "-empmute") >= 0;
    private static float _benchSeconds;

    internal static bool BenchOpens { get; set; }

    internal static void SaveEvery(float seconds)
    {
        _benchSeconds = seconds;
        _nextSave = Time.unscaledTime + Interval;
    }
#endif

    private static float Interval =>
#if DEBUG
        _benchSeconds > 0f ? _benchSeconds :
#endif
        _slow ? SlowSaveSeconds : SaveSeconds;

    /// <summary>
    ///     One frame after a world is loaded, once the local character is the right one (see
    ///     ElinNetHost.RemoveLeftOverCharas): what "Start Server" does, without the click
    /// </summary>
    internal static void OpenSession()
    {
        // the save "Host a game" loaded at the title screen: opened whatever the boxes say, as "Start Server" would.
        // Another world loaded since forgets the ask
        if (HostAfterLoad is { } asked) {
            HostAfterLoad = null;
            if (asked == Game.id) {
                OpenAsked();
                return;
            }
        }

        var local = false;
#if DEBUG
        if (_bench) {
            if (!BenchOpens) {
                return;
            }

            local = true;
        }
#endif

        if (!EmpConfig.Server.AutoHost.Value || NetSession.Instance.Transport is not null) {
            return;
        }

        // StartServer refuses these with a window: nobody clicked, so only the log says it
        if (!EClass.core.IsGameStarted || EClass.scene.mode == Scene.Mode.Title ||
            EClass.player?.chara?.homeBranch?.owner is null) {
            EmpLog.Information("Session not opened by itself: no game or no land claimed");
            return;
        }

        // a world nobody else plays in stays a solo game: a session changes rules of the game (no pause in menus,
        // the turns of a fight) that a player alone never asked for. The first session of a world is opened by hand,
        // unless the world was taken from a depot: that is asking to play it with others
        if (!ElinNetHost.IsSharedWorld && !SaveDepot.Taken) {
            return;
        }

        // another player took this world over since it was last hosted here: its game is the one that goes on
        if (WorldHandover.TryJoinNewerLocal()) {
            return;
        }

        // (with the box "hidden server", a hidden game hosts it and this one joins)
        // (a peer-hosted world, PeerHosting: always, the bench's local port too, never the classic way)
        if ((!local || PeerHosting.Active) && HiddenServer.TryStart(Open)) {
            return;
        }

        Open();

        void Open()
        {
            try {
                NetSession.Instance.InitializeComponent<ElinNetHost>().StartServer(local, true);
            } catch (Exception ex) {
                EmpLog.Warning(ex, "The session could not be opened by itself");
                // StartServer may throw half way (Steam away): no host component left behind
                NetSession.Instance.ResetSession();
            }
        }
    }

    /// <summary>
    ///     The id of the save the title screen's "Host a game" is loading (TabPlay.StartHosting): opened to
    ///     multiplayer once it is loaded and the right character is in place
    /// </summary>
    internal static string? HostAfterLoad { get; set; }

    /// <summary>
    ///     The title's "New world" (TabPlay.StartNewWorld) opened the game's new game: the world it starts is opened to
    ///     multiplayer once its land is claimed (#56). Forgotten by the title's own New Game or Continue (NewWorldPatch)
    /// </summary>
    internal static bool NewWorldAsked { get; set; }

    /// <summary>
    ///     The id of the new world "New world" made: opened to multiplayer as soon as the player's land is claimed (the
    ///     game's own claim, Ashland's or the survival start's), since a session needs a home base. Never stored
    /// </summary>
    internal static string? HostAfterClaim { get; private set; }

    private static bool _claimTold;

    /// <summary>
    ///     Game.StartNewGame has run (NewWorldPatch)
    /// </summary>
    internal static void OnNewGame()
    {
        if (!NewWorldAsked) {
            return;
        }

        NewWorldAsked = false;
        if (NetSession.Instance.Transport is not null) {
            return;
        }

        HostAfterClaim = Game.id;
        _claimTold = false;
        EmpLog.Information("New world {Id}: opened to multiplayer once its land is claimed", Game.id);
    }

    private static void UpdateHostAfterClaim()
    {
        if (HostAfterClaim is not { } id || !EClass.core.IsGameStarted || EClass.scene.mode != Scene.Mode.Zone) {
            return;
        }

        // another world loaded since, or a session already open: nothing left to open
        if (Game.id != id || NetSession.Instance.Transport is not null) {
            HostAfterClaim = null;
            return;
        }

        // (not in the middle of the claim scene or any window)
        if (!SafeToSave() || EClass.ui.IsActive) {
            return;
        }

        if (EClass.player?.chara?.homeBranch?.owner is null) {
            if (!_claimTold) {
                _claimTold = true;
                EmpPop.Pop("emp_ui_new_world_wait".lang());
            }

            return;
        }

        HostAfterClaim = null;
        EmpLog.Information("New world {Id}: land claimed, opened to multiplayer", id);
        OpenAsked();
    }

    /// <summary>
    ///     What the in-game "Start Server" does (TabPlay.StartServerFromPanel), without the click; why it could not is
    ///     said in a window and the game stays loaded, played alone
    /// </summary>
    private static void OpenAsked()
    {
        if (NetSession.Instance.Transport is not null || !EClass.core.IsGameStarted || EClass.scene.mode == Scene.Mode.Title) {
            return;
        }

        if (EClass.player?.chara?.homeBranch?.owner is null) {
            Failed("emp_ui_unclaimed_zone".lang());
            return;
        }

        // another player took this world over since it was last hosted here: its game is the one that goes on
        if (WorldHandover.TryJoinNewerLocal()) {
            return;
        }

        // (with the box "hidden server", a hidden game hosts it and this one joins)
        if (HiddenServer.TryStart(Open)) {
            return;
        }

        Open();

        static void Open()
        {
            try {
                NetSession.Instance.InitializeComponent<ElinNetHost>().StartServer();
            } catch (Exception ex) {
                EmpLog.Warning(ex, "The chosen game could not be opened to multiplayer");
                NetSession.Instance.ResetSession();
                Failed(ex.Message);
                return;
            }

            if (NetSession.Instance.Transport is ElinNetHost) {
                EmpPop.Pop("emp_ui_host_open".lang());
            } else {
                Failed("emp_ui_host_failed_log".lang());
            }
        }

        static void Failed(string why) => EmpPop.Block("emp_ui_host_failed".Loc(why));
    }

    // why a save asked by a network message waits for the game to be able to save, see SaveFromMessage
    private static string? _saveWhenSafe;

    /// <summary>
    ///     A save a network message needs made where nothing saves by itself (a player came back or dropped with
    ///     the box unticked, a dedicated server): made in that frame when the game may save, as soon as it may
    ///     otherwise. It was made whatever the game was doing (a map loading, the host changing map) and a save the
    ///     game could not make went unsaid (reliability audit 2026-10-27, #10)
    /// </summary>
    internal static void SaveFromMessage(string why)
    {
        if (!SafeToSave()) {
            _saveWhenSafe = why;
            EmpLog.Information("Save ({Why}) put off: the game cannot save right now", why);
            return;
        }

        _saveWhenSafe = null;
        if (!EClass.game.Save(isAutoSave: true)) {
            EmpLog.Warning("The game could not save the world ({Why}), see Player.log", why);
        }
    }

    private static void UpdateSaveWhenSafe()
    {
        if (_saveWhenSafe is not { } why) {
            return;
        }

        // the session or the game is gone: nothing to save for it
        if (!EClass.core.IsGameStarted || NetSession.Instance.Transport is not ElinNetHost) {
            _saveWhenSafe = null;
            return;
        }

        if (SafeToSave()) {
            SaveFromMessage(why);
        }
    }

    /// <summary>
    ///     Called every frame
    /// </summary>
    internal static void Update()
    {
        UpdateSaveWhenSafe();
        UpdateHostAfterClaim();

#if DEBUG
        // a window of the bench saves by itself only once its test asked (emp.autosave_every): the suites written
        // before this count on a world that is not saved behind their back
        if (_bench && _benchSeconds <= 0f) {
            return;
        }
#endif

        var session = NetSession.Instance;
        if (EmpServer.Requested || session.Transport is not ElinNetHost || !EClass.core.IsGameStarted ||
            !EmpConfig.Server.AutoSave.Value) {
            _guests = _owed = false;
            _forceAt = 0f;
            return;
        }

        var now = Time.unscaledTime;
        var guests = session.CurrentPlayers.Count > 1;
        if (guests != _guests) {
            // the first player in starts the clock; the last one out is saved at once: what it did is in the world.
            // A save asked for by RequestSave survives the first player in
            _guests = guests;
            if (!guests) {
                _owed = true;
                _nextSave = now;
            } else if (!_owed) {
                _nextSave = now + Interval;
            }
        }

        if ((!guests && !_owed) || now < _nextSave) {
            return;
        }

        // a save asked for by RequestSave is not put off for good by a menu or a fight: it was never asked
        // (never while a map loads or the host changes zone, however long it was put off)
        if (!SafeToSave() || (!CanSave() && (_forceAt <= 0f || now < _forceAt))) {
            // the regular save too, put off by a menu left open, fishing or a monster that keeps the host in sight:
            // made anyway a while later (reliability audit 2026-10-27: an hour of everyone's play went unsaved)
            if (SafeToSave() && _forceAt <= 0f) {
                _forceAt = now + RegularForceSeconds;
            }

            _nextSave = now + RetrySeconds;
            return;
        }

        _owed = false;
        _forceAt = 0f;
        Save();
        _nextSave = Time.unscaledTime + Interval;
    }

    /// <summary>
    ///     A player came back to the host's map: its character and the map it held are in the world now, and the world
    ///     is saved soon, once for all the players who come back in a row, instead of in the frame of each return.
    ///     False when nothing saves by itself here (box unticked, dedicated server, bench window that did not ask):
    ///     the caller saves then
    /// </summary>
    internal static bool RequestSave(float within = 5f)
    {
#if DEBUG
        if (_bench && _benchSeconds <= 0f) {
            return false;
        }
#endif

        if (EmpServer.Requested || !EmpConfig.Server.AutoSave.Value) {
            return false;
        }

        var now = Time.unscaledTime;
        _owed = true;
        _nextSave = _nextSave > now ? Mathf.Min(_nextSave, now + within) : now + within;
        if (_forceAt <= 0f) {
            _forceAt = now + ForceSeconds;
        }

        return true;
    }

    /// <summary>
    ///     Where the game's own quick save key would save, and nothing the save would take from the player's hands
    /// </summary>
    internal static bool SafeToSave()
    {
        return EClass.scene.mode == Scene.Mode.Zone && !EClass.game.isLoading && EClass.pc is { IsInActiveZone: true };
    }

    private static bool CanSave()
    {
        var pc = EClass.pc;
        return SafeToSave()
               && pc is { isDead: false, HasNoGoal: true }
               && ActionMode.AdvOrRegion.IsActive
               // a dialog, a menu, the end screen; Game.Save also cancels a drag in progress
               && !EClass.ui.IsActive
               && !EClass.ui.IsDragging
               // ponytail: "in a fight" is "something here is after us", a lingering target would hold the save for good
               && !EClass._map.charas.Exists(c => c.enemy == pc && !c.isDead);
    }

    private static void Save()
    {
        var game = EClass.game;
        // these saves overwrite the only folder of the world: the save as it is on disk goes to the game's backups
        // first, once per world and then once an hour (the game's own backup timer, 4 hours unless changed, or
        // off, still runs on top). Never one per save
        if (Game.id != _backupId || Time.unscaledTime >= _nextBackup) {
            _backupId = Game.id;
            _nextBackup = Time.unscaledTime + BackupSeconds;
            try {
                GameIO.MakeBackup(new GameIndex { id = Game.id, cloud = game.isCloud });
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Autosave: no backup of the save before it");
            }
        }

        var watch = Stopwatch.StartNew();
        var saved = SaveWriter.Save(game);
        watch.Stop();
        if (saved) {
            EmpLog.Information("Autosave: saved in {Ms} ms", watch.ElapsedMilliseconds);
        } else {
            EmpLog.Warning("Autosave: the game could not save the world ({Ms} ms), see Player.log", watch.ElapsedMilliseconds);
        }

        if (saved) {
            // every guest keeps a copy of the world as it was just saved
            (NetSession.Instance.Transport as ElinNetHost)?.WorldSaved();
        }

        // the save runs in the game and freezes it meanwhile: a heavy world is saved less often
        if (watch.ElapsedMilliseconds > SlowSaveMs && !_slow) {
            _slow = true;
            EmpLog.Information("Autosave: over {Limit} ms, every {Seconds} s from now on", SlowSaveMs, SlowSaveSeconds);
        }
    }
}
