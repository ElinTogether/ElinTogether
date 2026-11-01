using System;
using System.IO;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using UnityEngine;

namespace ElinTogether;

/// <summary>
///     A game started with `-empserver &lt;save id&gt;` is a server, as one runs a Minecraft server: it loads that
///     save and opens the session by itself, nobody plays it, the players join by address
///     (`&lt;this machine&gt;:55556`) whenever they want. `cloud:&lt;id&gt;` for a Steam Cloud save,
///     `world_depot` to take the world from the save depot when one is set (see SaveDepot). <br />
///     It tells its state in `ElinMP/server.txt` and stops, after saving, when `ElinMP/server.stop` appears:
///     that is how the server application (dev/server) shows and drives it
/// </summary>
internal static class EmpServer
{
    private const float SaveSeconds = 300f;

    // a copy of the world in the game's own backups (GameIO.MakeBackup, it keeps the last ones): what an operator
    // restores after a griefer or a broken save (council 2026-10-27, 50 players: snapshots)
    private const float BackupSeconds = 3600f;

    private static readonly string? _save = Arg("-empserver");
    private static readonly string _folder = Path.Combine(Application.persistentDataPath, "ElinMP");

    private static float _next;
    private static float _nextSave;
    private static float _nextBackup;
    private static bool _loading;
    private static string _saved = "-";
    private static string _order = "";
    private static int _lastPlayers;
    private static int _ownFrameRate;
    private static bool _resting;
    private static bool _stopping;
    private static float _stopAt;
    private const float StopWaitSeconds = 20f;
    private static readonly DateTime _started = DateTime.Now;

    internal static bool Requested => _save is not null;

