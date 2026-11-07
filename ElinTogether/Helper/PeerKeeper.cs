using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Components;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;
using Steamworks;
using UnityEngine;
using Tasks = System.Threading.Tasks;

namespace ElinTogether.Helper;

/// <summary>
///     Who keeps the peer-hosted world next, and the takeover by itself (PLAN_peer_hosted_server, step P5, sections
///     2.3-2.5), on the world's online store (IWorldStore, dev/etworld/API.md). Only when a store is set
///     (<see cref="Enabled" />); otherwise the takeover stays the one of P4. <br />
///     <b>Opening</b> (the public world's card, a guest that lost its keeper): WHO; a live keeper is joined; a free world
///     is taken (TAKE) by a player who may keep it, laid from its own copy when that one is the newest or holds the
///     same files as the head (no download), else from the head, and opened on a hidden server of this machine, which
///     this game joins as a plain guest (HiddenServer.StartOn). <br />
///     <b>Keeping</b> (the hidden server): the lease is renewed every 15 s with the succession list (eligible guests,
///     a whole copy of the last save first, then the longest connected, then the smallest Steam id), sent to the guests
///     with each save too; a version goes to the store when the session opens, every 5 minutes when the world was
///     saved, and at the end. <br />
///     <b>Leaving</b>: the last save to every guest, the final version, RELEASE next=S, then "the host leaves, S goes
///     on" (HostLeaving.Next); S takes within the 15 s the store keeps the world for it; the others follow S
///     (WorldHandover, NetReconnect). Nobody left: the last version and RELEASE, the next player who comes opens it. <br />
///     <b>Crash</b>: the guests see the link drop and ask WHO; once the lease is free (30 s), the first of the last
///     succession list takes, each next one 10 s later; a lease taken by another is followed. <br />
///     <b>Rollback</b> (BEAT answers "rollback n"): the keeper says it, saves nothing more, hands over in a minute; the
///     next keeper opens the head (the rolled back version) whatever its copy says
/// </summary>
internal static class PeerKeeper
{
    /// <summary>
    ///     The save folder the peer-hosted world is opened from, on every keeper's machine
    /// </summary>
    internal const string SaveId = "world_peer";

    // (council 2026-11-07: lease 30 s, a beat every 10 s, one missed beat is no takeover)
    private const float BeatSeconds = 10f;
    private const float PutSeconds = 300f;
    private const float SuccessionSeconds = 60f;
    private const float TurnSeconds = 10f;
    private const float ClaimSeconds = 60f;
    private const float HandoverSeconds = 90f;
    private const float RollbackSeconds = 60f;
    private const float JoinSeconds = 150f;
    private const int MaxSuccession = 32;

    private static IWorldStore Store => PeerStore.Current;

    /// <summary>
    ///     The election runs: a peer-hosted world with a store set
    /// </summary>
    internal static bool Enabled => PeerHosting.Active && Store.Ready;

    /// <summary>
    ///     This game, as the store and the other players know it
    /// </summary>
    private static ulong Me => NetSession.Instance.Self is { } self && (ulong)self.User != 0 ? (ulong)self.User : PlayerId;

    /// <summary>
    ///     The id this player is known by in the world and at the store, the same in its visible game and in its
    ///     hidden server (whose own seat is a reserved identity, ServerAvatar.HostUser): its Steam id, or the bench's
    ///     identity of the window
    /// </summary>
    internal static ulong PlayerId =>
#if DEBUG
        EmpConfig.Dev.Identity.Value != 0 ? SteamNetPeer.DevIdentityBase + (ulong)EmpConfig.Dev.Identity.Value :
#endif
        UserData.Me;

    // ------------------------------------------------------------------------------- the store, off the thread

    private static readonly ConcurrentQueue<Action> _answered = new();
    private static Tasks.Task _asking = Tasks.Task.CompletedTask;

    /// <summary>
    ///     One request to the store, after the ones before; <paramref name="then" /> on the game's thread with the
    ///     answer, or the exception when the store could not be reached
    /// </summary>
    private static void Later<T>(Func<T> ask, Action<T?, Exception?> then)
    {
        _asking = _asking.ContinueWith(_ => {
            T? value = default;
            Exception? failed = null;
            try {
                value = ask();
            } catch (Exception ex) {
                failed = ex;
                EmpLog.Warning("Peer-hosted world: the store did not answer: {Why}", ex.GetBaseException().Message);
            }

            _answered.Enqueue(() => then(value, failed));
        }, Tasks.TaskScheduler.Default);
    }

    private static Tasks.Task<T> Off<T>(Func<T> ask)
    {
        var task = _asking.ContinueWith(_ => ask(), Tasks.TaskScheduler.Default);
        _asking = task;
        return task;
    }

    /// <summary>
    ///     Every frame (both the visible game and its hidden server)
    /// </summary>
    internal static void Update()
    {
        while (_answered.TryDequeue(out var then)) {
            try {
                then();
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Peer-hosted world: an answer of the store could not be used");
            }
        }

        if (!Enabled || EmpServer.Requested) {
            return;
        }

        UpdateVisible();
    }

    // ===================================================================================== the visible game

