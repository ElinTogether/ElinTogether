using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ElinTogether.Components;
using ElinTogether.LangMod;
using ElinTogether.Net;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     "Play as a guest of a hidden server" (host box HiddenServer, beta, Windows): hosting a world starts a second,
///     hidden Elin on this machine as a server (-empserver, see EmpServer), and this game, the visible one, joins it
///     over the loopback like any other player: the host has no character of its own, so there is nothing left that
///     the host lives differently from the guests (council 2026-10-28, "no host at all"). <br />
///     The visible game saves the world, lets go of its single-instance mutex (SingleInstance), starts the server
///     with a token only the two of them know (owner.token: the loopback seat of the same Steam account is the
///     owner's, a loopback seat without it is refused, see ElinNetHost.OwnerSeat), shows "Starting your world..."
///     and joins when the server says it runs. Anything that goes wrong, or no answer in
///     <see cref="StartSeconds" />, is the classic hosting with a line saying why. Leaving the visible game stops
///     the server (server.stop), and a server whose visible game is gone stops by itself (EmpServer, -empparent)
/// </summary>
internal static class HiddenServer
{
    /// <summary>
    ///     How long the hidden game has to be up before the world is hosted the classic way
    /// </summary>
    private const float StartSeconds = 120f;

    // the game's Steam app id: a second Elin started by this one has no steam_appid.txt to tell it
    private const string AppId = "2135150";

    private static Process? _process;
    private static float _startedAt;
    private static float _nextLook;
    private static float _runningAt;
    private static bool _joined;
    private static Action? _classic;
    private static LayerProgress? _progress;

    /// <summary>
    ///     ElinMP/server: what the visible game and its hidden server tell each other besides server.txt
    /// </summary>
    internal static string Folder => Path.Combine(Application.persistentDataPath, "ElinMP", "server");

    private static string TokenFile => Path.Combine(Folder, "owner.token");
    private static string LobbyFile => Path.Combine(Folder, "lobby.txt");
    private static string Root => Path.Combine(Application.persistentDataPath, "ElinMP");

    /// <summary>
    ///     This game starts its worlds on a hidden server: the box is ticked, on Windows, and this game is not a server
    /// </summary>
    /// <remarks>
    ///     A peer-hosted world (PeerHosting) is always kept this way, box or not: whoever keeps it plays as a plain
    ///     guest of its hidden server, through the same checks as every other player
    /// </remarks>
    internal static bool Wanted => (EmpConfig.Server.HiddenServer.Value || PeerHosting.Active) && SettingsTable.HiddenServerAvailable &&
                                   !EmpServer.Requested;

    /// <summary>
    ///     This game plays on a hidden server it started (it joined it and the server runs): it invites through the
    ///     lobby of the server. It is granted zones like any guest; its zone session listens on Steam virtual port 1,
    ///     the server keeping 0 on the same account
    /// </summary>
    internal static bool Playing => _joined && _process is { HasExited: false };

    /// <summary>
    ///     The hidden server is being started: a progress window waits for it
    /// </summary>
    internal static bool Starting => _process is not null && !_joined;

    /// <summary>
    ///     Hosting a world with the box ticked. True: the hidden server takes it from here (the progress window shows,
    ///     the game joins when the server runs), <paramref name="classic" /> is what runs instead if it does not.
    ///     False: not applicable here, the caller hosts the classic way
    /// </summary>
    internal static bool TryStart(Action classic)
    {
        if (!Wanted || Starting || Playing || NetSession.Instance.Transport is not null || !EClass.core.IsGameStarted ||
            EClass.scene.mode == Scene.Mode.Title || EClass.player?.chara?.homeBranch?.owner is null) {
            return false;
        }

        // a world kept in the depot is taken and hosted by the server application's rules, not by this
        if (SaveDepot.Enabled && Game.id == SaveDepot.WorldId) {
            return false;
        }

        try {
            if (!EmpAutoHost.SafeToSave()) {
                Fallback(classic, "emp_hidden_why_save".lang());
                return true;
            }

            // the server reads the world from the disk: what this game holds goes there first
            if (!EClass.game.Save(false, true)) {
                Fallback(classic, "emp_hidden_why_save".lang());
                return true;
            }

            return Launch(EClass.game.isCloud ? "cloud:" + Game.id : Game.id, classic);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Hidden server: could not be started");
            Fallback(classic, "emp_hidden_why_launch".lang());
            return true;
        }
    }

    // what to do when a server started by StartOn does not come up (the lease is let go), null otherwise
    private static Action? _onFail;

