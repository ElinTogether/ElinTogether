using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Tasks = System.Threading.Tasks; // (the game has a Task of its own)
using ElinTogether.Components;
using ElinTogether.LangMod;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     A server that is only the save. The depot keeps the world; whoever comes first takes the world from it
///     and hosts it with the mod as it is, the others join that player, every save goes back to the depot, and
///     when the host leaves the next one can take over. No game runs at the depot. <br />
///     The depot (client setting DepotPath) is either the address of Elin Together Server, "host:port" (the
///     application of dev/server: it keeps the world as one archive and holds a real lock), a private GitHub
///     repository, "github:owner/repository" (GitHubDepot: the same requests, the access key in DepotPassword),
///     or a folder every player can reach (`world/` and `host.txt`, a lock by file date)
/// </summary>
[HarmonyPatch]
internal static class SaveDepot
{
    /// <summary>
    ///     The local save the depot's world is played from
    /// </summary>
    internal const string WorldId = "world_depot";

    // ponytail: the folder's lock is a file date, on a folder that may sync late: two players taking the world
    // in the same minute both get it and the last save wins. The server application has a real lock.
    private static readonly TimeSpan _lockLife = TimeSpan.FromMinutes(3);
    private const float BeatSeconds = 60f;
    private const float EtBeatSeconds = 15f;
    private const long MaxUnzipped = 2L * 1024 * 1024 * 1024;

    // an answer of the server application: a zipped world (a big one is some tens of MB), or a line of text
    private const int MaxAnswer = 512 * 1024 * 1024;
    private const int MaxLine = 64 * 1024;

    // GitHub keeps every world it receives and asks not to be written too often
    private const float SendSeconds = 300f;

    // what GitHub is asked in the background, one request after the other, and the answers for the game's thread
    private static readonly ConcurrentQueue<Action> _answered = new();
    private static Tasks.Task _asking = Tasks.Task.CompletedTask;

    // a save of the depot's world that GitHub has not been sent yet
    private static bool _unsent;

    // the saves made, and the version of the depot's world the local copy descends from: written in the marker,
    // so that a save kept from an earlier session never replaces a world it does not come from. Both are read
    // where the answer of GitHub arrives, in the background: under the lock
    private static readonly object _markGate = new();
    private static int _saves;
    private static string? _descends;
    private static float _nextSend;
    private static string? _who;
    private static float _whoAt = -60f;

    private static float _nextBeat;

    // the world service: a take or put is on its way (one at a time, the player clicked)
    private static bool _etBusy;
    private static string? _etRestore;

    // a beat is on its way: no other one is queued behind it (a long upload holds the line)
    private static bool _beating;
    private static string _refused = "";
    private static bool _lostTold;

    // where the holder of the folder's lock is joined (GitHub: GitHubDepot.HeldJoin)
    private static string _heldJoin = "";

    // the session this game had open when its lock last said so
    private static (ulong Lobby, int Port) _openTold;

    // the player whose game is being joined from the title screen, and until when it may take
    private static string? _joining;
    private static float _joinBy;

    private static string Root => EmpConfig.Client.DepotPath.Value.Trim();
    private static string World => Path.Combine(Root, "world");
    private static string LockFile => Path.Combine(Root, "host.txt");
    private static string ModsFile => Path.Combine(Root, "modlist.txt");
    private static string Local => CorePath.RootSave + WorldId;

    /// <summary>
    ///     Left beside the local copy while the depot has not received its last save
    /// </summary>
    private static string Unsent => Local + ".unsent";

    /// <summary>
    ///     The depot is the server application, not a folder
    /// </summary>
    private static bool Remote => Regex.IsMatch(Root, @"^[^\\/\s]+:\d+$");

