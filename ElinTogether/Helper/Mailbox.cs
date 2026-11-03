using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper.Extensions;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using HarmonyLib;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Host box "Mailbox" (user-approved design 2026-10-30): a player writes to another player of this world, online or
///     not: a text, up to 5 stacks of its own bag and some gold. What the letter holds leaves the sender's bag at once
///     and is kept by the HOST only, in its world save (<see cref="ElinNetHost.MailLetters" />), as the game writes a
///     thing, the way a team box is kept (<see cref="TeamBoxes" />): no guest ever holds another player's mail. The
///     recipient takes it into its bag from the Mail page of the Players tab: the host checks the letter is addressed
///     to the character of the peer that asks, then makes the things again (new uids) in that character's bag, the way a
///     deposit from another world is put in a box (ElinNetHost.PutInWorldBox). <br />
///     A player who is offline is told on its next join. A letter whose items or gold nobody took in 30 real days goes
///     back to its sender, once (returned letters stay where they are): nothing is ever lost. <br />
///     Sending and taking are done on the host's map only (away, a player's bag is its own game's, the host cannot move
///     what it holds); reading, listing and deleting work anywhere. Every limit is the host's: 5 stacks, 500 letters of
///     text, gold the sender has, 20 letters waiting per recipient, one letter per 5 seconds per sender, muted players
///     cannot write. A letter from an ignored player is hidden by the recipient's own game (its personal list)
/// </summary>
[HarmonyPatch]
internal static class Mailbox
{
    internal const int MaxText = 500;
    internal const int MaxStacks = 5;
    internal const int MaxWaiting = 20;
    internal const long ReturnSeconds = 30L * 24 * 3600;

    internal const int FlagRead = 1;
    internal const int FlagTaken = 2;
    internal const int FlagReturned = 4;
    // its stacks could not be read when it was taken: kept as it is, listed for admins, who can try again
    internal const int FlagBroken = 8;

    /// <summary>
    ///     For the bench only (fault injection, mailbox_safety_check): called before each step that moves a stack or
    ///     gold ("destroy" i, "gold" 0, "give" i, "givegold" 0, "post" 0); what it throws is a step that failed
    /// </summary>
    internal static Action<string, int>? TestFault { get; set; }

    private const float SendGap = 5f;
    private const float ListGap = 1f;
    private const float ReturnCheckSeconds = 60f;

    // the row of a letter in the host's book
    private const int ToAt = 0, FromAt = 1, UserAt = 2, SentAt = 3, FlagsAt = 4, GoldAt = 5, TextAt = 6, NameAt = 7, ItemsAt = 8;