    /// <summary>
    ///     A peer-hosted world taken from the world's store (PeerKeeper): the hidden server is started on that save
    ///     folder, from the title screen (this game never loads it: it joins the server as a guest, which sends it the
    ///     world). <paramref name="failed" /> when the server does not come up. False: not started (failed is not run)
    /// </summary>
    internal static bool StartOn(string save, Action failed)
    {
        if (!SettingsTable.HiddenServerAvailable || EmpServer.Requested || Starting || Playing ||
            NetSession.Instance.Transport is not null || EClass.core.IsGameStarted) {
            return false;
        }

        _onFail = failed;
        try {
            return Launch(save, () => { });
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Hidden server: could not be started");
            Fallback(() => { }, "emp_hidden_why_launch".lang());
            return true;
        }
    }

    private static bool Launch(string save, Action classic)
    {
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is null || !File.Exists(exe)) {
                Fallback(classic, "emp_hidden_why_launch".lang());
                return true;
            }

            Directory.CreateDirectory(Folder);
            foreach (var old in new[] { "server.txt", "server.stop", "server.cmd" }) {
                File.Delete(Path.Combine(Root, old));
            }

            File.Delete(LobbyFile);

            // (the server learns the same bytes from the file; this game sends them in its first handshake)
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) {
                random.GetBytes(token);
            }

            File.WriteAllBytes(TokenFile, token);

            // a second Elin waits for the first one's mutex before its engine starts: this game lets go of its own
            if (!SingleInstance.Release()) {
                Fallback(classic, "emp_hidden_why_mutex".lang());
                return true;
            }

            var log = Path.Combine(Folder, "server.log");
            var start = new ProcessStartInfo(exe,
                $"-batchmode -nographics -empserver \"{save}\" -empparent {Process.GetCurrentProcess().Id} -empmute -logFile \"{log}\"" +
                (PeerHosting.Active ? " " + LocalPeerHosting.Argument : "")) {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            // (no steam_appid.txt next to a player's game: Steam is told which game this is)
            start.EnvironmentVariables["SteamAppId"] = AppId;
            start.EnvironmentVariables["SteamGameId"] = AppId;
            start.EnvironmentVariables["SteamNoOverlayUIDrawing"] = "1";
            // (Unity Doorstop, which loads BepInEx and so the mods, marks the process it started in DOORSTOP_INITIALIZED
            // and does nothing in a child that inherits it: the hidden server started as a bare Elin without this mod,
            // sat on the title screen and was dropped after 2 minutes, bench 2026-10-28)
            foreach (var name in start.EnvironmentVariables.Keys.Cast<string>()
                         .Where(k => k.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)).ToList()) {
                start.EnvironmentVariables.Remove(name);
            }

            _process = Process.Start(start);
            if (_process is null) {
                Fallback(classic, "emp_hidden_why_launch".lang());
                return true;
            }

            _classic = classic;
            _joined = false;
            _startedAt = Time.unscaledTime;
            _runningAt = 0f;
            _nextLook = 0f;
            EmpLog.Information("Hidden server: {Save} started as process {Pid}", save, _process.Id);

            _progress = LayerProgress.Start("emp_hidden_starting".lang());
            _progress.onCancel = () => Fail("emp_hidden_why_cancel".lang());
            return true;
        }
    }

    /// <summary>
    ///     Called every frame
    /// </summary>
    internal static void Update()
    {
        if (_process is null || Time.unscaledTime < _nextLook) {
            return;
        }

        _nextLook = Time.unscaledTime + 1f;
        if (_joined) {
            // the server is gone (it crashed or was stopped): nothing to protect any more
            if (_process.HasExited) {
                EmpLog.Information("Hidden server: process {Pid} is gone", _process.Id);
                _process = null;
                _joined = false;
            }

            return;
        }

        if (_process.HasExited) {
            var said = ReadState("state");
            Fail(said is { } s && s.StartsWith("error:") ? s.Substring(6) : "emp_hidden_why_exit".lang());
            return;
        }

        if (Time.unscaledTime - _startedAt > StartSeconds) {
            Fail("emp_hidden_why_timeout".lang());
            return;
        }

        // up when the session is open; its Steam lobby (the way friends come in) a moment later, not waited for long
        if (ReadState("state") != "running") {
            return;
        }

        if (_runningAt <= 0f) {
            _runningAt = Time.unscaledTime;
        }

        if (ServerLobby() == 0 && Time.unscaledTime - _runningAt < 10f) {
            return;
        }

        Join();
    }

    /// <summary>
    ///     The server runs: this game leaves its own copy of the world and joins it as a player
    /// </summary>
    private static void Join()
    {
        var session = NetSession.Instance;
        _classic = null;
        _onFail = null;
        _joined = true;
        CloseProgress();
        EmpLog.Information("Hidden server: up after {Seconds} s, joining it", (int)(Time.unscaledTime - _startedAt));
        try {
            session.ResetSession();
            if (EClass.core.IsGameStarted) {
                EClass.scene.Init(Scene.Mode.Title);
            }

            session.InitializeComponent<ElinNetClient>().ConnectLocalPort();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Hidden server: could not join the server that runs");
        }
    }

    /// <summary>
    ///     The hidden game did not come up: it is ended (this game's own child, by its process), and the world is
    ///     hosted the classic way, with the reason
    /// </summary>
    private static void Fail(string why)
    {
        var classic = _classic;
        _classic = null;
        Kill();
        CloseProgress();
        if (classic is not null) {
            Fallback(classic, why);
        }
    }

    private static void Fallback(Action classic, string why)
    {
        // a server started on a world taken from the store: the lease is let go (PeerKeeper)
        var failed = _onFail;
        _onFail = null;
        failed?.Invoke();

        // a peer-hosted world is never hosted the classic way: that would make its keeper a host again
        if (PeerHosting.Active) {
            EmpLog.Warning("Hidden server: not used, and this peer-hosted world is not hosted the usual way ({Why})", why);
            EmpPop.Information("emp_peer_no_classic".Loc(why));
            NetSession.Instance.ResetSession();
            return;
        }

        EmpLog.Warning("Hidden server: not used, the world is hosted the usual way ({Why})", why);
        EmpPop.Information("emp_hidden_fallback".Loc(why));
        try {
            classic();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "The session could not be opened");
            NetSession.Instance.ResetSession();
        }
    }

    private static void Kill()
    {
        try {
            if (_process is { HasExited: false }) {
                _process.Kill();
            }
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Hidden server: the process could not be ended");
        }

        _process = null;
        _joined = false;
    }

    private static void CloseProgress()
    {
        if (_progress != null) {
            LayerProgress.completed = true;
            _progress.onCancel = null;
        }

        _progress = null;
    }

    /// <summary>
    ///     This game is leaving: the hidden server it started is asked to save and stop (the server also watches for
    ///     this game's process, so a crash ends it too)
    /// </summary>
    internal static void OnQuit()
    {
        if (_process is not { HasExited: false }) {
            return;
        }

        try {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "server.stop"), "");
            EmpLog.Information("Hidden server: asked to stop, this game leaves");
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Hidden server: could not be asked to stop");
        }
    }

    /// <summary>
    ///     The bytes this game shows the server in its first handshake: proof that it is the one that started it. Null
    ///     when there are none (no hidden server on this machine), read from the disk each time so that a game started
    ///     again after a crash still has them
    /// </summary>
    internal static byte[]? OwnerToken()
    {
        try {
            return File.Exists(TokenFile) ? File.ReadAllBytes(TokenFile) : null;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    private static byte[]? _serverToken;

    /// <summary>
    ///     On the server: this is the token its starter wrote (compared in constant time)
    /// </summary>
    internal static bool TokenMatches(byte[]? given)
    {
        _serverToken ??= OwnerToken();
        if (_serverToken is not { Length: 32 } mine || given is not { Length: 32 }) {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < mine.Length; i++) {
            diff |= mine[i] ^ given[i];
        }

        return diff == 0;
    }

    /// <summary>
    ///     On the server: the Steam lobby it made, for the game that invites with it
    /// </summary>
    internal static void WriteServerLobby(ulong lobby)
    {
        try {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(LobbyFile, lobby.ToString());
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "Hidden server: the lobby could not be written");
        }
    }

    /// <summary>
    ///     On the server: its end, what it left for its starter is removed
    /// </summary>
    internal static void Clean()
    {
        try {
            File.Delete(TokenFile);
            File.Delete(LobbyFile);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // (left for the next start to replace)
        }
    }

    /// <summary>
    ///     The Steam lobby of the hidden server of this machine, 0 when there is none
    /// </summary>
    internal static ulong ServerLobby()
    {
        try {
            return File.Exists(LobbyFile) && ulong.TryParse(File.ReadAllText(LobbyFile).Trim(), out var lobby) ? lobby : 0UL;
        } catch (IOException) {
            return 0UL;
        } catch (UnauthorizedAccessException) {
            return 0UL;
        }
    }

    // a value of the state file of the server (ElinMP/server.txt)
    private static string? ReadState(string key)
    {
        try {
            var file = Path.Combine(Root, "server.txt");
            if (!File.Exists(file)) {
                return null;
            }

            var prefix = key + "=";
            return File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith(prefix))?.Substring(prefix.Length);
        } catch (IOException) {
            // (the server writes it at the same time: next look)
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }
}