    /// <summary>
    ///     The depot is a private GitHub repository
    /// </summary>
    internal static bool GitHub => Root.StartsWith("github:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The depot is the append-only world service, "etworld:https://address" (EtWorldDepot)
    /// </summary>
    internal static bool EtWorld => Root.StartsWith("etworld:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The depot is a web service (GitHub or the world service): asked in the background, sent to at most every
    ///     five minutes, with a version the local copy descends from
    /// </summary>
    private static bool Service => GitHub || EtWorld;

    /// <summary>
    ///     The version of the depot's world this game plays from (taken, or last sent)
    /// </summary>
    private static string? ServiceVersion => EtWorld ? EtWorldDepot.Version : GitHubDepot.WorldSha;

    /// <summary>
    ///     Where the player who holds the world of a web service is joined, as last read
    /// </summary>
    private static string ServiceJoin => EtWorld ? EtWorldDepot.HeldJoin : GitHubDepot.HeldJoin;

    /// <summary>
    ///     The depot is asked (Ask), it is not a folder
    /// </summary>
    private static bool Asked => Remote || Service;

    /// <summary>
    ///     This game, among the ones that share the depot (two games of one machine are two)
    /// </summary>
    private static string Me { get; } = $"{Environment.MachineName}:{Process.GetCurrentProcess().Id}";

    private static string MyName => EClass.core.IsGameStarted ? EClass.pc.Name : Environment.UserName;

    /// <summary>
    ///     This install's key for the world service (EtWorldDepot), made at its first request, never sent
    /// </summary>
    private static string KeyFile => Path.Combine(Application.persistentDataPath, "ElinMP",
#if DEBUG
        // test instances on one machine share this folder, and are different players
        EmpConfig.Dev.Identity.Value > 0 ? $"etworld_key_{EmpConfig.Dev.Identity.Value}.txt" :
#endif
        "etworld_key.txt");

    private static ulong MySteam
    {
        get {
            try {
                return Steamworks.SteamUser.GetSteamID().m_SteamID;
            } catch (Exception) {
                // Steam is away: registered as 0, refused as a keeper by the guests later (plan 2.1)
                return 0;
            }
        }
    }

    /// <summary>
    ///     The session this game has open, for the others to join: its Steam lobby. Nothing open: none
    /// </summary>
    private static (ulong Lobby, int Port) Open
    {
        get {
            if (NetSession.Instance.Transport is not ElinNetHost host) {
                return default;
            }

#if DEBUG
            // a window of the bench: every window has the same Steam account, they join by the local port
            if (host.IsLocalServer) {
                return (0, Common.EmpConstants.LocalPort);
            }
#endif

            ulong lobby = NetSession.Instance.Lobby.Current;
            return (lobby, 0);
        }
    }

    /// <summary>
    ///     What the lock says beside the name: "steam id, lobby" of this player (and the local port of a bench
    ///     window, debug builds only). Read by Join
    /// </summary>
    private static string MyJoin
    {
        get {
            var steam = 0UL;
            try {
                steam = Steamworks.SteamUser.GetSteamID().m_SteamID;
            } catch (Exception) {
                // Steam is away: the lock only says who
            }

            var (lobby, port) = Open;
            return $"{steam} {lobby}" + (port > 0 ? " " + port : "");
        }
    }

    /// <summary>
    ///     The lock says where the player who holds the world is joined (HeldBy was asked first)
    /// </summary>
    internal static bool Joinable => (Service ? ServiceJoin : _heldJoin).Length > 0;

    internal static bool Enabled => Root.Length > 0 && (Asked || Directory.Exists(Root));

    /// <summary>
    ///     The world being played was taken from the depot in this run of the game: an old local copy of it loaded
    ///     from the game's own screen is not, someone else may hold the depot
    /// </summary>
    internal static bool Taken { get; private set; }

    internal static bool Holding => Enabled && EClass.core.IsGameStarted && Game.id == WorldId &&
                                   NetSession.Instance.Transport is not ElinNetClient;

    /// <summary>
    ///     The game closes without going through the title screen: the last save still goes to GitHub and the world
    ///     is freed, the time it takes. Called while the game is still whole (NetShutdown): later, at
    ///     Application.quitting, its objects are gone and nothing here can be read
    /// </summary>
    internal static void OnQuit()
    {
        try {
            if (Service && Holding) {
                ReleaseAtTitle(Scene.Mode.Title);
            }

            _asking.Wait(30000);
        } catch (Exception ex) {
            // the save is on this PC, and the lock expires by itself
            EmpLog.Warning(ex, "Could not free the depot at the exit");
        }
    }

    /// <summary>
    ///     The player hosting the depot's world right now, null when it is free (or ours)
    /// </summary>
    internal static string? HeldBy()
    {
        try {
            if (Service) {
                // the Lobby tab asks each time it opens: GitHub is asked in the background, the tab redrawn
                if (Time.unscaledTime - _whoAt > 15f) {
                    _whoAt = Time.unscaledTime;
                    Later("WHO", null, reply => {
                        var who = reply.Ok && reply.Text.Length > 0 ? reply.Text : null;
                        if (who != _who) {
                            _who = who;
                            if (!EClass.core.IsGameStarted) {
                                LayerElinTogether.Instance?.Reopen();
                            }
                        }
                    });
                }

                return _who;
            }

            // (the server application says a name and nothing else)
            _heldJoin = "";
            if (Remote) {
                var reply = Ask("WHO");
                return reply.Ok && reply.Text.Length > 0 ? reply.Text : null;
            }

            if (!File.Exists(LockFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(LockFile) > _lockLife) {
                return null;
            }

            var lines = File.ReadAllLines(LockFile);
            if (lines.Length < 2 || lines[0] == Me) {
                return null;
            }

            _heldJoin = lines.Length > 2 ? lines[2] : "";
            return lines[1];
        } catch (Exception ex) when (ex is IOException or SocketException) {
            return null;
        }
    }

    /// <summary>
    ///     Takes the world from the depot and loads it. The player then opens the session as with any save
    /// </summary>
    internal static void Take()
    {
        if (EtWorld) {
            TakeEt();
            return;
        }

        // (GitHub says who hosts when the world is asked for; what was sent in the background arrives first)
        if (Service) {
            Settle();
        } else if (HeldBy() is { } who) {
            if (!Join(who)) {
                Dialog.Ok("emp_ui_depot_held".Loc(who));
            }

            return;
        }

        if (File.Exists(Unsent) && File.Exists(Path.Combine(Local, "game.txt"))) {
            // held by another player who can be joined: joined first. The save kept here and its marker stay as
            // they are, for the day the world is free
            try {
                if (Service && Ask("WHO") is { Ok: true, Text.Length: > 0 } held && Join(held.Text)) {
                    return;
                }
            } catch (Exception ex) when (ex is IOException or AggregateException) {
                // GitHub does not answer: the question below, as before
            }

            // (GitHub: never without asking, another player may have hosted since)
            if (EmpServer.Requested && !Service) {
                SendUnsent();
            } else {
                Dialog.YesNo("emp_ui_depot_unsent", SendUnsent, () => {
                    File.Delete(Unsent);
                    Take();
                });
            }

            return;
        }

        // the list of the world (modlist.txt): whoever hosts it plays with these mods. With FetchMods it is read
        // before the world is held: what is missing is fetched, Elin restarts and takes the world by itself
        var fetch = EmpConfig.Client.FetchMods.Value;
        var mods = fetch ? WorldMods() : null;
        var differs = mods is null ? null : ModList.Compare(ModListFile.Parse(mods), "the depot");
        if (differs is { Missing.Count: > 0 } && ModFetch.Begin(mods!, differs, "depot")) {
            return;
        }

        try {
            if (Asked) {
                var reply = Ask("TAKE");
                if (!reply.Ok) {
                    if (!reply.Text.StartsWith("held ") || !Join(reply.Text.Substring(5))) {
                        Dialog.Ok(reply.Text == "empty" ? "emp_ui_depot_empty" : Refusal(reply.Text));
                    }

                    return;
                }

                // beside the local copy, then swapped: an archive cut short leaves it as it was
                var incoming = Local + ".new";
                IO.DeleteDirectory(incoming);
                Unzip(reply.Body!, incoming);
                IO.DeleteDirectory(Local);
                Directory.Move(incoming, Local);
                Descends(ServiceVersion);
            } else {
                if (!File.Exists(Path.Combine(World, "game.txt"))) {
                    Dialog.Ok("emp_ui_depot_empty");
                    return;
                }

                IO.DeleteDirectory(Local);
                IO.CopyDir(World, Local);
                Beat();
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not take the world from the depot {Root}", Root);
            Dialog.Ok(Refusal(""));
            return;
        }

        _lostTold = _unsent = false;
        _nextSend = 0f;
        _openTold = default;
        EmpLog.Information("Took the world from the depot {Root}", Root);
        Taken = true;
        // its list is what this game publishes while it hosts it; what is not loaded here is said, by name
        if (!fetch && EmpConfig.Server.PublishMods.Value) {
            mods = WorldMods();
            differs = mods is null ? null : ModList.Compare(ModListFile.Parse(mods), "the depot");
        }

        ModList.World = mods;
        if (differs is not null) {
            ModList.Tell(differs);
        }

        Game.Load(WorldId, false);
    }

    /// <summary>
    ///     modlist.txt of the depot: the mods of the world. Null: the depot has none (the server application
    ///     keeps none), or it could not be read
    /// </summary>
    private static string? WorldMods()
    {
        try {
            var text = Service ? Ask("MODS") is { Ok: true } reply ? reply.Text : "" :
                !Remote && File.Exists(ModsFile) ? File.ReadAllText(ModsFile) : "";
            return text.Trim().Length > 0 ? text : null;
        } catch (Exception ex) {
            EmpLog.Warning("Could not read the mods of the world in the depot {Root}: {Why}", Root, ex.GetBaseException().Message);
            return null;
        }
    }

    /// <summary>
    ///     Another player holds the world: this game joins theirs, as through a Steam invitation, without one.
    ///     False when the lock does not say where (written by an older version, or the server application)
    /// </summary>
    private static bool Join(string who)
    {
        var at = (Service ? ServiceJoin : _heldJoin).Split(' ');
        if (at.Length < 2 || !ulong.TryParse(at[1], out var lobby)) {
            return false;
        }

        var session = NetSession.Instance;
#if DEBUG
        if (at.Length > 2 && ushort.TryParse(at[2], out var port)) {
            session.InitializeComponent<ElinNetClient>().ConnectLocalPort(port);
        } else
#endif
        if (lobby != 0) {
            session.Lobby.ConnectLobby(lobby);
        } else {
            // they took the world and their session is not open yet
            Dialog.Ok("emp_ui_depot_join_fail".Loc(who));
            return true;
        }

        EmpLog.Information("The depot's world is held by {Name}: joining their game", who);
        _joining = who;
        // (the connection gives up by itself after Timeout and says why; this is for when it does not)
        _joinBy = Time.unscaledTime + EmpConfig.Policy.Timeout.Value + 10f;
        return true;
    }

    /// <summary>
    ///     "Join by address" given the address of Elin Together Server: it becomes the depot and its world is
    ///     taken. False when no depot answers there (a game server, or nothing)
    /// </summary>
    // ponytail: the game waits up to 5 s when the machine drops the connection without refusing it; a thread
    // if that wait bothers someone.
    internal static bool TakeFrom(string address)
    {
        var depot = EmpConfig.Client.DepotPath;
        var before = depot.Value;
        depot.Value = address;
        // (the world service is asked in the background: what goes wrong is said there)
        if (EtWorld) {
            TakeEt(before);
            return true;
        }

        string? refusal = null;
        try {
            if (!Asked) {
                throw new IOException("not an address");
            }

            var reply = Ask("WHO");
            if (Service && !reply.Ok) {
                refusal = Refusal(reply.Text);
            }
        } catch (Exception) when (!Service) {
            depot.Value = before;
            return false;
        } catch (Exception) {
            refusal = Refusal("");
        }

        // a GitHub depot that cannot be used is said, and the depot set before stays
        if (refusal is not null) {
            depot.Value = before;
            Dialog.Ok(refusal);
            return true;
        }

        Take();
        return true;
    }

    /// <summary>
    ///     The local copy holds a save the depot never received: it goes there now. What the depot holds at
    ///     that moment (another player may have played since) is kept aside, never thrown away
    /// </summary>
    private static void SendUnsent()
    {
        // (the marker says which world of the depot this save was played from)
        try {
            var from = File.ReadAllText(Unsent).Trim();
            _descends = from.Length == 40 ? from : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _descends = null;
        }

        if (Copy(Local, true)) {
            Take();
        } else {
            Dialog.Ok(Refusal(_refused));
        }
    }

    /// <summary>
    ///     Puts the save being played in the depot, as its world
    /// </summary>
    internal static void Put()
    {
        if (!Service && HeldBy() is { } who) {
            Dialog.Ok("emp_ui_depot_held".Loc(who));
            return;
        }

        if (!EClass.game.Save(silent: true)) {
            return;
        }

        if (EtWorld) {
            PutEt(CorePath.RootSave + Game.id);
            return;
        }

        if (!Copy(CorePath.RootSave + Game.id, true)) {
            Dialog.Ok(Refusal(_refused));
            return;
        }

        // (an older unsent copy of the depot's world must not come back over the world just put)
        File.Delete(Unsent);

        // the save now lives in the depot: it is played from there, so that every later save goes back to it
        // (played on under its own name, it would never reach the depot again)
        EmpPop.Information("emp_ui_depot_put_done".lang());
        EClass.scene.Init(Scene.Mode.Title);
        EClass.core.actionsNextFrame.Add(Take);
    }

    /// <summary>
    ///     Take, with the world service: every request in the background, one after the other, and each answer goes
    ///     on from Update. The game never waits on the network (a world is tens of MB)
    /// </summary>
    /// <param name="restore">"Join by address": the depot set before, set again when this one cannot be used</param>
    private static void TakeEt(string? restore = null)
    {
        if (_etBusy) {
            if (restore is not null) {
                EmpConfig.Client.DepotPath.Value = restore;
            }

            return;
        }

        _etBusy = true;
        _etRestore = restore;
        EmpPop.Information("emp_ui_depot_et_taking".lang());
        var unsent = File.Exists(Unsent) && File.Exists(Path.Combine(Local, "game.txt"));
        Later("WHO", null, who => {
            _etBusy = false;
            if (!who.Ok) {
                Dialog.Ok(Refusal(who.Text));
                Restore();
                return;
            }

            _etRestore = null;

            // held by another player: joined (through Steam, the service does not say where yet). A save kept
            // here and its marker stay as they are, for the day the world is free
            if (who.Text.Length > 0) {
                if (!Join(who.Text)) {
                    Dialog.Ok("emp_ui_depot_held".Loc(who.Text));
                }

                return;
            }

            if (unsent) {
                Dialog.YesNo("emp_ui_depot_unsent", SendUnsentEt, () => {
                    File.Delete(Unsent);
                    TakeEt();
                });
                return;
            }

            _etBusy = true;
            var incoming = Local + ".new";
            Later("TAKE", null, TookEt, after: reply => {
                if (!reply.Ok) {
                    return reply;
                }

                // unpacked here too, beside the local copy: an archive cut short leaves it as it was
                IO.DeleteDirectory(incoming);
                Unzip(reply.Body!, incoming);
                return (true, EtWorldDepot.Version ?? "", null);
            });
        });
    }

    private static void Restore()
    {
        if (_etRestore is { } before) {
            EmpConfig.Client.DepotPath.Value = before;
            _etRestore = null;
        }
    }

    private static void TookEt((bool Ok, string Text, byte[]? Body) reply)
    {
        _etBusy = false;
        if (!reply.Ok) {
            if (!reply.Text.StartsWith("held ")) {
                // (the world may have been taken and not unpacked: given back now, not in 45 s)
                Later("RELEASE", null, _ => { });
                Dialog.Ok(Refusal(reply.Text));
            } else if (!Join(reply.Text.Substring(5))) {
                Dialog.Ok(Refusal(reply.Text));
            }

            return;
        }

        // the player did something else meanwhile (another world, a join): this one is given back
        if (EClass.core.IsGameStarted || _joining is not null || NetSession.Instance.HasActiveConnection) {
            EmpLog.Information("The world taken from the depot {Root} arrived after the player went elsewhere: given back", Root);
            IO.DeleteDirectory(Local + ".new");
            Later("RELEASE", null, _ => { });
            return;
        }

        try {
            IO.DeleteDirectory(Local);
            Directory.Move(Local + ".new", Local);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not lay the world taken from the depot {Root}", Root);
            Dialog.Ok(Refusal(""));
            Later("RELEASE", null, _ => { });
            return;
        }

        Descends(reply.Text);
        // (a marker left without a save beside it is of no world any more)
        Marker(null);
        _lostTold = _unsent = false;
        _nextSend = 0f;
        _openTold = default;
        EmpLog.Information("Took the world from the depot {Root}, version {Version}", Root, reply.Text);
        Taken = true;
        // ponytail: the service keeps no list of mods yet (the signed rules will carry it, plan step P4)
        ModList.World = null;
        Game.Load(WorldId, false);
    }

    /// <summary>
    ///     SendUnsent, with the world service: the save kept here goes over the version it was played from, never
    ///     over another one ("changed": it stays on this PC)
    /// </summary>
    private static void SendUnsentEt()
    {
        string? from;
        try {
            from = File.ReadAllText(Unsent).Trim();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            from = null;
        }

        PutEt(Local, from is not null && EtWorldDepot.IsVersion(from) ? from : null, toTitle: false);
    }

    /// <summary>
    ///     Put (or SendUnsent) with the world service: the archive is made here, of the save just written, and sent
    ///     in the background; then the world is taken from the depot, as with the other depots
    /// </summary>
    private static void PutEt(string save, string? descends = null, bool toTitle = true)
    {
        if (_etBusy) {
            return;
        }

        byte[] world;
        try {
            world = Zip(save);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning("Could not pack the world for the depot {Root}: {Why}", Root, ex.Message);
            Dialog.Ok(Refusal(""));
            return;
        }

        _etBusy = true;
        Later("PUT", world, reply => {
            _etBusy = false;
            if (!reply.Ok) {
                EmpLog.Warning("The depot {Root} refused the world: {Reason}", Root, reply.Text);
                Dialog.Ok(Refusal(reply.Text));
                return;
            }

            EmpLog.Information("World sent to the depot {Root}", Root);
            // (an older unsent copy of the depot's world must not come back over the world just put)
            Marker(null);
            if (toTitle) {
                EmpPop.Information("emp_ui_depot_put_done".lang());
                EClass.scene.Init(Scene.Mode.Title);
                EClass.core.actionsNextFrame.Add(Take);
            } else {
                TakeEt();
            }
        }, descends: descends);
    }

    private static string Refusal(string why)
    {
        return why.StartsWith("held ") ? "emp_ui_depot_held".Loc(why.Substring(5)) :
            EtWorld ? why switch {
                "changed" => "emp_ui_depot_gh_changed",
                "rolled back" => "emp_wa_rolled_back_keeper",
                "too big" => "emp_ui_depot_et_big",
                "banned" => "emp_ui_depot_et_banned",
                "too_new" or "not_vouched" or "debug_build" => "emp_ui_depot_et_not_yet",
                "reserved" => "emp_ui_depot_et_reserved",
                "steam_mismatch" => "emp_ui_depot_et_key",
                "empty" => "emp_ui_depot_empty",
                _ => "emp_ui_depot_et_down",
            } :
            !GitHub ? why == "password" ? "emp_ui_depot_password_wrong" : "emp_ui_depot_fail" :
            why switch {
                "password" => "emp_ui_depot_gh_key",
                "missing" => "emp_ui_depot_gh_missing",
                "public" => "emp_ui_depot_gh_public",
                "too big" => "emp_ui_depot_gh_big",
                "changed" => "emp_ui_depot_gh_changed",
                _ => "emp_ui_depot_gh_down",
            };
    }

    /// <summary>
    ///     The save folder replaces the depot's world. The folder is written beside it, then swapped, so a copy
    ///     cut short never leaves half a world; the server does the same with its archive
    /// </summary>
    private static bool Copy(string save, bool replaces = false)
    {
        var sent = CopyTo(save, replaces);
        if (save == Local) {
            Mark(!sent);
        }

        return sent;
    }

    private static void Mark(bool unsent)
    {
        Marker(!unsent ? null : Service ? _descends ?? "" : _refused);
    }

    /// <param name="text">null: no marker</param>
    private static void Marker(string? text)
    {
        try {
            if (text is null) {
                File.Delete(Unsent);
            } else {
                File.WriteAllText(Unsent, text);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // the marker is a courtesy: the save itself is on this PC either way
        }
    }

    /// <summary>
    ///     The local copy now descends from this version of the depot's world (taken, or just sent)
    /// </summary>
    private static void Descends(string? sha)
    {
        if (sha is not null) {
            _descends = sha;
        }
    }

    /// <param name="replaces">the folder depot keeps the world it held aside (the server decides by itself)</param>
    private static bool CopyTo(string save, bool replaces)
    {
        _refused = "";
        try {
            if (Asked) {
                var reply = Ask("PUT", Zip(save), save == Local ? _descends : null);
                _refused = reply.Text;
                if (reply.Ok && Service) {
                    Descends(ServiceVersion);
                }

                if (!reply.Ok) {
                    EmpLog.Warning("The depot {Root} refused the world: {Reason}", Root, reply.Text);
                }

                return reply.Ok;
            }

            var incoming = World + ".new";
            var old = World + ".old";
            IO.DeleteDirectory(incoming);
            IO.CopyDir(save, incoming, name => name == "Temp");
            IO.DeleteDirectory(old);
            if (Directory.Exists(World)) {
                Directory.Move(World, replaces ? World + ".replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") : old);
            }

            Directory.Move(incoming, World);
            IO.DeleteDirectory(old);
            EmpLog.Information("World sent to the depot {Root}", Root);
            try {
                // the list of the world, as GitHubDepot.WriteModList: written when there is none, never replaced
                if (!File.Exists(ModsFile)) {
                    File.WriteAllText(ModsFile, ModList.Text);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // the world is sent already
            }

            return true;
        } catch (Exception ex) {
            EmpLog.Warning("Could not send the world to the depot {Root}: {Why}", Root, ex.GetBaseException().Message);
            return false;
        }
    }

    private static byte[] Zip(string save)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
            foreach (var file in Directory.GetFiles(save, "*", SearchOption.AllDirectories)) {
                var name = file.Substring(save.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (name.StartsWith("Temp/")) {
                    continue;
                }

                // (the date of the file, not of the archive: the same save makes the same archive, which is how
                // GitHub is known to have it already when its answer was lost)
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = File.GetLastWriteTime(file);
                using var from = File.OpenRead(file);
                using var to = entry.Open();
                from.CopyTo(to);
            }
        }

        return bytes.ToArray();
    }

    private static void Unzip(byte[] world, string folder)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(new MemoryStream(world), ZipArchiveMode.Read);
        long size = 0;
        foreach (var entry in zip.Entries) {
            size += entry.Length;
        }

        // (a small archive that unfolds into a full disk is not a world)
        if (size > MaxUnzipped) {
            throw new IOException("the depot sent a world too big once unzipped");
        }

        long written = 0;
        foreach (var entry in zip.Entries) {
            // (archives made by Windows write their folders with backslashes)
            var name = entry.FullName.Replace('\\', '/');
            var path = Path.GetFullPath(Path.Combine(root, name));
            // what comes from the network stays inside the save folder
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                throw new IOException("the depot sent a file outside the save: " + entry.FullName);
            }

            if (name.EndsWith("/")) {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var from = entry.Open();
            using var to = File.Create(path);
            // the sizes the archive says are the archive's word only: what is really unpacked is counted
            // (security audit 2026-10-29)
            var piece = new byte[81920];
            for (var n = from.Read(piece, 0, piece.Length); n > 0; n = from.Read(piece, 0, piece.Length)) {
                written += n;
                if (written > size || written > MaxUnzipped) {
                    throw new IOException("the depot sent a world bigger once unzipped than its archive says");
                }

                to.Write(piece, 0, n);
            }
        }
    }

    /// <summary>
    ///     One request to the server application: password, command and who asks on the first lines, then the
    ///     length and the bytes of a world when one is sent. The answer has the same shape
    /// </summary>
    // ponytail: on the game's thread for what the player just clicked (Take, Put, "Join by address"); what happens
    // while playing (each save, the beat) goes through Later
    private static (bool Ok, string Text, byte[]? Body) Ask(string command, byte[]? body = null, string? descends = null)
    {
        if (EtWorld) {
            // ponytail: every path of the world service goes through Later (TakeEt, PutEt); this is for one left
            Settle();
            return AskEt(Root, KeyFile, MySteam, MyName, command, body, Local, descends);
        }

        if (GitHub) {
            // ponytail: here too the game waits (Take, Put, "Join by address": the player just clicked), a few
            // seconds with GitHub, seven for the first request, and up to a minute behind a save still going
            Settle();
            GitHubDepot.ModListText ??= () => ModList.Text;
            return GitHubDepot.Ask(Root.Substring(7), EmpConfig.Client.DepotPassword.Value, command, Me, MyName, body,
                descends, MyJoin);
        }

        // after what was sent in the background (a save on its way): not before. Not for a question (the Lobby tab
        // asks who holds the world each time it opens)
        if (command != "WHO") {
            Settle();
        }

        return AskServer(Root, EmpConfig.Client.DepotPassword.Value, command, MyName, body);
    }

    /// <summary>
    ///     A request to the world service: reads nothing of the game, so that it runs in the background
    /// </summary>
    private static (bool Ok, string Text, byte[]? Body) AskEt(string root, string keyFile, ulong steam, string name,
        string command, byte[]? body, string local, string? descends)
    {
#if DEBUG
        const bool debug = true;
#else
        const bool debug = false;
#endif
        return EtWorldDepot.Ask(root.Substring(8), keyFile, steam, name, command, body, local, descends, debug,
            ModInfo.Version);
    }

    /// <summary>
    ///     The request itself, to Elin Together Server: reads nothing of the game, so that it runs in the background
    /// </summary>
    private static (bool Ok, string Text, byte[]? Body) AskServer(string root, string password, string command, string name,
        byte[]? body)
    {
        var at = root.LastIndexOf(':');
        using var client = new TcpClient();
        // a refused connection (no server there) comes wrapped by the task: told as the depot not answering, which
        // every caller expects; it reached the player as an error at the title screen (GitHub issue 8)
        try {
            if (!client.ConnectAsync(root.Substring(0, at), int.Parse(root.Substring(at + 1))).Wait(5000)) {
                throw new IOException("the depot does not answer");
            }
        } catch (AggregateException ex) {
            throw new IOException("the depot does not answer", ex.InnerException ?? ex);
        }

        using var stream = client.GetStream();
        stream.ReadTimeout = stream.WriteTimeout = 60000;
        var head = $"{password}\n{command}\n{Me}\n{name}\n{body?.Length ?? 0}\n";
        var bytes = Encoding.UTF8.GetBytes(head);
        stream.Write(bytes, 0, bytes.Length);
        if (body is not null) {
            stream.Write(body, 0, body.Length);
        }

        var status = ReadLine(stream);
        var length = int.Parse(ReadLine(stream));
        // the array is made before anything comes: a depot saying 2 GB got them (security audit 2026-10-29)
        if (length > MaxAnswer) {
            throw new IOException($"the depot announced {length} bytes, more than a world");
        }

        byte[]? answer = null;
        if (length > 0) {
            answer = new byte[length];
            for (var read = 0; read < length;) {
                var n = stream.Read(answer, read, length - read);
                if (n <= 0) {
                    throw new IOException("the depot stopped answering");
                }

                read += n;
            }
        }

        return (status.StartsWith("OK"), status.Length > 3 ? status.Substring(3) : "", answer);
    }

    private static string ReadLine(Stream stream)
    {
        var line = new MemoryStream();
        for (var b = stream.ReadByte(); b != '\n'; b = stream.ReadByte()) {
            if (b < 0) {
                throw new IOException("the depot stopped answering");
            }

            line.WriteByte((byte)b);
            if (line.Length > MaxLine) {
                throw new IOException("the depot sent a line too long");
            }
        }

        return Encoding.UTF8.GetString(line.ToArray());
    }

    /// <summary>
    ///     GitHub answers in a second or more (seven for the first request of the game), the server application as
    ///     fast as a world travels: what happens while playing is asked in the background, in order, and the answer
    ///     is given on the game's thread (Update). The server's were asked on the game's thread, which froze while
    ///     each save travelled, and up to 5 s each minute with the server away (reliability audit 2026-10-27, #9)
    /// </summary>
    /// <param name="sent">called in the background as soon as the depot accepted (the game may be closing)</param>
    /// <param name="after">
    ///     also in the background, on the answer, before it goes to the game's thread (the world service: a world
    ///     taken is unpacked there)
    /// </param>
    /// <param name="descends">the world service: PUT of a save kept from an earlier session (see GitHubDepot.Ask)</param>
    private static void Later(string command, byte[]? body, Action<(bool Ok, string Text, byte[]? Body)> then,
        Action? sent = null, Func<(bool Ok, string Text, byte[]? Body), (bool Ok, string Text, byte[]? Body)>? after = null,
        string? descends = null)
    {
        var (root, key, name, join, github) = (Root, EmpConfig.Client.DepotPassword.Value, MyName, MyJoin, GitHub);
        // (read here: Unity's paths and Steam are for the game's thread only)
        var et = EtWorld ? (KeyFile, Local, MySteam) : default;
        _asking = _asking.ContinueWith(_ => {
            (bool Ok, string Text, byte[]? Body) reply;
            try {
                if (et.KeyFile is not null) {
                    reply = AskEt(root, et.KeyFile, et.MySteam, name, command, body, et.Local, descends);
                } else if (github) {
                    GitHubDepot.ModListText ??= () => ModList.Text;
                    reply = GitHubDepot.Ask(root.Substring(7), key, command, Me, name, body, join: join);
                } else {
                    reply = AskServer(root, key, command, name, body);
                }

                if (after is not null) {
                    reply = after(reply);
                }

                if (reply.Ok) {
                    sent?.Invoke();
                }
            } catch (Exception ex) {
                EmpLog.Warning("The depot {Root} did not answer {Command}: {Why}", root, command, ex.Message);
                reply = (false, "", null);
            }

            _answered.Enqueue(() => then(reply));
        }, Tasks.TaskScheduler.Default);
    }

    /// <summary>
    ///     Waits for what was asked in the background, and gives the answers
    /// </summary>
    private static void Settle()
    {
        _asking.Wait(60000);
        while (_answered.TryDequeue(out var then)) {
            then();
        }
    }

    /// <summary>
    ///     The last save of the depot's world goes to GitHub, in the background
    /// </summary>
    /// <param name="command">the world service: "PUT last-leave" for the save sent when the world is given back</param>
    private static void SendLater(string command = "PUT")
    {
        var etworld = EtWorld;
        _unsent = false;
        _nextSend = Time.unscaledTime + SendSeconds;
        var saved = _saves;
        byte[] world;
        try {
            world = Zip(Local);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _unsent = true;
            return;
        }

        Later(command, world, reply => {
            _refused = reply.Text;
            if (reply.Ok) {
                EmpLog.Information("World sent to the depot {Root}", Root);
                return;
            }

            EmpLog.Warning("The depot {Root} did not take the world: {Reason}", Root, reply.Text);
            if (Lost(reply.Text)) {
                return;
            }

            // it goes again at the next sending; meanwhile the player knows, and why
            _unsent = true;
            EmpPop.Information(Refusal(reply.Text).lang() + " " + "emp_ui_depot_unsaved".lang());
        }, () => {
            // GitHub has this save: no marker, unless the game saved again meanwhile. Done here and not on the
            // game's thread, which no longer runs when the game is closing
            lock (_markGate) {
                Descends(etworld ? EtWorldDepot.Version : GitHubDepot.WorldSha);
                Marker(saved == _saves ? null : _descends);
            }
        });
    }

    /// <summary>
    ///     This game keeps the public world of the world service (its lease): its lobby is the public server
    /// </summary>
    internal static bool KeepsEtWorld => EtWorld && Holding && EtWorldDepot.WorldName == "public";

    /// <summary>
    ///     Asks the world service a host certificate naming that lobby (plan P3), written in it on the answer
    /// </summary>
    internal static void AskCertificate(ulong lobby)
    {
        if (!KeepsEtWorld || lobby == 0) {
            return;
        }

        _nextCertAsk = Time.unscaledTime + 60f;
        _certLobby = lobby;
        Later($"CERT {lobby}", null, reply => {
            if (!reply.Ok) {
                EmpLog.Warning("The world service gave no host certificate for lobby {LobbyId}: {Why}", lobby, reply.Text);
            }

            Certify();
        });
    }

    // after a beat or a certificate: the lobby carries the last certificate, none once the lease is gone, and a
    // keeper whose lobby has none yet (opened before the world was taken, or a new lobby since) asks one
    private static void Certify()
    {
        if (!EtWorld) {
            return;
        }

        var cert = KeepsEtWorld ? EtWorldDepot.Certificate : null;
        if (cert != Net.Steam.PublicServerCertificate.Current) {
            Net.Steam.PublicServerCertificate.Set(cert);
        }

        if (KeepsEtWorld && Time.unscaledTime >= _nextCertAsk &&
            Net.Steam.PublicServerCertificate.OwnLobby() is var lobby and not 0 &&
            (cert is null || lobby != _certLobby)) {
            AskCertificate(lobby);
        }
    }

    // (a certificate asked once a minute at most while the service gives none, not at every beat)
    private static float _nextCertAsk;

    // the lobby the last certificate was asked for
    private static ulong _certLobby;

    private static void Beat()
    {
        // (the world service's lease lives 45 s)
        _nextBeat = Time.unscaledTime + (EtWorld ? EtBeatSeconds : BeatSeconds);
        if (Asked) {
            if (_beating) {
                return;
            }

            _beating = true;
            Later("BEAT", null, reply => {
                _beating = false;
                Lost(reply.Text);
                Certify();
            });
        } else {
            File.WriteAllLines(LockFile, [Me, MyName, MyJoin]);
        }
    }

    // every save of the depot's world by the player who took it goes back to the depot
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Game), nameof(Game.Save))]
    private static void OnSaved(bool __result)
    {
        if (!__result || !Holding) {
            return;
        }

        if (Service) {
            // the marker first: a game that closes before GitHub has the save offers to send it next time
            lock (_markGate) {
                _saves++;
                _unsent = true;
                Mark(true);
            }

            return;
        }

        if (Remote) {
            SendToServer();
            return;
        }

        if (Copy(Local)) {
            Beat();
        } else if (!Lost(_refused)) {
            EmpPop.Information("emp_ui_depot_unsaved".lang());
        }
    }