    /// <summary>
    ///     Asks this server to save and stop, as the operator's stop file does (the restart warning ends there)
    /// </summary>
    internal static void StopNow()
    {
        try {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "server.stop"), "stop");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "Server: the stop file could not be written");
        }
    }

    /// <summary>
    ///     The process of the game that started this server (-empparent): a server of a player's own machine, hidden
    ///     (HiddenServer). It listens on the loopback only, and stops by itself when that game is gone
    /// </summary>
    private static readonly int _parent = int.TryParse(Arg("-empparent"), out var pid) ? pid : 0;

    internal static bool Hidden => Requested && _parent > 0;

    // how long the guests of a server whose player left are waited for (the player is gone, nobody is to wait long)
    private const float ParentGoneWaitSeconds = 5f;
    private static float _stopWait = StopWaitSeconds;

    private static bool ParentGone()
    {
        try {
            return System.Diagnostics.Process.GetProcessById(_parent).HasExited;
        } catch (ArgumentException) {
            return true;
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Server: the parent process could not be looked at");
            return false;
        }
    }

    private static string? Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>
    ///     Called every frame
    /// </summary>
    internal static void Update()
    {
        // every frame, not on the 2-second tick: a player who connects to a resting server is served at full rate
        // from the next frame, the join (several round trips) does not crawl at 5 frames a second
        if (Requested) {
            Rest();
        }

        if (!Requested || Time.unscaledTime < _next) {
            return;
        }

        _next = Time.unscaledTime + 2f;
        AudioListener.volume = 0f;
        LogMemory();

        // the visible game that started this server is gone (closed, crashed): saved and stopped within seconds, no
        // process left behind holding 2 GB. Before the session is open there is nothing to save
        if (Hidden && !_stopping && ParentGone()) {
            EmpLog.Information("Server: the game that started it is gone, stopping");
            if (NetSession.Instance.Transport is null) {
                Application.Quit();
                return;
            }

            _stopping = true;
            _stopAt = Time.unscaledTime;
            _stopWait = ParentGoneWaitSeconds;
        }

        var session = NetSession.Instance;
        if (!EClass.core.IsGameStarted) {
            Tell("loading");
            // (the title has its own layers open: no waiting for an idle interface)
            if (_loading || EClass.scene.mode != Scene.Mode.Title) {
                return;
            }

            _loading = true;
            File.Delete(Path.Combine(_folder, "server.stop"));
            EmpLog.Information("Server: loading {Save}", _save);
            if (_save == SaveDepot.WorldId && SaveDepot.Enabled) {
                SaveDepot.Take();
            } else {
                // "cloud:<id>" for a Steam Cloud save, as the game's own list loads them
                var cloud = _save!.StartsWith("cloud:");
                Game.Load(cloud ? _save.Substring(6) : _save, cloud);
            }

            return;
        }

        // the owner's character is being exchanged for the server's stand-in (ServerAvatar): the session opens after
        if (EClass.pc is null || EClass._map is null || (session.Transport is null && !ServerAvatar.Settled)) {
            return;
        }

        if (session.Transport is null) {
            // a question left open by the load (a mod the save knew is missing) would hold the world
            foreach (var dialog in EClass.ui.layers.FindAll(l => l is Dialog)) {
                dialog.Close();
            }

            // without these two the session never opens: say why and stop, instead of trying for ever
            if (EClass.player?.chara?.homeBranch?.owner is null) {
                Fail("emp_server_why_no_base".lang());
                return;
            }

            EmpLog.Information("Server: opening the session on port {Port}", Common.EmpConstants.LocalPort);
            try {
                session.InitializeComponent<ElinNetHost>().StartServer(true, alsoSteam: true);
            } catch (Exception ex) {
                Fail("emp_server_why_port".Loc(Common.EmpConstants.LocalPort, ex.Message));
                return;
            }

            _nextSave = Time.unscaledTime + SaveSeconds;
            _nextBackup = Time.unscaledTime + BackupSeconds;
            return;
        }

        if (_stopping || File.Exists(Path.Combine(_folder, "server.stop"))) {
            File.Delete(Path.Combine(_folder, "server.stop"));
            if (!_stopping) {
                _stopping = true;
                _stopAt = Time.unscaledTime;
                EmpLog.Information("Server: asked to stop, saving");
            }

            // the world is saved as a game hosted from the menu would hold it: the players leave, the owner's
            // character takes the local place back from the stand-in (ServerAvatar). A player who is slow to go is
            // waited for a little; then the world is saved as it is (a crash save is mended when it is opened)
            if (ServerAvatar.Active && ServerAvatar.Is(EClass.pc) && session.Connection is ElinNetHost stopping) {
                if (!stopping.EmptiedForStop() && Time.unscaledTime - _stopAt < _stopWait) {
                    Tell("stopping");
                    return;
                }

                try {
                    ElinNetHost.GiveBackToOwner(ElinNetHost.AvatarOwnerUserId());
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Server: the owner's character could not take its place back, saved as it is");
                }
            }

            EClass.game.Save(false, true);
            Tell("stopped");
            if (Hidden) {
                HiddenServer.Clean();
            }

            Application.Quit();
            return;
        }

        // orders of the operator (kick, ban, unban), one per line; taken whole, so a line half written is not run
        var orders = Path.Combine(_folder, "server.cmd");
        if (File.Exists(orders) && session.Connection is ElinNetHost host) {
            try {
                var taken = orders + ".run";
                File.Delete(taken);
                File.Move(orders, taken);
                foreach (var line in File.ReadAllLines(taken).Where(l => l.Trim().Length > 0)) {
                    _order = line.Trim() == "backup" ? Backup() : host.RunOrder(line);
                    EmpLog.Information("Server: order {Order}: {Result}", line, _order);
                }

                File.Delete(taken);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // the application writes it at the same time: next tick
            }
        }

        // nobody is there to save: the server does, while someone plays
        if (Time.unscaledTime >= _nextSave) {
            _nextSave = Time.unscaledTime + SaveSeconds;
            if (session.CurrentPlayers.Count > 1) {
                SaveNow();
            }
        }

        // the last player out: what it did is in the world, saved at once (reliability audit 2026-10-27)
        var players = session.CurrentPlayers.Count;
        if (players < _lastPlayers && players <= 1) {
            SaveNow();
        }

        _lastPlayers = players;

        if (Time.unscaledTime >= _nextBackup) {
            Backup();
        }

        Tell("running");
    }

    private const float MemoryLogSeconds = 600f;
    private static float _nextMemoryLog;

    /// <summary>
    ///     Every 10 minutes, one line: what the process holds and the sizes of what could grow, so a server that
    ///     swells shows which part does (the idle public server took 1.4 GB an hour, 2026-11-01: the mesh passes)
    /// </summary>
    private static void LogMemory()
    {
        if (Time.unscaledTime < _nextMemoryLog) {
            return;
        }

        _nextMemoryLog = Time.unscaledTime + MemoryLogSeconds;
        try {
            long privateBytes;
            using (var self = System.Diagnostics.Process.GetCurrentProcess()) {
                privateBytes = self.PrivateMemorySize64;
            }

            int passes = 0, batches = 0;
            foreach (var pass in Resources.FindObjectsOfTypeAll<MeshPass>()) {
                passes++;
                batches += pass.batches?.Count ?? 0;
            }

            var cards = Models.CardCache.Counts;
            EmpLog.Information(
                "Server memory: managed {Managed} MB, private {Private} MB, Unity reserved {Reserved} MB, " +
                "mesh batches {Batches} in {Passes} passes, card cache {Cards}/{KeptAlive}/{Invalid}, " +
                "textures {Textures}, render textures {RenderTextures}, materials {Materials}, meshes {Meshes}, " +
                "replay frames {Frames}, chat {Chat}, journal {Journal}, players {Players}, up {Up}",
                GC.GetTotalMemory(false) >> 20, privateBytes >> 20,
                UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() >> 20,
                batches, passes, cards.Cards, cards.KeptAlive, cards.Invalid,
                Resources.FindObjectsOfTypeAll<Texture2D>().Length,
                Resources.FindObjectsOfTypeAll<RenderTexture>().Length,
                Resources.FindObjectsOfTypeAll<Material>().Length,
                Resources.FindObjectsOfTypeAll<Mesh>().Length,
                ReplayBuffer.FrameCount, ChatLog.Entries.Count, ActivityJournal.ToldCount,
                NetSession.Instance.CurrentPlayers.Count, DateTime.Now - _started);
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Server memory could not be measured");
        }
    }

    /// <summary>
    ///     ServerRestsWhenEmpty: nobody connected, nobody to simulate for (the world is frozen, EmptyWorld), so the
    ///     machine that keeps the server on all day mostly sleeps. Only the frame rate and the sound: the world, the
    ///     session and the saves stay as they are, and the game's own rate comes back as soon as a peer is there
    /// </summary>
    private static void Rest()
    {
        var rest = EmpConfig.Server.ServerRestsWhenEmpty.Value && EClass.core.IsGameStarted &&
                   NetSession.Instance.Transport is ElinNetHost host && !host.PeerIds.Any();
        if (rest == _resting) {
            return;
        }

        _resting = rest;
        if (rest) {
            _ownFrameRate = Application.targetFrameRate;
            Application.targetFrameRate = 5;
        } else {
            Application.targetFrameRate = _ownFrameRate;
        }

        AudioListener.pause = rest;
        EmpLog.Information("Server: {State}", rest ? "nobody connected, resting at 5 frames a second" : "awake");
    }

    /// <summary>
    ///     The world as it is now, saved then copied to the game's backups (also the operator's order "backup")
    /// </summary>
    private static string Backup()
    {
        _nextBackup = Time.unscaledTime + BackupSeconds;
        try {
            EClass.game.Save(true, true);
            GameIO.MakeBackup(new GameIndex { id = Game.id, cloud = EClass.game.isCloud });
            EmpLog.Information("Server: backup of {Save} made", Game.id);
            return "Backup made " + DateTime.Now.ToString("HH:mm");
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Server: backup of {Save} failed", Game.id);
            return "error: backup failed";
        }
    }

    private static void SaveNow()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var saved = SaveWriter.Save(EClass.game);
        EmpLog.Information("Server: save made in {Main} ms main thread", watch.ElapsedMilliseconds);
        if (saved) {
            _saved = DateTime.Now.ToString("HH:mm");
        } else {
            _saved = "FAILED " + DateTime.Now.ToString("HH:mm");
            EmpLog.Warning("Server: the game could not save the world, see Player.log");
        }
    }

    private static void Fail(string reason)
    {
        EmpLog.Error("Server: cannot start, {Reason}", reason);
        Tell("error:" + reason.Split('\n')[0].Trim());
        Application.Quit();
    }

    private static void Tell(string state)
    {
        try {
            var playing = state == "running" ? NetSession.Instance.CurrentPlayers.Where(p => p.Index != 0).ToList() : [];
            var players = playing.Select(p => EClass.game.cards.globalCharas.Find(p.CharaUid)?.Name ?? "?");
            Directory.CreateDirectory(_folder);
            File.WriteAllLines(Path.Combine(_folder, "server.txt"), [
                "state=" + state,
                "save=" + _save,
                "port=" + Common.EmpConstants.LocalPort,
                "players=" + string.Join("|", players),
                // (the Steam ids in the same order, for the operator's orders)
                "ids=" + string.Join("|", playing.Select(p => (ulong)p.User)),
                "order=" + _order,
                // (the banned accounts of this world, id and reason, for the app's list with Unban)
                "banned=" + string.Join("|", ElinNetHost.Bans.Select(b => b.Key + ":" + b.Value.Replace("|", " "))),
                "started=" + _started.ToString("yyyy-MM-dd HH:mm:ss"),
                "date=" + (state == "running" ? EClass.world.date.GetText(Date.TextFormat.Log) : ""),
                "saved=" + _saved,
                "time=" + DateTime.Now.ToString("HH:mm:ss"),
            ]);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // the application reads it at the same time: next tick
        }
    }
}
