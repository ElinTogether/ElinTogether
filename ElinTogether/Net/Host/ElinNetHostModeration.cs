using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     Admin tools (council 2026-10-27, dev/COUNCIL_2026-10-27_admin_tools.md): a kick that says why and keeps the
///     player out a few minutes, bans by Steam id kept in the world save (they follow the depot, the world copy and a
///     takeover). Checked when a player connects, before the world is sent
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     Steam id -> why it is banned (may be empty)
    /// </summary>
    [ElinGameIOProperty("mod_bans")]
    internal static Dictionary<ulong, string> Bans
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Steam id -> why it is muted (may be empty)
    /// </summary>
    [ElinGameIOProperty("mod_mutes")]
    internal static Dictionary<ulong, string> Mutes
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Steam ids the host made admins of this world: they kick, ban and mute other players (not the host, not
    ///     another admin). Only the host or the server's operator makes or unmakes them
    /// </summary>
    [ElinGameIOProperty("mod_admins")]
    internal static Dictionary<ulong, int> Admins
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     Steam ids let in by the host or the operator (allow) while MembersOnly is ticked, besides the players of
    ///     this world
    /// </summary>
    [ElinGameIOProperty("mod_members")]
    internal static Dictionary<ulong, int> Members
    {
        get => field ??= [];
        set;
    }

    /// <summary>
    ///     A player of this world (it has a character here, or played the host's), an admin, or one let in
    /// </summary>
    internal static bool IsMember(ulong user)
    {
        return SavedRemoteCharas.ContainsKey(user) || PlayerRosters.ContainsKey(user) || PcOwners.ContainsKey(user) ||
               Admins.ContainsKey(user) || Members.ContainsKey(user);
    }

    /// <summary>
    ///     Members only (box MembersOnly): nobody new comes in, a Steam friend or a public game's stranger alike
    /// </summary>
    internal static bool MembersOnlyRefuses(ulong user)
    {
        return EmpConfig.Server.MembersOnly.Value && !IsMember(user);
    }

    internal static string Allow(ulong user)
    {
        Members[user] = 1;
        AdminLog($"allow {user}");
        return $"{user} may come in";
    }

    // Steam id -> until when a kicked player is kept out (real time; forgotten at a restart)
    private static readonly Dictionary<ulong, float> KeptOut = [];

    /// <summary>
    ///     Why this player may not come in now, as the reason its game shows; null when it may
    /// </summary>
    private static string? Refusal(ulong user)
    {
        if (Bans.TryGetValue(user, out var why)) {
            return EmpDisconnectInfo.With(EmpDisconnectInfo.Banned, why);
        }

        if (MembersOnlyRefuses(user)) {
            return EmpDisconnectInfo.MembersOnly;
        }

        if (KeptOut.TryGetValue(user, out var until)) {
            var left = until - Time.realtimeSinceStartup;
            if (left > 0f) {
                return EmpDisconnectInfo.With(EmpDisconnectInfo.KickWait, Mathf.CeilToInt(left / 60f).ToString());
            }

            KeptOut.Remove(user);
        }

        return null;
    }

    /// <summary>
    ///     The player at that index leaves, told why; it may come back after KickKeepOutMinutes
    /// </summary>
    internal string Kick(int peerIndex, string? reason = null)
    {
        if (peerIndex == 0 || Socket.Peers.FirstOrDefault(p => p.Id == peerIndex) is not { } peer) {
            return $"No player at index {peerIndex}";
        }

        var minutes = EmpConfig.Server.KickKeepOutMinutes.Value;
        if (minutes > 0) {
            KeptOut[peer.User] = Time.realtimeSinceStartup + minutes * 60f;
        }

        EmpLog.Information("Admin: kicked {@Peer} ({Reason}), kept out {Minutes} min", peer, reason ?? "", minutes);
        AdminLog($"kick {(ulong)peer.User} {peer.User.Name} ({reason}), kept out {minutes} min");
        Socket.Disconnect(peer, EmpDisconnectInfo.With(EmpDisconnectInfo.HostKick, reason));
        return $"Kicked {peer.User.Name} ({peer.User})";
    }

    /// <summary>
    ///     That Steam account no longer comes into this world, playing now or not
    /// </summary>
    internal string Ban(ulong user, string? reason = null)
    {
        ulong me = Helper.ServerAvatar.HostUser;
        if (user == me) {
            return "The host cannot ban itself";
        }

        Bans[user] = reason?.Trim() ?? "";
        NoteSessionBan(user, true);
        EmpLog.Information("Admin: banned {User} ({Reason})", user, reason ?? "");
        AdminLog($"ban {user} ({reason})");
        foreach (var peer in Socket.Peers.Where(p => (ulong)p.User == user).ToList()) {
            Socket.Disconnect(peer, EmpDisconnectInfo.With(EmpDisconnectInfo.Banned, reason));
        }

        return $"Banned {user}";
    }

    internal string Mute(ulong user, string? reason = null)
    {
        ulong me = Helper.ServerAvatar.HostUser;
        if (user == me) {
            return "The host cannot mute itself";
        }

        Mutes[user] = reason?.Trim() ?? "";
        AdminLog($"mute {user} ({reason})");
        TellModeration();
        return $"Muted {user}";
    }

    internal string Unmute(ulong user)
    {
        if (!Mutes.Remove(user)) {
            return $"{user} was not muted";
        }

        AdminLog($"unmute {user}");
        TellModeration();
        return $"Unmuted {user}";
    }

    /// <summary>
    ///     Every game learns who is muted: at each change and when a player comes in (SendJoinCopies)
    /// </summary>
    internal void TellModeration()
    {
        if (IsZoneSession) {
            return;
        }

        ModerationDelta.Muted.Clear();
        ModerationDelta.Muted.UnionWith(Mutes.Keys);
        ModerationDelta.AdminIds.Clear();
        ModerationDelta.AdminIds.UnionWith(Admins.Keys);
        ModerationDelta.ModeratorIds.Clear();
        ModerationDelta.ModeratorIds.UnionWith(Moderators.Keys);
        ModerationDelta.FrozenIds.Clear();
        ModerationDelta.FrozenIds.UnionWith(Frozen.Keys);
        Delta.AddRemote(new ModerationDelta {
            Mutes = [..Mutes.Keys],
            Admins = [..Admins.Keys],
            Moderators = [..Moderators.Keys],
            Frozen = [..Frozen.Keys],
        });
    }

    /// <summary>
    ///     A line that came from a muted player (hard check, whatever EnforceOwnership says)
    /// </summary>
    internal bool FromMuted(int peerId)
    {
        return Socket.Peers.FirstOrDefault(p => p.Id == peerId) is { } peer && ModerationDelta.IsMuted(peer.User);
    }

    // chat flood limit (council 2026-10-28, ops): lines in the last ChatWindow seconds per player, and who is silenced until when
    internal const int ChatBurst = 8;
    internal const float ChatWindow = 10f;
    internal const float ChatMuteSeconds = 30f;
    private readonly Dictionary<int, Queue<float>> _chatTimes = [];
    private readonly Dictionary<int, float> _chatSilencedUntil = [];

    /// <summary>
    ///     A typed line (public chat or whisper) of that player is over the flood limit: more than 8 lines in 10 s
    ///     silence it for 30 s, told to that player only. Where trust is enforced (public servers); the host's admins
    ///     are not limited. The line is dropped, nothing reaches the others
    /// </summary>
    internal bool ChatFlooded(int peerId)
    {
        if (!ElinDelta.TrustEnforced || Socket.Peers.FirstOrDefault(p => p.Id == peerId) is not { } peer || Admins.ContainsKey(peer.User)) {
            return false;
        }

        var now = Time.realtimeSinceStartup;
        if (_chatSilencedUntil.TryGetValue(peerId, out var until)) {
            if (now < until) {
                return true;
            }

            _chatSilencedUntil.Remove(peerId);
        }

        if (!_chatTimes.TryGetValue(peerId, out var times)) {
            _chatTimes[peerId] = times = new();
        }

        while (times.Count > 0 && now - times.Peek() > ChatWindow) {
            times.Dequeue();
        }

        times.Enqueue(now);
        if (times.Count <= ChatBurst) {
            return false;
        }

        times.Clear();
        _chatSilencedUntil[peerId] = now + ChatMuteSeconds;
        EmpLog.Information("Chat of {@Peer} silenced for {Seconds} s: over {Burst} lines in {Window} s", peer, ChatMuteSeconds, ChatBurst, ChatWindow);
        peer.Send(new MsgSayDelta {
            Text = "emp_chat_flood".lang(),
            R = 1,
            G = 1,
            B = 1,
            A = 1,
            Notice = true,
            Name = "[Server]",
        });
        return true;
    }

    internal string MakeAdmin(ulong user, bool admin)
    {
        if (PeerHostedServer) {
            return "refused: the admins of a peer-hosted world are its signed list";
        }

        if (admin) {
            Admins[user] = 1;
        } else if (!Admins.Remove(user)) {
            return $"{user} was not an admin";
        }

        AdminLog($"{(admin ? "admin" : "unadmin")} {user}");
        TellModeration();
        return $"{user} {(admin ? "is now an admin" : "is no longer an admin")}";
    }

    /// <summary>
    ///     An admin's order (AdminCommandDelta), checked here: from an admin of this world, about a player who is
    ///     neither the host nor an admin
    /// </summary>
    internal void RunAdminOrder(int originPeer, AdminCommandDelta order)
    {
        ulong host = Helper.ServerAvatar.HostUser;
        var sender = Socket.Peers.FirstOrDefault(p => p.Id == originPeer);
        ulong from = sender is null ? 0UL : sender.User;
        // announcements and restart warnings are about no player (Reason is the text, or the minutes)
        if (order.Verb is "say" or "restart" or "restart_cancel") {
            var allowed = sender is not null && Admins.ContainsKey(from);
            var result = !allowed ? "refused: not an admin" : order.Verb switch {
                "say" => Announce(order.Reason ?? ""),
                "restart" => WarnRestart(int.TryParse(order.Reason, out var m) ? m : 10),
                _ => CancelRestart(),
            };
            AdminLog($"by admin {from} {sender?.User.Name}: {order.Verb}: {result}");
            return;
        }

        // a moderator (admin extras) only mutes, unmutes and kicks, and not a fellow moderator
        var admin = Admins.ContainsKey(from);
        var moderator = !admin && AdminExtrasOn && Moderators.ContainsKey(from) &&
                        order.Verb is "kick" or "mute" or "unmute" && !Moderators.ContainsKey(order.Target);
        if (sender is null || !(admin || moderator) || order.Target == host || Admins.ContainsKey(order.Target)) {
            AdminLog($"refused order {order.Verb} {order.Target} from {from}: not an admin or moderator for it, or about the host or an admin");
            return;
        }

        var target = Socket.Peers.FirstOrDefault(p => (ulong)p.User == order.Target);
        var done = order.Verb switch {
            "kick" => target is null ? "not playing" : Kick(target.Id, order.Reason),
            "ban" when admin => Ban(order.Target, order.Reason),
            "unban" when admin => Unban(order.Target),
            "mute" => Mute(order.Target, order.Reason),
            "unmute" => Unmute(order.Target),
            "freeze" when admin => Freeze(order.Target),
            "jail" when admin => Freeze(order.Target, true),
            "unfreeze" or "unjail" when admin => Release(order.Target),
            "setjail" when admin => SetJail(PlayerChara(originPeer)?.currentZone),
            _ => "unknown order",
        };
        AdminLog($"by admin {from} {sender.User.Name}: {order.Verb} {order.Target}: {done}");
    }

    /// <summary>
    ///     A line of the server to every player, the host's own included (admin announcement, council 2026-10-28)
    /// </summary>
    internal string Announce(string text)
    {
        text = text.Trim();
        if (text.Length == 0) {
            return "error: nothing to say";
        }

        text = text.Length > 300 ? text.Substring(0, 300) : text;
        SendDeltaToAllExcept(int.MinValue, new MsgSayDelta {
            Text = text,
            R = 1,
            G = 1,
            B = 1,
            A = 1,
            Notice = true,
            Name = "[Server]",
        });
        if (EClass.core.IsGameStarted) {
            MsgSayDelta.ShowNotice(text);
        }

        AdminLog($"say {text}");
        return "Said to everyone";
    }

    // when the restart is due (real time), and the warnings still to give, in minutes left (largest first)
    private static float _restartAt;
    private static readonly List<int> RestartWarnings = [];

    /// <summary>Minutes before an announced restart, or -1 when none is due (the Status page)</summary>
    internal static int RestartInMinutes => RestartWarnings.Count == 0 && _restartAt <= Time.realtimeSinceStartup ? -1
        : Mathf.Max(0, Mathf.CeilToInt((_restartAt - Time.realtimeSinceStartup) / 60f));

    // the official server's daily restart (2026-10-28): the warning ends in a stop, written as the operator's stop file
    // (saved, players sent away, then the application starts it again). A game a player hosts is never closed by it
    private static bool _restartStops;

    internal string WarnRestart(int minutes)
    {
        minutes = Mathf.Clamp(minutes, 1, 600);
        _restartAt = Time.realtimeSinceStartup + minutes * 60f;
        RestartWarnings.Clear();
        RestartWarnings.AddRange(new[] { minutes, 5, 1 }.Where(m => m <= minutes).Distinct().OrderByDescending(m => m));
        _restartStops = EmpServer.Requested;
        AdminLog($"restart warning {minutes} min");
        UpdateRestartWarnings();
        return $"Restart announced in {minutes} min";
    }

    internal string CancelRestart()
    {
        if (RestartWarnings.Count == 0) {
            return "no restart announced";
        }

        RestartWarnings.Clear();
        _restartStops = false;
        return Announce("emp_info_restart_cancel".lang());
    }

    /// <summary>
    ///     Each frame of the host: the next warning when its time came
    /// </summary>
    internal void UpdateRestartWarnings()
    {
        if (_restartStops && Time.realtimeSinceStartup >= _restartAt) {
            _restartStops = false;
            RestartWarnings.Clear();
            AdminLog("restart: time is up, stopping");
            EmpServer.StopNow();
            return;
        }

        if (RestartWarnings.Count == 0 || IsZoneSession) {
            return;
        }

        var left = Mathf.CeilToInt((_restartAt - Time.realtimeSinceStartup) / 60f);
        if (left > RestartWarnings[0]) {
            return;
        }

        RestartWarnings.RemoveAt(0);
        Announce("emp_info_restart".Loc(Mathf.Max(left, 1)));
    }

    internal static string Unban(ulong user)
    {
        // (a peer-hosted world: the store's list is the world's, lifted there only)
        if (PeerHostedServer && Helper.PeerHosting.IsBanned(user)) {
            return $"{user} is banned by the signed list of this world, not here";
        }

        NoteSessionBan(user, false);
        KeptOut.Remove(user);
        if (!Bans.Remove(user)) {
            return $"{user} was not banned";
        }

        EmpLog.Information("Admin: unbanned {User}", user);
        AdminLog($"unban {user}");
        return $"Unbanned {user}";
    }

    /// <summary>
    ///     One line per order, refusal, arrival and departure, with the Steam id: ElinMP/Logs/admin.log of the machine
    ///     that hosts (never in the world save, which would grow for ever)
    /// </summary>
    internal static void AdminLog(string line)
    {
        // one line: a reason or a name with line breaks wrote lines of its own, an order that never was (security
        // audit 2026-10-27, item 13)
        line = new string(line.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        try {
            var folder = Path.Combine(Application.persistentDataPath, "ElinMP", "Logs");
            Directory.CreateDirectory(folder);
            // one older file kept past 20 MB
            var file = Path.Combine(folder, "admin.log");
            if (File.Exists(file) && new FileInfo(file).Length > 20L * 1024 * 1024) {
                File.Copy(file, Path.Combine(folder, "admin.1.log"), true);
                File.Delete(file);
            }

            File.AppendAllText(Path.Combine(folder, "admin.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "Admin log not written: {Line}", line);
        }
    }

    /// <summary>
    ///     An order of the server's operator (ElinMP/server.cmd, see EmpServer), one line:
    ///     say &lt;text&gt;, or kick|ban|unban|mute|unmute &lt;Steam id or exact name&gt; [reason]. Returns what was done
    /// </summary>
    internal string RunOrder(string line)
    {
        // a line to every player, wherever they are, from the server's operator
        if (line.TrimStart().StartsWith("say ", StringComparison.OrdinalIgnoreCase)) {
            return Announce(line.Trim().Substring(4).Trim());
        }

        // restart [minutes]: the warning at that many minutes, then at 5 and 1 (council 2026-10-28)
        if (line.Trim().StartsWith("restart", StringComparison.OrdinalIgnoreCase)) {
            var rest = line.Trim().Substring(7).Trim();
            return rest == "cancel" ? CancelRestart() : WarnRestart(int.TryParse(rest, out var m) ? m : 10);
        }

        // the zone the host stands in becomes the jail
        if (line.Trim().Equals("setjail", StringComparison.OrdinalIgnoreCase)) {
            var jailed = SetJail(EClass._zone);
            AdminLog($"order \"setjail\": {jailed}");
            return jailed;
        }

        var words = line.Trim().Split([' '], 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) {
            return "error: " + line;
        }

        var reason = words.Length > 2 ? words[2] : null;
        // the Steam id first, a name only when no id matches and one player alone has it (security audit 2026-10-27,
        // item 13: a player named as another's id, or as another player, took the order in its place)
        var peer = Socket.Peers.FirstOrDefault(p => ((ulong)p.User).ToString() == words[1]);
        if (peer is null && !ulong.TryParse(words[1], out _)) {
            var named = Socket.Peers.Where(p => p.User.Name == words[1]).ToList();
            if (named.Count > 1) {
                return $"error: {named.Count} players are named {words[1]}, give the Steam id";
            }

            peer = named.FirstOrDefault();
        }

        ulong user = peer is not null ? peer.User : ulong.TryParse(words[1], out var id) ? id : 0UL;
        if (user == 0UL) {
            return "error: no player " + words[1];
        }

        var done = words[0].ToLowerInvariant() switch {
            "kick" => peer is null ? "error: " + words[1] + " is not playing" : Kick(peer.Id, reason),
            "ban" => Ban(user, reason),
            "unban" => Unban(user),
            "mute" => Mute(user, reason),
            "unmute" => Unmute(user),
            "allow" => Allow(user),
            "admin" => MakeAdmin(user, true),
            "unadmin" => MakeAdmin(user, false),
            "mod" => MakeModerator(user, true),
            "unmod" => MakeModerator(user, false),
            "freeze" => Freeze(user),
            "jail" => Freeze(user, true),
            "unfreeze" or "unjail" => Release(user),
            _ => "error: unknown order " + words[0],
        };
        AdminLog($"order \"{line.Trim()}\": {done}");
        return done;
    }
}