    /// <summary>
    ///     The save just made goes to Elin Together Server in the background. As safe as it was on the game's thread:
    ///     the archive is made here, of the save as it was just written; the marker says "not sent" until the server
    ///     took it (a game closed meanwhile offers to send it next time, see Take); a refusal is told as before, and
    ///     the next save goes again
    /// </summary>
    private static void SendToServer()
    {
        int saved;
        lock (_markGate) {
            saved = ++_saves;
            Mark(true);
        }

        byte[] world;
        try {
            world = Zip(Local);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning("Could not send the world to the depot {Root}: {Why}", Root, ex.Message);
            EmpPop.Information("emp_ui_depot_unsaved".lang());
            return;
        }

        Later("PUT", world, reply => {
            _refused = reply.Text;
            if (reply.Ok) {
                EmpLog.Information("World sent to the depot {Root}", Root);
                return;
            }

            EmpLog.Warning("The depot {Root} refused the world: {Reason}", Root, reply.Text);
            if (!Lost(reply.Text)) {
                EmpPop.Information("emp_ui_depot_unsaved".lang());
            }
        }, () => {
            // the server has this save: no marker, unless the game saved again meanwhile (in the background: the
            // game's thread no longer runs when the game is closing)
            lock (_markGate) {
                if (saved == _saves) {
                    Marker(null);
                }
            }
        });
    }