    // the lease this game took while its hidden server starts (the server renews it once it runs), 0: none
    private static long _claimed;
    private static float _nextClaimBeat;
    private static bool _claiming;
    private static bool _toldKeeping;

    /// <summary>
    ///     A claim is under way: nothing else tries to join the keeper that is gone
    /// </summary>
    internal static bool Claiming => _claiming;

    private static void UpdateVisible()
    {
        if (_claimed == 0) {
            return;
        }

        // the server joined: it beats from now on, and the player is told once
        if (HiddenServer.Playing) {
            if (!_toldKeeping) {
                _toldKeeping = true;
                EmpPop.Information("emp_peer_keeping".lang());
            }

            return;
        }

        if (!HiddenServer.Starting) {
            // (the server did not come up: HiddenServer said why and let the lease go, see StartOn)
            _claimed = 0;
            return;
        }

        if (Time.unscaledTime >= _nextClaimBeat) {
            _nextClaimBeat = Time.unscaledTime + BeatSeconds;
            var handover = _claimed;
            Later(() => Store.Beat(handover, []), (reply, _) => {
                if (reply is { Ok: false } && reply.Error is "not_holder" or "banned") {
                    EmpLog.Warning("Peer-hosted world: the lease was lost while the server started ({Why})", reply.Error);
                }
            });
        }
    }

    private static void Let(long handover, ulong next)
    {
        Later(() => Store.Release(handover, next), (_, _) => { });
    }

    /// <summary>
    ///     The public world's card at the title screen: the keeper joined, or the world opened here when nobody keeps
    ///     it and this game may. False when the election does not run (the card does what it did)
    /// </summary>
    internal static bool Enter()
    {
        if (!Enabled || _claiming || HiddenServer.Starting) {
            return false;
        }

        if (HiddenServer.Playing) {
            // (this machine keeps the world already: its own server is joined again)
            NetSession.Instance.InitializeComponent<ElinNetClient>().ConnectLocalPort();
            return true;
        }

        _claimFailed = false;
        EmpPop.Information("emp_peer_asking".lang());
        // (read here, on the game's thread: starts the fetch of the signed documents when none is held yet)
        _ = PeerHosting.Current.SignedRules;
        Ask(0);
        return true;
    }

    // the signed rules travel apart from the store's "who": a first click came back before them, "signed rules
    // missing", and only the second one kept the world. Asked again a few times while they are still on their way
    private const int RulesWaits = 10;

    private static void Ask(int waited)
    {
        Later(() => {
            if (waited > 0) {
                System.Threading.Thread.Sleep(500);
            }

            return Store.Who();
        }, (who, failed) => {
            if (who is null) {
                EmpPop.Block("emp_peer_store_down".Loc(failed?.GetBaseException().Message ?? ""));
                return;
            }

            if (!who.Free && who.Lease!.Key != who.MyKey) {
                JoinKeeper(who.Lease.Steam, who.Lease.Name);
                return;
            }

            if (who.ReservedForOther(Me)) {
                JoinKeeper(who.ReservedFor, "");
                return;
            }

            if (CanKeep(who) is { } why) {
                if (PeerHosting.Current.SignedRules is null && waited < RulesWaits) {
                    Ask(waited + 1);
                    return;
                }

                EmpPop.Block("emp_peer_nobody".Loc(why));
                return;
            }

            Claim(null);
        });
    }

    /// <summary>
    ///     Null when this game may keep the world, else why not
    /// </summary>
    private static string? CanKeep(StoreWho? who)
    {
        if (!SettingsTable.HiddenServerAvailable) {
            return "emp_peer_why_hidden".lang();
        }

        if (PeerHosting.IsBanned(Me)) {
            return "banned";
        }

        if (PeerHosting.Current.SignedRules is null) {
            return "emp_peer_no_rules".lang();
        }

        return who is { Eligible: false } ? who.Why : null;
    }

    // ------------------------------------------------------------------------------ joining the keeper

    private static Coroutine? _joining;

    /// <summary>
    ///     The game of the player who keeps the world, found by its lobby (the hidden server's lobby is owned by the
    ///     keeper's Steam account), and joined. Looked for a while: a keeper that just took the world is still opening
    /// </summary>
    private static void JoinKeeper(ulong keeper, string name)
    {
        if (keeper == 0) {
            return;
        }

        if (_joining is not null) {
            EmpMod.Instance.StopCoroutine(_joining);
        }

        EmpLog.Information("Peer-hosted world: kept by {Keeper} {Name}, joining its game", keeper, name);
        WorldHandover.FollowKeeper(keeper);
        _joining = EmpMod.Instance.StartCoroutine(LookForKeeper(keeper));
    }