    // the game that keeps the letters: the host of the world (never a zone session)
    private static bool Keeps => NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false };

    /// <summary>
    ///     The box as this game knows it: the host's own setting, a guest's from the host's rules
    /// </summary>
    internal static bool On => NetSession.Instance is { InGame: true } net && EClass.pc != null &&
                               (net.Transport is ElinNetHost ? EmpConfig.Server.Mailbox.Value : net.Rules is { Mailbox: true });

    // ---------------------------------------------------------------- the host's book

    // sender chara uid -> when its last letter left; asker chara uid -> when its list was last sent
    private static readonly Dictionary<int, float> _lastSent = [];
    private static readonly Dictionary<int, float> _lastList = [];
    private static float _nextReturnCheck;
    private static Game? _seenGame;

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static int Int(string[] row, int at) => row.Length > at && int.TryParse(row[at], out var v) ? v : 0;

    private static long Long(string[] row, int at) => row.Length > at && long.TryParse(row[at], out var v) ? v : 0;

    private static ulong ULong(string[] row, int at) => row.Length > at && ulong.TryParse(row[at], out var v) ? v : 0;

    /// <summary>
    ///     A letter's row: see <see cref="ElinNetHost.MailLetters" />
    /// </summary>
    internal static string[] Row(int to, int from, ulong fromUser, long sent, int flags, int gold, string text, string fromName,
        IEnumerable<string> items)
    {
        return new[] { to.ToString(), from.ToString(), fromUser.ToString(), sent.ToString(), flags.ToString(), gold.ToString(), text, fromName }
            .Concat(items).ToArray();
    }

    private static bool IsEmpty(string[] row) => row.Length <= ItemsAt && Int(row, GoldAt) <= 0;

    /// <summary>
    ///     What a letter holds, for the journal's comparison: its gold and its stacks as written
    /// </summary>
    internal static string ContentOf(string[] row) => Math.Max(0, Int(row, GoldAt)) + "|" + string.Join("|", row.Skip(ItemsAt));

    private static bool IsAdmin(ulong user) => user == ServerAvatar.HostUser || ElinNetHost.Admins.ContainsKey(user);

    /// <summary>
    ///     The letters waiting for that player, returned ones too
    /// </summary>
    private static int Waiting(int charaUid) => ElinNetHost.MailLetters.Values.Count(r => Int(r, ToAt) == charaUid);

    /// <summary>
    ///     Who can be written to: every character a player has in this world (the host's list of saved characters) and
    ///     the host's own player, never a server's stand-in
    /// </summary>
    internal static List<(int Uid, string Name)> Directory(ElinNetHost host)
    {
        var list = new List<(int, string)>();
        if (EClass.pc is { } pc && !ServerAvatar.IsUid(pc.uid)) {
            list.Add((pc.uid, pc.NameSimple));
        }

        foreach (var (user, chara, _) in host.SavedCharacters()) {
            if (ServerAvatar.IsUid(chara.uid) || list.Exists(e => e.Item1 == chara.uid)) {
                continue;
            }

            var account = UserData.Get(user).Name;
            list.Add((chara.uid, string.IsNullOrWhiteSpace(account) ? chara.NameSimple : $"{chara.NameSimple} ({account})"));
        }

        return list;
    }

    private static string NameOf(ElinNetHost host, int charaUid)
    {
        return Directory(host).Find(e => e.Uid == charaUid).Name ?? Teams.PlayerName(charaUid);
    }

    private static MailLetter Letter(int id, string[] row)
    {
        var items = new List<byte[]>();
        for (var i = ItemsAt; i < row.Length; i++) {
            try {
                items.Add(Convert.FromBase64String(row[i]));
            } catch (FormatException) {
                items.Add([]);
            }
        }

        return new() {
            Id = id,
            From = Int(row, FromAt),
            FromName = row.Length > NameAt ? row[NameAt] : "",
            FromUser = ULong(row, UserAt),
            Sent = Long(row, SentAt),
            Flags = Int(row, FlagsAt),
            Gold = Int(row, GoldAt),
            Text = row.Length > TextAt ? row[TextAt] : "",
            Items = items,
        };
    }

    /// <summary>
    ///     What that player is told: its letters, oldest first, and who it can write to
    /// </summary>
    private static MailStateDelta State(ElinNetHost host, int charaUid, string reason = "", string done = "", bool notice = false)
    {
        var directory = Directory(host).Where(e => e.Uid != charaUid).ToList();
        return new() {
            Letters = ElinNetHost.MailLetters.Where(p => Int(p.Value, ToAt) == charaUid).OrderBy(p => p.Key).Select(p => Letter(p.Key, p.Value))
                .ToList(),
            Uids = directory.Select(e => e.Uid).ToArray(),
            Names = directory.Select(e => e.Name).ToArray(),
            Reason = reason,
            Done = done,
            Notice = notice,
        };
    }

    /// <summary>
    ///     That player's letters, to it, if it is here: the host's own player, or a connected guest (also one away)
    /// </summary>
    private static void TellOwner(ElinNetHost host, int charaUid, bool notice)
    {
        if (EClass.pc?.uid == charaUid) {
            Show(State(host, charaUid, notice: notice));
            return;
        }

        foreach (var peer in host.PeerIds.ToArray()) {
            if (host.PlayerChara(peer)?.uid == charaUid) {
                host.SendDeltaTo(peer, State(host, charaUid, notice: notice));
                return;
            }
        }
    }

    /// <summary>
    ///     Host: a guest's request. The asker is the character of that peer, never a uid the guest sent
    /// </summary>
    internal static void OnRequest(ElinNetHost host, int peer, MailRequestDelta request)
    {
        if (!Keeps || host.IsZoneSession || host.PlayerChara(peer) is not { } chara) {
            return;
        }

        // (sending and taking move things in the bag: only the host's copy of a character on its map is the real one)
        var here = host.ActiveRemoteCharas.TryGetValue(peer, out var onMap) && onMap == chara && !host.IsAwayPeer(peer) &&
                   host.IsSettled(peer);
        Handle(host, chara, host.UserOf(peer), here, host.FromMuted(peer), request, state => host.SendDeltaTo(peer, state));
    }

    private static void Handle(ElinNetHost host, Chara chara, ulong user, bool here, bool muted, MailRequestDelta request,
        Action<MailStateDelta> reply)
    {
        if (!On) {
            return;
        }

        string? reason = null;
        var done = "";
        // (every request, refused ones too: each answer carries the whole mailbox, review 2026-10-30)
        var now = Time.realtimeSinceStartup;
        // (a send has its own 5 s rule, which answers why it was refused)
        if (request.Kind != MailRequestDelta.Send) {
            if (_lastList.TryGetValue(chara.uid, out var last) && now - last < (request.Kind == MailRequestDelta.List ? ListGap : 0.3f)) {
                return;
            }

            _lastList[chara.uid] = now;
        }
        switch (request.Kind) {
            case MailRequestDelta.List:
                break;
            case MailRequestDelta.Read:
                if (ElinNetHost.MailLetters.TryGetValue(request.LetterId, out var read) && Int(read, ToAt) == chara.uid) {
                    read[FlagsAt] = (Int(read, FlagsAt) | FlagRead).ToString();
                }

                // (the reader's game marked it already: nothing to answer)
                return;
            case MailRequestDelta.Send:
                reason = SendLetter(host, chara, user, here, muted, request);
                done = reason is null ? "emp_mail_sent" : "";
                break;
            case MailRequestDelta.Take:
                reason = TakeLetter(host, chara, here, request.LetterId);
                done = reason is null ? "emp_mail_taken" : "";
                break;
            case MailRequestDelta.Delete:
                reason = DeleteLetter(chara, request.LetterId);
                break;
            case MailRequestDelta.Retry:
                if (!IsAdmin(user)) {
                    reason = Refuse(chara, "a retry of letter " + request.LetterId + " (not an admin)", "emp_mail_gone");
                    break;
                }

                reason = Retry(host, request.LetterId);
                done = reason is null ? "emp_mail_restored" : "";
                break;
            default:
                return;
        }

        var state = State(host, chara.uid, reason ?? "", done);
        // (the admins also see the letters that could not be read, with "Try again")
        if (IsAdmin(user)) {
            state.Broken = BrokenLetters(host);
        }

        reply(state);
    }

    private static string? SendLetter(ElinNetHost host, Chara from, ulong user, bool here, bool muted, MailRequestDelta request)
    {
        if (!here) {
            return "emp_mail_away";
        }

        if (muted) {
            return "emp_mail_muted";
        }

        var now = Time.realtimeSinceStartup;
        if (_lastSent.TryGetValue(from.uid, out var last) && now - last < SendGap) {
            return "emp_mail_wait";
        }

        // (a refused letter counts too: a stream of refused sends costs the host as much)
        _lastSent[from.uid] = now;

        var text = (request.Text ?? "").Trim();
        var uids = request.Items ?? [];
        if (text.Length > MaxText) {
            return Refuse(from, "a text of " + text.Length, "emp_mail_too_long");
        }

        if (request.Gold < 0 || request.Gold > from.GetCurrency()) {
            return Refuse(from, "gold " + request.Gold, "emp_mail_no_gold");
        }

        if (uids.Length > MaxStacks || uids.Distinct().Count() != uids.Length) {
            return Refuse(from, uids.Length + " stacks", "emp_mail_too_many");
        }

        if (request.To == from.uid || ServerAvatar.IsUid(from.uid) || !Directory(host).Exists(e => e.Uid == request.To)) {
            return Refuse(from, "recipient " + request.To, "emp_mail_no_recipient");
        }

        // (and 3 from one sender: text letters cost nothing, one player could fill another's box, review 2026-10-30)
        if (Waiting(request.To) >= MaxWaiting ||
            ElinNetHost.MailLetters.Values.Count(r => Int(r, ToAt) == request.To && Int(r, FromAt) == from.uid) >= 3) {
            return "emp_mail_full";
        }

        if (text.Length == 0 && uids.Length == 0 && request.Gold == 0) {
            return "emp_mail_empty";
        }

        // every stack must be in the sender's own bag, as the host has it, and be something a player may hand over
        var things = new List<Thing>();
        foreach (var uid in uids) {
            var thing = from.things.Find(uid);
            if (thing is null || thing.isDestroyed || thing.GetRootCard() != from || thing.isEquipped || PlayerTrade.Refuse(thing) is not null) {
                return Refuse(from, "item " + uid, "emp_mail_bad_item");
            }

            things.Add(thing);
        }

        // written first: nothing leaves the bag if one of them cannot be
        var packed = new List<string>();
        try {
            packed.AddRange(things.Select(t => Convert.ToBase64String(LZ4Bytes.Create(t).Bytes)));
        } catch (Exception ex) {
            EmpLog.Warning(ex, "A letter of {Uid} could not be written", from.uid);
            return "emp_mail_bad_item";
        }

        // the row is in the book before anything leaves the bag: whatever fails below, what left is in the letter
        var book = ElinNetHost.MailLetters;
        var id = Math.Max(ElinNetHost.MailLastId, book.Keys.DefaultIfEmpty().Max()) + 1;
        ElinNetHost.MailLastId = id;
        book[id] = Row(request.To, from.uid, user, Now(), 0, request.Gold, text, from.NameSimple, packed);

        // the things leave the world (everyone is told, as for any card); they exist only in the letter now. A step
        // that fails stops there: what did not leave stays in the bag and is taken out of the letter
        var moved = new List<string>();
        var goldBefore = from.GetCurrency();
        var failed = false;
        using (ElinDelta.Simulate()) {
            for (var i = 0; i < things.Count && !failed; i++) {
                try {
                    TestFault?.Invoke("destroy", i);
                    things[i].Destroy();
                } catch (Exception ex) {
                    failed = true;
                    EmpLog.Warning(ex, "Letter {Id}: stack {Uid} of {From} could not leave the bag", id, things[i].uid, from.uid);
                }

                if (things[i].isDestroyed) {
                    moved.Add(packed[i]);
                }
            }

            if (!failed && request.Gold > 0) {
                try {
                    TestFault?.Invoke("gold", 0);
                    from.ModCurrency(-request.Gold);
                } catch (Exception ex) {
                    failed = true;
                    EmpLog.Warning(ex, "Letter {Id}: the gold of {From} could not leave its purse", id, from.uid);
                }
            }
        }

        var gold = Mathf.Clamp(goldBefore - from.GetCurrency(), 0, request.Gold);
        if (failed && moved.Count == 0 && gold == 0) {
            book.Remove(id);
            EmpLog.Warning("Letter {Id} of {From} not sent: nothing left the bag", id, from.uid);
            return "emp_mail_bad_item";
        }

        var row = Row(request.To, from.uid, user, Now(), 0, gold, text, from.NameSimple, moved);
        book[id] = row;
        MailJournal.Record(id, from, null, row);
        _lastSent[from.uid] = now;
        if (failed) {
            EmpLog.Warning("Letter {Id} from {From}: {Moved} of {Stacks} stacks and {Gold} of {Asked} gold left the bag, the rest stays with the sender",
                id, from.uid, moved.Count, packed.Count, gold, request.Gold);
        }

        EmpLog.Information("Letter {Id} from {From} to {To}: {Stacks} stacks, {Gold} gold, {Length} letters of text",
            id, from.uid, request.To, moved.Count, gold, text.Length);
        TellOwner(host, request.To, true);
        return null;
    }

    /// <summary>
    ///     A letter from the world itself (no sender, never sent back), holding one thing: what the host gives a player
    ///     who is away or whose bag is full, instead of a parcel any player could pick up (user 2026-10-30)
    /// </summary>
    internal static bool Post(int toUid, Thing thing, string text, string fromName)
    {
        if (!Keeps) {
            return false;
        }

        string packed;
        try {
            packed = Convert.ToBase64String(LZ4Bytes.Create(thing).Bytes);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "A letter for {Uid} could not be written", toUid);
            return false;
        }

        // in the book first: the thing leaves the world only into the letter
        var book = ElinNetHost.MailLetters;
        var id = Math.Max(ElinNetHost.MailLastId, book.Keys.DefaultIfEmpty().Max()) + 1;
        ElinNetHost.MailLastId = id;
        var row = Row(toUid, 0, 0, Now(), FlagReturned, 0, text, fromName, [packed]);
        book[id] = row;
        try {
            TestFault?.Invoke("post", 0);
            thing.Destroy();
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Letter {Id} for {Uid}: {Thing} could not leave the world", id, toUid, thing.id);
        }

        if (!thing.isDestroyed) {
            // (still where it was: no letter, the caller gives it another way)
            book.Remove(id);
            return false;
        }

        // (the recipient carries the mark: its world is saved with the letter or before it)
        if (EClass.game.cards.globalCharas.Find(toUid) is { } recipient) {
            MailJournal.Record(id, recipient, null, row);
        }

        EmpLog.Information("Letter {Id} from the world to {To}: {Thing}", id, toUid, thing.id);
        if (NetSession.Instance.Transport is ElinNetHost host) {
            TellOwner(host, toUid, true);
        }

        return true;
    }

    private static string Refuse(Chara from, string what, string reason)
    {
        EmpLog.Warning("Refused a letter of {Uid}: {What}", from.uid, what);
        return reason;
    }

    private static string? TakeLetter(ElinNetHost host, Chara to, bool here, int id)
    {
        if (!ElinNetHost.MailLetters.TryGetValue(id, out var row) || Int(row, ToAt) != to.uid) {
            EmpLog.Warning("Refused to take letter {Id} for {Uid}: not addressed to it", id, to.uid);
            return "emp_mail_gone";
        }

        if (IsEmpty(row)) {
            return null;
        }

        if (!here) {
            return "emp_mail_away";
        }

        var gold = Int(row, GoldAt);
        // (the gold existed with the sender, but a full purse would wrap past int, review 2026-10-30)
        if ((long)to.GetCurrency() + gold > int.MaxValue) {
            return "emp_mail_bag_full";
        }

        var stacks = row.Length - ItemsAt;
        if (!PlayerTrade.HasRoom(to, stacks, gold)) {
            return "emp_mail_bag_full";
        }

        if (!Decode(row, out var things, out var error)) {
            // (kept as it is, marked for the admins: a later version may read it)
            row[FlagsAt] = (Int(row, FlagsAt) | FlagBroken).ToString();
            EmpLog.Warning(error, "Letter {Id} could not be read, kept and marked broken", id);
            return "emp_mail_broken";
        }

        // emptied before anything is given: a letter is taken once
        var before = (string[])row.Clone();
        var emptied = row.Take(ItemsAt).ToArray();
        emptied[GoldAt] = "0";
        emptied[FlagsAt] = ((Int(row, FlagsAt) | FlagTaken | FlagRead) & ~FlagBroken).ToString();
        ElinNetHost.MailLetters[id] = emptied;

        // one stack at a time: a step that fails (or a bag full meanwhile) stops there, and what was not given stays
        // in the letter as it was written
        var given = new bool[things.Count];
        var stop = "";
        for (var i = 0; i < things.Count && stop.Length == 0; i++) {
            var thing = things[i];
            if (!PlayerTrade.HasRoom(to, 1, 0)) {
                stop = "emp_mail_bag_full";
                break;
            }

            try {
                TestFault?.Invoke("give", i);
                // new uids: the old ones left the world with the sender's copy
                foreach (var card in thing.things.Flatten().Prepend(thing)) {
                    EClass.game.cards.AssignUID(card);
                }

                CardCache.Add(thing);
                CardCache.CacheContainer(thing.things);
                host.Delta.AddRemote(CardGenDelta.Create(thing));
                using (ElinDelta.Simulate()) {
                    // (not stacked: the receiver's game would stack it into a pile inside one of its bags, as for a trade)
                    to.AddThing(thing, false);
                }
            } catch (Exception ex) {
                stop = "emp_mail_partly";
                EmpLog.Warning(ex, "Letter {Id}: stack {Index} could not be given to {Uid}", id, i, to.uid);
            }

            given[i] = !thing.isDestroyed && thing.GetRootCard() == to;
        }

        var goldGiven = 0;
        if (stop.Length == 0 && gold > 0) {
            var had = to.GetCurrency();
            try {
                TestFault?.Invoke("givegold", 0);
                using (ElinDelta.Simulate()) {
                    to.ModCurrency(gold);
                }
            } catch (Exception ex) {
                stop = "emp_mail_partly";
                EmpLog.Warning(ex, "Letter {Id}: the gold could not be given to {Uid}", id, to.uid);
            }

            goldGiven = Mathf.Clamp(to.GetCurrency() - had, 0, gold);
        }

        // what was not given stays in the letter, as it was written (the copy made from it never reached a bag)
        var left = new List<string>();
        for (var i = 0; i < things.Count; i++) {
            if (!given[i]) {
                left.Add(before[ItemsAt + i]);
            }
        }

        var after = emptied;
        var partly = left.Count > 0 || goldGiven < gold;
        if (partly) {
            after = emptied.Concat(left).ToArray();
            after[GoldAt] = (gold - goldGiven).ToString();
            after[FlagsAt] = ((Int(before, FlagsAt) | FlagRead) & ~FlagTaken & ~FlagBroken).ToString();
            ElinNetHost.MailLetters[id] = after;
            EmpLog.Warning("Letter {Id} taken in part by {Uid}: {Given} of {Stacks} stacks and {GoldGiven} of {Gold} gold, the rest stays in it",
                id, to.uid, things.Count - left.Count, things.Count, goldGiven, gold);
        }

        MailJournal.Record(id, to, before, after);
        if (partly) {
            return stop.Length > 0 ? stop : "emp_mail_partly";
        }

        EmpLog.Information("Letter {Id} taken by {Uid}: {Stacks} stacks, {Gold} gold", id, to.uid, things.Count, gold);
        return null;
    }

    /// <summary>
    ///     The stacks of a letter made again from what it holds; false (and why) when one cannot be read
    /// </summary>
    private static bool Decode(string[] row, out List<Thing> things, out Exception? error)
    {
        things = [];
        error = null;
        try {
            for (var i = ItemsAt; i < row.Length; i++) {
                things.Add(new LZ4Bytes { Bytes = Convert.FromBase64String(row[i]) }.Decompress<Thing>());
            }

            return true;
        } catch (Exception ex) {
            error = ex;
            return false;
        }
    }

    /// <summary>
    ///     Admin "Try again": a letter marked broken is read once more; when every stack reads, it is a letter like the
    ///     others again (its recipient is told) and can be taken
    /// </summary>
    private static string? Retry(ElinNetHost host, int id)
    {
        if (!ElinNetHost.MailLetters.TryGetValue(id, out var row) || (Int(row, FlagsAt) & FlagBroken) == 0) {
            return "emp_mail_gone";
        }

        if (!Decode(row, out _, out var error)) {
            EmpLog.Warning(error, "Letter {Id} still cannot be read", id);
            return "emp_mail_still_broken";
        }

        row[FlagsAt] = (Int(row, FlagsAt) & ~FlagBroken).ToString();
        EmpLog.Information("Letter {Id} can be read again (admin)", id);
        TellOwner(host, Int(row, ToAt), true);
        return null;
    }

    /// <summary>
    ///     The letters marked broken, for the admins: who they are for and from, how many stacks (not the stacks)
    /// </summary>
    internal static List<MailLetter> BrokenLetters(ElinNetHost host)
    {
        return ElinNetHost.MailLetters.Where(p => (Int(p.Value, FlagsAt) & FlagBroken) != 0).OrderBy(p => p.Key).Select(p => {
            var letter = Letter(p.Key, p.Value);
            letter.Items = letter.Items.Select(_ => Array.Empty<byte>()).ToList();
            letter.Text = "";
            letter.ToName = NameOf(host, Int(p.Value, ToAt));
            return letter;
        }).ToList();
    }

    private static string? DeleteLetter(Chara owner, int id)
    {
        if (!ElinNetHost.MailLetters.TryGetValue(id, out var row) || Int(row, ToAt) != owner.uid) {
            return "emp_mail_gone";
        }

        if (!IsEmpty(row)) {
            return "emp_mail_not_empty";
        }

        ElinNetHost.MailLetters.Remove(id);
        return null;
    }

    /// <summary>
    ///     Every frame, connected: the host sends back what waited too long, and tells its own player of its letters
    ///     once a world is loaded
    /// </summary>
    internal static void Update()
    {
        MailJournal.Tick();
        if (!Keeps || !EClass.core.IsGameStarted || EClass.game is null || NetSession.Instance.Transport is not ElinNetHost host) {
            return;
        }

        if (!ReferenceEquals(_seenGame, EClass.game)) {
            _seenGame = EClass.game;
            _lastSent.Clear();
            _lastList.Clear();
            _nextReturnCheck = 0f;
            if (On && EClass.pc is { } pc) {
                Show(State(host, pc.uid, notice: true));
            }
        }

        if (!On || Time.unscaledTime < _nextReturnCheck) {
            return;
        }

        _nextReturnCheck = Time.unscaledTime + ReturnCheckSeconds;
        SendBackOld(host);
    }

    /// <summary>
    ///     Letters whose items or gold nobody took in 30 days go back to their sender, once, as returned letters
    /// </summary>
    internal static int SendBackOld(ElinNetHost host)
    {
        var now = Now();
        var told = new HashSet<int>();
        foreach (var (id, row) in ElinNetHost.MailLetters.ToArray()) {
            var flags = Int(row, FlagsAt);
            if ((flags & (FlagTaken | FlagReturned)) != 0 || IsEmpty(row) || now - Long(row, SentAt) < ReturnSeconds) {
                continue;
            }

            var (to, from) = (Int(row, ToAt), Int(row, FromAt));
            var back = (string[])row.Clone();
            back[ToAt] = from.ToString();
            back[FromAt] = to.ToString();
            // (the one it comes back from wrote nothing: never hidden by an ignore list)
            back[UserAt] = "0";
            back[NameAt] = NameOf(host, to);
            back[SentAt] = now.ToString();
            back[FlagsAt] = FlagReturned.ToString();
            ElinNetHost.MailLetters[id] = back;
            EmpLog.Information("Letter {Id} to {To} was not claimed in 30 days: back to {From}", id, to, from);
            told.Add(from);
        }

        foreach (var uid in told) {
            TellOwner(host, uid, true);
        }

        return told.Count;
    }

    /// <summary>
    ///     A player joins: it is told of its letters (its game says so if one waits)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ElinNetHost), "TellJoined")]
    private static void OnJoined(ElinNetHost __instance, List<(Chara Chara, ISteamNetPeer Peer, Action? After)> joins)
    {
        if (!Keeps || !On) {
            return;
        }

        foreach (var (chara, peer, _) in joins) {
            __instance.SendDeltaTo(peer.Id, State(__instance, chara.uid, notice: true));
        }
    }

    /// <summary>
    ///     For the bench: every letter of the book, one line each
    /// </summary>
    internal static string Describe()
    {
        return string.Join("\n", ElinNetHost.MailLetters.OrderBy(p => p.Key).Select(p =>
            $"{p.Key} to {Int(p.Value, ToAt)} from {Int(p.Value, FromAt)} flags {Int(p.Value, FlagsAt)} gold {Int(p.Value, GoldAt)} " +
            $"stacks {Math.Max(0, p.Value.Length - ItemsAt)} sent {Long(p.Value, SentAt)}"));
    }

    /// <summary>
    ///     For the bench: the entries of the mail journal not yet covered by a save wholly on the disk
    /// </summary>
    internal static int JournalCount() => MailJournal.Count;

    /// <summary>
    ///     For the bench: that letter as if it was sent so many days ago
    /// </summary>
    internal static void Age(int id, int days)
    {
        if (ElinNetHost.MailLetters.TryGetValue(id, out var row) && row.Length > SentAt) {
            row[SentAt] = (Now() - days * 86400L).ToString();
        }
    }

    // ---------------------------------------------------------------- the player

    private static MailStateDelta? _latest;
    private static Game? _latestGame;

    /// <summary>
    ///     Told when the host's answer lands (the open Mail page redraws)
    /// </summary>
    internal static Action? Changed { get; set; }

    /// <summary>
    ///     What the host last said, for this world
    /// </summary>
    internal static MailStateDelta? Latest => ReferenceEquals(_latestGame, EClass.game) ? _latest : null;

    /// <summary>
    ///     The letters this player sees: those of an ignored player are hidden (its personal list)
    /// </summary>
    internal static List<MailLetter> Letters()
    {
        return Latest?.Letters.Where(l => l.FromUser == 0 || !PlayerSocial.IsIgnored(l.FromUser)).ToList() ?? [];
    }

    /// <summary>
    ///     The letters not opened yet, for the badges (the Players tab, the HUD button)
    /// </summary>
    internal static int Unread => On ? Letters().Count(l => (l.Flags & (FlagRead | FlagTaken)) == 0) : 0;

    internal static bool Unclaimed(MailLetter letter)
    {
        return (letter.Flags & FlagTaken) == 0 && (letter.Items.Count > 0 || letter.Gold > 0 || (letter.Flags & FlagRead) == 0);
    }

    internal static void Show(MailStateDelta state)
    {
        _latest = state;
        _latestGame = EClass.game;
        if (state.Reason.Length > 0) {
            EmpPop.Information(state.Reason.lang());
        } else if (state.Done.Length > 0) {
            EmpPop.Information(state.Done.lang());
        }

        if (state.Notice && Letters().Count(Unclaimed) is var waiting and > 0) {
            EmpPop.Information("emp_mail_waiting".Loc(waiting));
        }

        Changed?.Invoke();
    }

    /// <summary>
    ///     A request of this game's own player: the host handles it at once, a guest asks the host
    /// </summary>
    internal static void Ask(MailRequestDelta request)
    {
        if (!On) {
            return;
        }

        switch (NetSession.Instance.Transport) {
            case ElinNetHost { IsZoneSession: false } host:
                if (EClass.pc is { } pc) {
                    Handle(host, pc, host.UserOf(0), true, false, request, Show);
                }

                break;
            case ElinNetClient client:
                // (also from a map of one's own: straight to the host, which answers away players too)
                if (NetSession.Instance.IsAway) {
                    client.SendWhileAway(request);
                } else {
                    client.Delta.AddRemote(request);
                }

                break;
        }
    }

    internal static void AskList() => Ask(new() { Kind = MailRequestDelta.List });

    internal static void Write(int to, string text, int gold, IEnumerable<int> items)
    {
        Ask(new() {
            Kind = MailRequestDelta.Send,
            To = to,
            Text = text.Length > MaxText ? text[..MaxText] : text,
            Gold = Mathf.Max(0, gold),
            Items = items.Take(MaxStacks).ToArray(),
        });
    }

    internal static void Take(int id) => Ask(new() { Kind = MailRequestDelta.Take, LetterId = id });

    internal static void Delete(int id) => Ask(new() { Kind = MailRequestDelta.Delete, LetterId = id });

    internal static void Retry(int id) => Ask(new() { Kind = MailRequestDelta.Retry, LetterId = id });

    internal static void MarkRead(MailLetter letter)
    {
        if ((letter.Flags & FlagRead) != 0) {
            return;
        }

        letter.Flags |= FlagRead;
        Ask(new() { Kind = MailRequestDelta.Read, LetterId = letter.Id });
    }

    // the stacks of a letter as this game reads them (name and icon only), kept per letter
    private static readonly Dictionary<(int Id, int Index), Thing?> _shown = [];

    internal static Thing? ItemOf(MailLetter letter, int index)
    {
        if (_shown.TryGetValue((letter.Id, index), out var thing)) {
            return thing;
        }

        try {
            thing = letter.Items[index] is { Length: > 0 } bytes ? new LZ4Bytes { Bytes = bytes }.Decompress<Thing>() : null;
        } catch (Exception ex) {
            EmpLog.Information(ex, "A stack of letter {Id} could not be read", letter.Id);
            thing = null;
        }

        if (_shown.Count > 200) {
            _shown.Clear();
        }

        return _shown[(letter.Id, index)] = thing;
    }
}