    /// <summary>
    ///     The depot answered that another player hosts the world: this game was cut off long enough for the
    ///     lock to pass on. Said once, in a dialog: nothing played from here on reaches the depot
    /// </summary>
    private static bool Lost(string why)
    {
        if (why == "rolled back") {
            RolledBack();
            return true;
        }

        if (why != "changed" && why != "banned" && !why.StartsWith("held ")) {
            return false;
        }

        if (!_lostTold) {
            _lostTold = true;
            Dialog.Ok(why == "changed" ? "emp_ui_depot_gh_changed" : why == "banned" ? "emp_ui_depot_et_banned" :
                "emp_ui_depot_lost".Loc(why.Substring(5)));
        }

        return true;
    }

    /// <summary>
    ///     An admin rolled the world back while this game kept it (PLAN_peer_hosted_server 4.3): nothing more of this
    ///     session goes to the service (EtWorldDepot refuses it), the players are told, and a hidden or headless server
    ///     stops after 60 seconds (the restart warning); the next keeper opens the version the admin chose. Once
    /// </summary>
    private static void RolledBack()
    {
        if (_lostTold) {
            return;
        }

        _lostTold = true;
        EmpLog.Warning("World service: an admin rolled the world back; this session is no longer saved there");
        if (NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false } host) {
            host.Announce("emp_wa_rolled_back_all".lang());
            host.WarnRestart(1);
        }