    private static IEnumerator LookForKeeper(ulong keeper)
    {
        var session = NetSession.Instance;
        var until = Time.realtimeSinceStartup + JoinSeconds;
        var nextAsk = Time.realtimeSinceStartup + 20f;
        try {
            while (Time.realtimeSinceStartup < until) {
                if (EClass.core.IsGameStarted || session.Transport is not null || _claiming) {
                    yield break;
                }

                // (no lobby yet 20 s on: the keeper may have crashed with its lease still running; once the store
                // says the world is free, this game takes it)
                if (Time.realtimeSinceStartup >= nextAsk) {
                    nextAsk = Time.realtimeSinceStartup + 20f;
                    var asking = Off(() => Store.Who());
                    while (!asking.IsCompleted) {
                        yield return null;
                    }

                    if (!asking.IsFaulted && asking.Result is { } who && who.Free && !who.ReservedForOther(Me) && CanKeep(who) is null) {
                        EmpLog.Information("Peer-hosted world: the keeper {Keeper} is gone and its lease ran out, taking the world", keeper);
                        Claim(keeper);
                        yield break;
                    }
                }

                if (!session.Lobby.IsListing) {
                    LobbyData? found = null;
                    var asked = true;
                    session.Lobby.GetOnlineLobbies(lobbies => {
                        asked = false;
                        found = lobbies.Where(l => l.GameServer.id.m_SteamID == keeper)
                            .OrderByDescending(l => int.TryParse(l[Common.EmpLobbyData.Handover], out var h) ? h : 0)
                            .Select(l => (LobbyData?)l).FirstOrDefault();
                    });
                    var answerBy = Time.realtimeSinceStartup + 10f;
                    while (asked && Time.realtimeSinceStartup < answerBy) {
                        yield return null;
                    }

                    if (found is { } lobby) {
                        EmpLog.Information("Peer-hosted world: joining the keeper's lobby {Lobby}", lobby);
                        WorldHandover.Followed();
                        session.Lobby.ConnectLobby(lobby);
                        yield break;
                    }
                }

                yield return new WaitForSecondsRealtime(5f);
            }

            EmpLog.Information("Peer-hosted world: the keeper's game {Keeper} was not found", keeper);
            EmpPop.Block("emp_peer_join_fail".Loc(Name(keeper)));
        } finally {
            _joining = null;
        }
    }

    internal static string Name(ulong user)
    {
        try {
            var name = SteamFriends.GetFriendPersonaName(new(user));
            return name is { Length: > 0 } ? name : user.ToString();
        } catch (Exception) {
            return user.ToString();
        }
    }

    // ------------------------------------------------------------------------------ taking the world

    /// <summary>
    ///     The keeper said it leaves and named this game (HostLeaving.Next): it takes the world now, the store keeps
    ///     it for this game 15 s
    /// </summary>
    internal static void NamedNext()
    {
        EmpLog.Information("Peer-hosted world: the keeper leaves and hands the world to this game");
        Claim(WorldHandover.LastKeeper, afterCopy: true);
    }

    /// <summary>
    ///     Takes the lease and opens the world here. <paramref name="former" />: the keeper that was lost or that left
    ///     (its lease may still be live for a moment: waited for, never taken from another player)
    /// </summary>
    private static void Claim(ulong? former, bool afterCopy = false)
    {
        if (_claiming) {
            return;
        }

        _claiming = true;
        EmpMod.Instance.StartCoroutine(RunClaim(former, afterCopy));
    }

    private sealed class Laid
    {
        internal string Why = "";
        internal long Handover;
        internal long Head;
        internal string Reason = "opening";
        internal string From = "";
        internal ulong HeldBy;
        internal string HeldName = "";
    }

    private static IEnumerator RunClaim(ulong? former, bool afterCopy)
    {
        var session = NetSession.Instance;
        try {
            // the last save the keeper sent may still be on its way to the disk
            var copyBy = Time.realtimeSinceStartup + 20f;
            while (afterCopy && WorldCopyReceiver.IsClosing && Time.realtimeSinceStartup < copyBy) {
                yield return null;
            }

            var mine = WorldHandover.Mine() ?? BestPeerCopy();
            var me = Me;
            var target = CorePath.RootSave + SaveId;
            var work = Off(() => TakeAndLay(former ?? 0, me, mine, target));
            while (!work.IsCompleted) {
                yield return null;
            }

            var laid = work.IsFaulted ? new Laid { Why = work.Exception!.GetBaseException().Message } : work.Result;
            if (laid.HeldBy != 0) {
                // another player keeps it now: its game is the one to join
                _claiming = false;
                if (session.Transport is ElinNetClient { IsConnected: true }) {
                    // (still in the old keeper's game a moment: NetReconnect follows the new keeper)
                    WorldHandover.FollowKeeper(laid.HeldBy);
                } else {
                    NetReconnect.Stop();
                    JoinKeeper(laid.HeldBy, laid.HeldName);
                }

                yield break;
            }

            if (laid.Why.Length > 0) {
                // (said once: a guest that lost its keeper does not try again every few seconds)
                _claimFailed = true;
                EmpLog.Warning("Peer-hosted world: not opened here: {Why}", laid.Why);
                EmpPop.Information("emp_peer_open_fail".Loc(laid.Why));
                yield break;
            }

            EmpLog.Information("Peer-hosted world: taken, handover {Handover}, from {From}, head {Head}", laid.Handover, laid.From, laid.Head);
            WriteLeaseFile(laid);

            // out of the game of the keeper that is gone, and no more attempts to join it (the session's patches stay
            // until this game is a guest of its own hidden server, HiddenServer)
            ElinNetBase.KeepPatches = true;
            NetReconnect.Stop();
            if (session.Transport is not null) {
                session.ResetSession();
            }

            yield return null;
            if (EClass.core.IsGameStarted) {
                EClass.ui.RemoveLayers();
                EClass.scene.Init(Scene.Mode.Title);
            }

            yield return null;
            yield return null;

            _claimed = laid.Handover;
            _nextClaimBeat = Time.unscaledTime + BeatSeconds;
            _toldKeeping = false;
            var handover = laid.Handover;
            Action failed = () => {
                EmpLog.Warning("Peer-hosted world: the hidden server did not come up, the world is let go");
                ElinNetBase.ReleasePatches();
                Let(handover, 0);
                _claimed = 0;
            };
            // (started ahead while the lease ran out: it loads the world now)
            if (HiddenServer.PreBooted) {
                HiddenServer.Go(failed);
            } else if (!HiddenServer.StartOn(SaveId, failed)) {
                EmpLog.Warning("Peer-hosted world: the hidden server could not be started, the world is let go");
                ElinNetBase.ReleasePatches();
                Let(laid.Handover, 0);
                _claimed = 0;
            }
        } finally {
            _claiming = false;
        }
    }

    /// <summary>
    ///     Off the game's thread: TAKE (waiting while the former keeper's lease runs out), then the world laid in the
    ///     save folder from this game's copy or from the head
    /// </summary>
    private static Laid TakeAndLay(ulong former, ulong me, WorldHandover.Copy? copy, string target)
    {
        var laid = new Laid();
        var until = DateTime.UtcNow.AddSeconds(ClaimSeconds);
        StoreLease? lease = null;
        while (lease is null) {
            var take = Store.Take();
            if (take.Ok) {
                lease = take.Value;
                break;
            }

            // the former keeper's lease (it is releasing, or it crashed a moment ago), or the store keeps the world
            // for the one the keeper named: waited for, then whoever holds it is joined
            var holder = take.Lease;
            var waiting = take.Error is "held" or "reserved" && DateTime.UtcNow < until &&
                          (take.Error == "reserved" || holder is null || holder.Steam == former);
            if (!waiting) {
                if (take.Error == "held" && holder is not null && holder.Steam != me) {
                    laid.HeldBy = holder.Steam;
                    laid.HeldName = holder.Name;
                    return laid;
                }

                if (take.Error == "reserved" && Store.Who() is { ReservedFor: var other and not 0 } && other != me) {
                    laid.HeldBy = other;
                    return laid;
                }

                laid.Why = "the store refused the lease: " + take.Error;
                return laid;
            }

            System.Threading.Thread.Sleep(3000);
        }

        laid.Handover = lease!.Handover;
        try {
            var head = Store.Head();
            laid.Head = head?.N ?? 0;
            var why = Lay(copy, head, target, laid);
            if (why.Length > 0) {
                laid.Why = why;
                Store.Release(lease.Handover, 0);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            laid.Why = ex.Message;
            Store.Release(lease.Handover, 0);
        }

        return laid;
    }

    /// <summary>
    ///     The newer of this game's copy and the head, laid as the save folder: the copy when it is newer (a keeper
    ///     that crashed after its last save to the guests and before its last version), or when it holds the very
    ///     files of the head (nothing to download). A head made by a rollback always wins
    /// </summary>
    private static string Lay(WorldHandover.Copy? copy, StoreVersion? head, string target, Laid laid)
    {
        (string Dir, WorldCopyManifest? Manifest) local = copy is { } c ? WorldHandover.Read(c) : ("", null);
        var useCopy = false;
        if (local.Manifest is { Incomplete: false } manifest && copy is { } have) {
            if (head is null) {
                useCopy = true;
            } else if (head.Reason != "rollback") {
                useCopy = SameFiles(manifest, head) || have.IsNewerThan((int)head.Handover, head.SavedUtc);
            }

            if (useCopy && !SameFiles(manifest, head)) {
                laid.Reason = "crash-recovery";
            }
        }

        if (!useCopy && head is null) {
            return "the world has no version in the store and this game has no copy of it";
        }

        var fresh = target + ".emp";
        IO.DeleteDirectory(fresh);
        if (useCopy) {
            laid.From = "this game's copy " + copy;
            var why = WorldTakeover.Lay(copy!.Value, fresh);
            if (why.Length > 0) {
                return why;
            }
        } else {
            laid.From = "the store's version " + head!.N;
            Directory.CreateDirectory(fresh);
            var why = Store.Download(head, fresh, local.Manifest is not null ? local.Dir : null, laid.Handover);
            if (why.Length > 0) {
                IO.DeleteDirectory(fresh);
                return why;
            }
        }

        // the folder this world was opened from last time on this machine is kept aside once, never a player's save
        var old = target + ".old";
        IO.DeleteDirectory(old);
        if (Directory.Exists(target)) {
            Directory.Move(target, old);
        }

        Directory.Move(fresh, target);
        return "";
    }

    private static bool SameFiles(WorldCopyManifest copy, StoreVersion? head)
    {
        if (head is null || head.Files.Length != copy.Files.Length) {
            return false;
        }

        var files = head.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
        return copy.Files.All(f => files.TryGetValue(f.Path, out var hash) && string.Equals(hash, f.Hash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     A player at the title screen: the newest whole copy it keeps of the peer-hosted world (every keeper opens it
    ///     from the same save id), or none
    /// </summary>
    private static WorldHandover.Copy? BestPeerCopy()
    {
        try {
            if (!Directory.Exists(WorldCopyStore.Root)) {
                return null;
            }

            return Directory.GetDirectories(WorldCopyStore.Root, "*_" + SaveId)
                .SelectMany(WorldCopyStore.Copies)
                .Where(c => !c.Manifest.Incomplete && c.Manifest.World == SaveId)
                .OrderByDescending(c => c.Manifest.Handover).ThenByDescending(c => c.Manifest.Saved)
                .Select(c => (WorldHandover.Copy?)WorldHandover.Find(c.Manifest.Host, c.Manifest.World))
                .FirstOrDefault(c => c is not null);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    // ------------------------------------------------------------------------------ the guest lost its keeper

    private static float _nextLook;
    private static float _freeSince = -1f;
    private static float _claimFailedAt = float.MinValue;

    // a claim that failed (store away, refused) or a game that may not keep: not tried again for a minute
    private static bool _claimFailed
    {
        get => Time.realtimeSinceStartup - _claimFailedAt < 60f;
        set => _claimFailedAt = value ? Time.realtimeSinceStartup : float.MinValue;
    }

    /// <summary>
    ///     NetReconnect, every few seconds while the keeper is lost without a word: the store says whether its lease
    ///     still runs; once it is free, this game takes the world when its turn in the last succession list comes, a
    ///     lease taken by another player is followed. True: the election handles it (NetReconnect only tries to join)
    /// </summary>
    /// <summary>
    ///     While the keeper is gone: the name of the player who took the world and opens it, null while the lost
    ///     keeper's turn has not ended (NetReconnect's notice)
    /// </summary>
    internal static string? Opening { get; private set; }

    internal static bool WhileLost()
    {
        if (!Enabled) {
            return false;
        }

        if (_claiming || Time.realtimeSinceStartup < _nextLook) {
            return true;
        }

        // (a new loss of the keeper: what an earlier one found is forgotten)
        if (Time.realtimeSinceStartup - _nextLook > 15f) {
            _freeSince = -1f;
            Opening = null;
            _preBootTried = false;
        }

        _nextLook = Time.realtimeSinceStartup + 5f;
        var former = WorldHandover.LastKeeper;
        var me = Me;
        Later(() => Store.Who(), (who, _) => {
            if (who is null || _claiming) {
                return;
            }

            // the first of the list starts its hidden server's engine while the lost keeper's lease runs out
            if (!_preBootTried && WorldHandover.Position(me) == 0 && (who.Free || who.Lease!.Steam == former) &&
                !who.ReservedForOther(me) && CanKeep(who) is null) {
                _preBootTried = true;
                HiddenServer.PreBoot(SaveId);
            }

            if (!who.Free) {
                _freeSince = -1f;
                if (who.Lease!.Steam != former && who.Lease.Key != who.MyKey) {
                    HiddenServer.DropPreBoot("another player took the world");
                    Opening = who.Lease.Name ?? "";
                    WorldHandover.FollowKeeper(who.Lease.Steam);
                }

                return;
            }

            if (who.ReservedForOther(me)) {
                HiddenServer.DropPreBoot("the world is kept for another player");
                WorldHandover.FollowKeeper(who.ReservedFor);
                return;
            }

            Opening = null;
            if (_freeSince < 0f) {
                _freeSince = Time.realtimeSinceStartup;
            }

            // the first of the list at once, the next ones 10 s apart (a courtesy: the store gives the lease to whoever
            // asks first); a player the list does not name comes after all of them
            var position = WorldHandover.Position(me);
            if (Time.realtimeSinceStartup - _freeSince < position * TurnSeconds) {
                return;
            }

            if (_claimFailed) {
                return;
            }

            if (CanKeep(who) is { } why) {
                EmpLog.Information("Peer-hosted world: the keeper is gone and this game may not keep the world ({Why})", why);
                HiddenServer.DropPreBoot("this game may not keep the world");
                _claimFailed = true;
                return;
            }

            EmpLog.Information("Peer-hosted world: the keeper's lease is free, taking the world (place {Position} in the list)", position);
            EmpPop.Information("emp_takeover_begin".lang());
            Claim(former);
        });
        return true;
    }

    // a hidden server was started ahead for this loss of the keeper (once per loss)
    private static bool _preBootTried;

    internal static void LinkBack()
    {
        HiddenServer.DropPreBoot("the keeper is back");
        _preBootTried = false;
        Opening = null;
        _freeSince = -1f;
        _claimFailed = false;
    }

    // ------------------------------------------------------------------------------ between the two games

    private static string LeaseFile => Path.Combine(HiddenServer.Folder, "peer_lease.txt");

    private static void WriteLeaseFile(Laid laid)
    {
        try {
            Directory.CreateDirectory(HiddenServer.Folder);
            File.WriteAllLines(LeaseFile, [laid.Handover.ToString(), laid.Head.ToString(), laid.Reason]);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "Peer-hosted world: the lease file could not be written");
        }
    }

    private static string ReadLeaseReason()
    {
        try {
            var lines = File.Exists(LeaseFile) ? File.ReadAllLines(LeaseFile) : [];
            File.Delete(LeaseFile);
            return lines.Length > 2 && lines[2] is "opening" or "crash-recovery" ? lines[2] : "opening";
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return "opening";
        }
    }

    // ===================================================================================== the hidden server

    private static long _handover;
    private static long _parent;
    private static float _nextBeat;
    private static float _nextPut;
    private static bool _dirty;
    private static string _putReason = "";
    private static bool _putting;
    private static long _rollback;
    private static float _rollbackAt;
    private static StoreLease? _lostTo;
    private static ulong[] _succession = [];
    private static float _nextSuccession;
    private static readonly Dictionary<ulong, float> _since = [];

    /// <summary>
    ///     The keeper's succession list as last computed (the hidden server), the first one takes over
    /// </summary>
    internal static ulong[] Succession => _succession;

    private static bool Keeping => Enabled && ElinNetHost.PeerHostedServer && _handover > 0;

    /// <summary>
    ///     This hidden server keeps the public world: its lobby carries the store's host certificate (plan P3)
    /// </summary>
    internal static bool KeepsPublic => Keeping && _lostTo is null && EtWorldDepot.WorldName == Net.Steam.HostCertificate.World;

    private static ulong _certLobby;

    /// <summary>
    ///     The hidden server opened its Steam lobby (PublicServerCertificate): a certificate naming it is asked, each
    ///     BEAT renews it after that
    /// </summary>
    internal static void LobbyOpened(ulong lobby)
    {
        if (!Keeping || lobby == 0) {
            return;
        }

        _certLobby = lobby;
        var handover = _handover;
        Later(() => Store.Cert(handover, lobby), (reply, _) => {
            if (reply is { Ok: true }) {
                Net.Steam.PublicServerCertificate.Set(reply.Value);
            } else {
                EmpLog.Warning("Peer-hosted world: no host certificate for lobby {Lobby}: {Why}", lobby, reply?.Error ?? "no answer");
            }
        });
    }

    /// <summary>
    ///     The hidden server, before its session opens: the lease is this machine's (the visible game took it; taken
    ///     again here, which renews it) and its handover number goes in the save and the lobby. Null when kept, else
    ///     why not (the session does not open)
    /// </summary>
    internal static string? ServerOpening()
    {
        if (!Enabled || !ElinNetHost.PeerHostedServer) {
            return null;
        }

        try {
            var take = Store.Take();
            if (!take.Ok || take.Value is not { } lease) {
                return "the store refused the lease: " + take.Error;
            }

            _handover = lease.Handover;
            _parent = Store.Head()?.N ?? 0;
        } catch (Exception ex) {
            return "the store did not answer: " + ex.GetBaseException().Message;
        }

        ElinNetHost.SetHandoverNumber((int)Math.Min(int.MaxValue, _handover));
        ElinNetHost.KeeperOpened();
        _putReason = ReadLeaseReason();
        _dirty = true;
        // (the opening version: what this keeper starts from, a moment after the session opened)
        _nextPut = Time.unscaledTime + 5f;
        _nextBeat = Time.unscaledTime + BeatSeconds;
        _rollback = 0;
        _lostTo = null;
        EmpLog.Information("Peer-hosted world: kept by this machine, handover {Handover}, head {Head}", _handover, _parent);
        // (a new keeping: only this one counts for the next order)
        _slowSeconds = 0f;
        _slowLook = -1f;
        return null;
    }

    /// <summary>
    ///     The hidden server saved the world
    /// </summary>
    internal static void ServerSaved()
    {
        if (Keeping && _rollback == 0) {
            _dirty = true;
        }
    }

    /// <summary>
    ///     The hidden server's tick (EmpServer, every 2 s)
    /// </summary>
    internal static void ServerTick(ElinNetHost host)
    {
        if (!Keeping || host.IsZoneSession) {
            return;
        }

        CountSlow();
        var now = Time.unscaledTime;
        if (now >= _nextSuccession) {
            _nextSuccession = now + SuccessionSeconds;
            _succession = ComputeSuccession(host);
        }

        if (_rollback > 0 && now >= _rollbackAt) {
            _rollbackAt = float.MaxValue;
            EmpLog.Information("Peer-hosted world: rolled back to version {Version}, handing the world over", _rollback);
            EmpServer.StopNow();
        }

        BeatIfDue(host);

        if (_dirty && !_putting && _rollback == 0 && _lostTo is null && now >= _nextPut) {
            Put(_putReason.Length > 0 ? _putReason : "periodic", null);
        }
    }

    // (also while the world is handed over: the lease must not run out under the last version)
    private static void BeatIfDue(ElinNetHost host)
    {
        if (_lostTo is not null || _handover == 0 || Time.unscaledTime < _nextBeat) {
            return;
        }

        _nextBeat = Time.unscaledTime + BeatSeconds;
        var (handover, succession) = (_handover, _succession);
        Later(() => Store.Beat(handover, succession), (reply, _) => OnBeat(host, reply));
    }

    private static void OnBeat(ElinNetHost host, StoreReply<StoreBeat>? reply)
    {
        if (reply is null) {
            // (the store is away: the lease runs 30 s, the next beat may reach it)
            return;
        }

        if (reply.Ok) {
            if (reply.Value!.Rollback > 0) {
                RolledBack(host, reply.Value.Rollback);
            } else if (reply.Value.Cert is { Length: > 0 } cert) {
                Net.Steam.PublicServerCertificate.Set(cert);
            } else if (_certLobby == 0 && Net.Steam.PublicServerCertificate.OwnLobby() is var lobby and not 0) {
                // (a lobby opened before this machine held the lease: asked now)
                LobbyOpened(lobby);
            }

            return;
        }

        if (reply.Error is "not_holder" or "banned") {
            // cut off longer than the lease, or banned from keeping: another keeper may be opening the world, two
            // games must never go on with it. Nothing more is saved to the store, the guests are sent to the holder
            _lostTo = reply.Lease ?? new StoreLease();
            Net.Steam.PublicServerCertificate.Set(null);
            EmpLog.Warning("Peer-hosted world: the lease is lost ({Why}, now {Holder}), this server stops", reply.Error, _lostTo.Steam);
            EmpServer.StopNow();
        }
    }

    private static void RolledBack(ElinNetHost host, long version)
    {
        if (_rollback > 0) {
            return;
        }

        _rollback = version;
        _rollbackAt = Time.unscaledTime + RollbackSeconds;
        _dirty = false;
        EmpLog.Warning("Peer-hosted world: an admin rolled the world back to version {Version}: nothing more is saved", version);
        try {
            host.RunOrder("say " + "emp_peer_rollback".lang());
        } catch (Exception ex) {
            EmpLog.Information(ex, "Peer-hosted world: the rollback could not be announced");
        }
    }

    /// <param name="done">on the game's thread once the store answered (or not)</param>
    private static void Put(string reason, Action? done)
    {
        _putting = true;
        _dirty = false;
        _putReason = "";
        _nextPut = Time.unscaledTime + PutSeconds;
        var folder = GameIO.pathCurrentSave;
        var (parent, handover) = (_parent, _handover);
        var date = EClass.world?.date?.GetText(Date.TextFormat.Log) ?? "";
        var time = DateTime.UtcNow.ToString("o");
        Later(() => {
            // (FastJoinsAndSaves: the files of the save may still be on their way to the disk)
            SaveWriter.Wait();
            var reply = Store.Put(folder, parent, handover, reason, date, time);
            // a version of this lease whose answer was lost: the head is ours, put again over it
            if (reply.Error == "parent_mismatch" && reply.Number > parent) {
                reply = Store.Put(folder, reply.Number, handover, reason, date, time);
            }

            return reply;
        }, (reply, _) => {
            _putting = false;
            if (reply is { Ok: true }) {
                _parent = reply.Value;
                EmpLog.Information("Peer-hosted world: version {Version} ({Reason}) in the store", reply.Value, reason);
            } else if (reply?.Error == "rolled_back" && NetSession.Instance.Transport is ElinNetHost host) {
                RolledBack(host, reply.Number);
            } else {
                // the next time: whatever the store said, the save is on this machine and with every guest
                _dirty = _rollback == 0;
                EmpLog.Warning("Peer-hosted world: version ({Reason}) not taken by the store: {Why}", reason, reply?.Error ?? "no answer");
            }

            done?.Invoke();
        });
    }

    /// <summary>
    ///     Who keeps the world after this keeper, in order: the players of the world (never this machine's own seat,
    ///     never a banned one), a whole copy of the last save first, then the longest connected, then the smallest
    ///     Steam id. Advisory (the store gives the lease to whoever asks first): every guest waits for its turn
    /// </summary>
    /// <summary>
    ///     Player -> the minutes its own PC was too slow (its LoadCap closed) during its last keeping of this world,
    ///     measured by that keeper's server itself, kept in the save and so in every copy. A keeper that struggled
    ///     goes after the others next time (council 2026-11-07 (3): the world moves to stronger PCs at the natural
    ///     turns, no handover pause, and no player's own word ranks anyone)
    /// </summary>
    [ElinGameIOProperty("peer_weak_keepers")]
    private static Dictionary<ulong, int> WeakKeepers
    {
        get => field ??= [];
        set;
    }

    // (not const: the bench lowers it)
    internal static int WeakMinutes = 10;

    private static float _slowSeconds;
    private static float _slowLook = -1f;

    internal static bool IsWeakKeeper(ulong user) => WeakKeepers.TryGetValue(user, out var minutes) && minutes >= WeakMinutes;

    // the server's tick: how long this keeping has been too slow so far
    private static void CountSlow()
    {
        var now = Time.unscaledTime;
        if (_slowLook >= 0f && ElinNetHost.LoadCap is not null) {
            _slowSeconds += now - _slowLook;
        }

        _slowLook = now;
        WeakKeepers[PlayerId] = (int)(_slowSeconds / 60f);
    }

    private static ulong[] ComputeSuccession(ElinNetHost host)
    {
        var keeper = PlayerId;
        var now = Time.unscaledTime;
        var playing = NetSession.Instance.CurrentPlayers.Where(p => p is not null && p.Index != 0)
            .Select(p => (User: (ulong)p.User, Peer: p.Index))
            .Where(p => p.User != 0 && p.User != keeper && p.User != ElinNetHost.OwnerSeat && p.User != ElinNetHost.LocalUserId &&
                        !PeerHosting.IsBanned(p.User))
            .GroupBy(p => p.User).Select(g => g.First()).ToList();
        foreach (var gone in _since.Keys.Where(u => playing.All(p => p.User != u)).ToList()) {
            _since.Remove(gone);
        }

        foreach (var p in playing.Where(p => !_since.ContainsKey(p.User))) {
            _since[p.User] = now;
        }

        // (the ping this server measures to each, in 50 ms steps so the order does not churn: a player can only make it
        // worse, never better)
        var pings = NetSession.Instance.CurrentPlayers.Where(p => p is not null)
            .GroupBy(p => (ulong)p.User).ToDictionary(g => g.Key, g => (int)(g.First().AvgPingMs / 50f));
        return playing
            .OrderByDescending(p => host.HasLatestCopy(p.User))
            .ThenBy(p => IsWeakKeeper(p.User))
            .ThenBy(p => pings.TryGetValue(p.User, out var step) ? step : 0)
            .ThenBy(p => _since[p.User])
            .ThenBy(p => p.User)
            .Select(p => p.User).Take(MaxSuccession).ToArray();
    }

    // ------------------------------------------------------------------------------ the keeper leaves

    private static bool _handingOver;
    private static bool _handedOver;
    private static float _handoverStarted;

    /// <summary>
    ///     The next keeper this one names when it leaves (HostLeaving.Next), 0: none
    /// </summary>
    internal static ulong Next { get; private set; }

    /// <summary>
    ///     The hidden server is asked to stop (its player left, or a rollback, or the lease was lost): true while the
    ///     world is being handed over, the stop waits (at most <see cref="HandoverSeconds" />)
    /// </summary>
    internal static bool HoldStop(ElinNetHost host)
    {
        // (once under way it runs to its end: the lease is let go before the guests are told)
        if (_handedOver || (!_handingOver && !Keeping)) {
            return false;
        }

        if (!_handingOver) {
            _handingOver = true;
            _handoverStarted = Time.unscaledTime;
            EmpMod.Instance.StartCoroutine(HandOver(host));
        }

        BeatIfDue(host);
        if (Time.unscaledTime - _handoverStarted > HandoverSeconds) {
            EmpLog.Warning("Peer-hosted world: the handover took too long, the server stops as it is");
            _handedOver = true;
        }

        return !_handedOver;
    }

    private static IEnumerator HandOver(ElinNetHost host)
    {
        var session = NetSession.Instance;
        _succession = ComputeSuccession(host);
        var connected = new HashSet<ulong>(session.CurrentPlayers.Where(p => p is not null).Select(p => (ulong)p.User));
        Next = _lostTo is { Steam: not 0 } lost ? lost.Steam : _succession.FirstOrDefault(connected.Contains);
        EmpLog.Information("Peer-hosted world: leaving, next keeper {Next} of {Count} in the list", Next, _succession.Length);

        // the last save to every guest, then (before they hear that the keeper leaves) the last version and the lease
        // given to the next one: it can take the world the moment it is told
        var owes = ElinNetHost.OwesGuestsLastCopy;
        var given = false;
        if (owes) {
            try {
                EClass.game.Save(false, true);
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Peer-hosted world: the last save could not be made, the guests keep the copy they have");
            }

            ElinNetHost.LeaveWithLastCopy(false, () => given = true, BeforeLeaving);
            while (!given) {
                yield return null;
            }
        } else {
            if (_rollback == 0 && _lostTo is null) {
                try {
                    EClass.game.Save(false, true);
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Peer-hosted world: the last save could not be made");
                }
            }

            var before = BeforeLeaving();
            while (before.MoveNext()) {
                yield return before.Current;
            }

            host.AnnounceLeaving();
        }

        _handedOver = true;
    }

    /// <summary>
    ///     Between the last save given to the guests and the word that the keeper leaves: the last version (not after
    ///     a rollback, not when the lease is lost) and RELEASE next=S
    /// </summary>
    private static IEnumerator BeforeLeaving()
    {
        // (its own time from here: the last save to the guests may have taken a good part of the handover's)
        var began = Time.unscaledTime;
        if (_rollback == 0 && _lostTo is null) {
            var reason = _succession.Length > 0 && NetSession.Instance.CurrentPlayers.Any(p => p is not null && _succession.Contains((ulong)p.User))
                ? "handover"
                : "last-leave";
            var put = false;
            // (a version on its way is waited for: its parent is the one this goes over)
            while (_putting && Time.unscaledTime - began < 20f) {
                yield return null;
            }

            Put(reason, () => put = true);
            while (!put && Time.unscaledTime - began < 45f) {
                yield return null;
            }
        }

        if (_lostTo is not null) {
            yield break;
        }

        var released = false;
        var (handover, next) = (_handover, Next);
        Later(() => Store.Release(handover, next), (reply, _) => {
            released = true;
            EmpLog.Information("Peer-hosted world: lease released, next {Next}: {Result}", next, reply?.Ok == true ? "ok" : reply?.Error ?? "no answer");
        });
        while (!released && Time.unscaledTime - began < 55f) {
            yield return null;
        }

        _handover = 0;
        Net.Steam.PublicServerCertificate.Set(null);
    }
}