        if (!EmpServer.Requested) {
            Dialog.Ok("emp_wa_rolled_back_keeper");
        }
    }

    /// <summary>
    ///     Called every frame: the holder says it is still there
    /// </summary>
    internal static void Update()
    {
        while (_answered.TryDequeue(out var then)) {
            then();
        }

        // at most one world every five minutes goes to GitHub: the last one saved, and always the one at the exit
        if (_unsent && Time.unscaledTime >= _nextSend && Holding) {
            SendLater();
        }

        if (_joining is { } host) {
            var session = NetSession.Instance;
            if (EClass.core.IsGameStarted || session.HasActiveConnection) {
                _joining = null;
            } else if (session.Transport is not ElinNetClient || Time.unscaledTime > _joinBy) {
                // their game is not open (just started, or it stopped): back to the title, with one line
                _joining = null;
                session.ResetSession();
                Dialog.Ok("emp_ui_depot_join_fail".Loc(host));
            }
        }

        if (!Holding) {
            return;
        }

        // the session opened or closed: the lock says so now, not a minute later
        if (Open != _openTold) {
            _openTold = Open;
            _nextBeat = 0f;
        }

        if (Time.unscaledTime < _nextBeat) {
            return;
        }

        try {
            Beat();
        } catch (Exception ex) when (ex is IOException or SocketException or AggregateException) {
            // next minute
        }
    }

    [ElinPostSceneInit]
    private static void ReleaseAtTitle(Scene.Mode mode)
    {
        if (mode == Scene.Mode.Title) {
            Taken = false;
        }

        if (mode != Scene.Mode.Title || !Enabled) {
            return;
        }

        try {
            if (Service) {
                if (_unsent) {
                    SendLater(EtWorld ? "PUT last-leave" : "PUT");
                }

                Later("RELEASE", null, _ => { });
            } else if (Remote) {
                // behind the last save, still on its way
                Later("RELEASE", null, _ => { });
            } else if (File.Exists(LockFile) && File.ReadAllLines(LockFile) is { Length: > 0 } lines && lines[0] == Me) {
                File.Delete(LockFile);
            }
        } catch (Exception ex) when (ex is IOException or SocketException or AggregateException) {
            // it expires by itself
        }
    }
}
