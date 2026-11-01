using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     What a rank of a team may do (lead dev 2026-11-01: "ranks with different permissions per role"). The leader has
///     every one; disbanding and handing the team over stay the leader's alone
/// </summary>
[System.Flags]
public enum TeamPerm
{
    None = 0,

    /// <summary>Invite players, answer join requests, open or close joining</summary>
    Recruit = 1,

    /// <summary>Put out a member of a lower rank</summary>
    Kick = 1 << 1,

    /// <summary>Edit the ranks below one's own and give them to the members below one's own</summary>
    Ranks = 1 << 2,

    Rename = 1 << 3,

    /// <summary>The team's emblem and banner</summary>
    Emblem = 1 << 4,

    /// <summary>The base's board and core (policies, residents, home, spawn), moving a base with its deed</summary>
    Base = 1 << 5,

    /// <summary>Claim a land with a deed for the team, abandon a land</summary>
    Claim = 1 << 6,

    LandRules = 1 << 7,

    /// <summary>Build, dig and place on the team's lands (without it: at most open chests, or only visit)</summary>
    Build = 1 << 8,

    /// <summary>Open the chests of the team's lands and take from them</summary>
    Chests = 1 << 9,

    /// <summary>Put things in the team bank (and the team's delivery box)</summary>
    Deposit = 1 << 10,

    /// <summary>Take from the team bank (and the team's delivery box), up to the rank's daily limit</summary>
    Withdraw = 1 << 11,

    /// <summary>Share one's quests with the team</summary>
    Quests = 1 << 12,

    /// <summary>Speak in the team chat</summary>
    Chat = 1 << 13,

    All = (1 << 14) - 1,
}

/// <summary>
///     Team ranks (lead dev 2026-11-01). Each team has a list of ranks, the leader's first (all permissions, fixed),
///     and each member one rank of it; a new member gets the team's default rank. Kept by the host with the team
///     (<see cref="ElinNetHost.TeamRankBook" />, one text per team) and told with the teams (<see cref="TeamsDelta.Ranks" />);
///     every action is checked on the host against the sender's rank, a guest's page only greys what it cannot do. <br />
///     A team with no book has the default one (Leader, Officer, Member, Recruit; everyone is a Member) whose Member
///     rights are the rights every member had before ranks, so an old world plays as it did until someone edits its
///     ranks. While the host box "Team leaders manage their team" is off, ranks do not apply at all: the leader has its
///     old rights and every member the old Member ones, as before
/// </summary>
[HarmonyPatch]
internal static class TeamRanks
{
    internal sealed class Rank
    {
        internal int Id;
        internal string Name = "";
        internal int Perms;
        internal int Limit;
    }

    internal sealed class Book
    {
        internal readonly List<Rank> Ranks = [];
        internal int Default = MemberId;
        internal readonly Dictionary<int, int> Members = [];

        internal Rank? Find(int id) => Ranks.Find(r => r.Id == id);

        internal int IndexOf(int id) => Ranks.FindIndex(r => r.Id == id);
    }

    internal const int LeaderId = 0;
    private const int OfficerId = 1;
    private const int MemberId = 2;
    private const int RecruitId = 3;

    /// <summary>
    ///     What every member could do before ranks: build and open chests on the team's lands, claim and abandon lands,
    ///     the base's menus, the team bank both ways, share quests, the team chat
    /// </summary>
    internal const TeamPerm MemberDefault = TeamPerm.Base | TeamPerm.Claim | TeamPerm.Build | TeamPerm.Chests | TeamPerm.Deposit |
                                            TeamPerm.Withdraw | TeamPerm.Quests | TeamPerm.Chat;

    private const TeamPerm OfficerDefault = MemberDefault | TeamPerm.Recruit | TeamPerm.Kick;

    private const TeamPerm RecruitDefault = TeamPerm.Chests | TeamPerm.Deposit | TeamPerm.Quests | TeamPerm.Chat;

    /// <summary>The most ranks a team may have, the leader's included (the four first ones plus eight)</summary>
    internal const int MaxRanks = 12;

    internal const int NameMax = 20;

    /// <summary>The daily withdraw limits a rank may have, in pieces (gold counts by coin); 0 is no limit</summary>
    internal static readonly int[] Limits = [0, 100, 500, 1000, 5000, 10000, 50000];

    /// <summary>The permissions in the order the Ranks page lists them</summary>
    internal static readonly TeamPerm[] Listed = [
        TeamPerm.Recruit, TeamPerm.Kick, TeamPerm.Ranks, TeamPerm.Rename, TeamPerm.Emblem, TeamPerm.Base, TeamPerm.Claim,
        TeamPerm.LandRules, TeamPerm.Build, TeamPerm.Chests, TeamPerm.Deposit, TeamPerm.Withdraw, TeamPerm.Quests, TeamPerm.Chat,
    ];

    // the game that keeps the book: the host of the world, or a game playing alone
    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    // a guest's copy of the host's books
    private static Dictionary<int, string> _told = [];

    private static Dictionary<int, string> Source => KeepsBook ? ElinNetHost.TeamRankBook : _told;

    // the books read from their text, kept while the text is the same
    private static readonly Dictionary<int, (string Text, Book Book)> Parsed = [];

    // the host's memory of this session: what each player took from its team's boxes today (not saved)
    private static readonly Dictionary<int, (int Day, int Taken)> TakenToday = [];

    [ElinPreLoad]
    private static void Forget(GameIOContext context)
    {
        _told = [];
        Parsed.Clear();
        TakenToday.Clear();
    }

    /// <summary>
    ///     Ranks are the team's own while the host box "Team leaders manage their team" is on
    /// </summary>
    internal static bool On => Teams.LeadersManage;

    // ---- the book

    private static readonly Book SharedDefault = DefaultBook();

    internal static Book DefaultBook()
    {
        var book = new Book();
        book.Ranks.Add(new() { Id = LeaderId, Perms = (int)TeamPerm.All });
        book.Ranks.Add(new() { Id = OfficerId, Perms = (int)OfficerDefault });
        book.Ranks.Add(new() { Id = MemberId, Perms = (int)MemberDefault });
        book.Ranks.Add(new() { Id = RecruitId, Perms = (int)RecruitDefault });
        return book;
    }

    /// <summary>
    ///     A book as one text: "d id", then "r id perms limit name" for each rank in order, then "m chara rank"
    /// </summary>
    internal static string Pack(Book book)
    {
        var text = new StringBuilder();
        text.Append("d ").Append(book.Default).Append('\n');
        foreach (var rank in book.Ranks) {
            text.Append("r ").Append(rank.Id).Append(' ').Append(rank.Perms).Append(' ').Append(rank.Limit).Append(' ')
                .Append(Clean(rank.Name)).Append('\n');
        }

        foreach (var (uid, id) in book.Members.OrderBy(m => m.Key)) {
            text.Append("m ").Append(uid).Append(' ').Append(id).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    ///     A book from its text. Whatever does not read is left out; a book without the leader's rank, or with no
    ///     other, is the default one (a broken text never locks a team)
    /// </summary>
    internal static Book Unpack(string? text)
    {
        var book = new Book();
        foreach (var line in (text ?? "").Split('\n')) {
            var parts = line.Split([' '], 5);
            if (parts.Length < 2) {
                continue;
            }

            switch (parts[0]) {
                case "d" when int.TryParse(parts[1], out var d):
                    book.Default = d;
                    break;
                case "r" when parts.Length >= 4 && int.TryParse(parts[1], out var id) && int.TryParse(parts[2], out var perms) &&
                              int.TryParse(parts[3], out var limit) && book.Find(id) is null && book.Ranks.Count < MaxRanks:
                    book.Ranks.Add(new() { Id = id, Perms = perms & (int)TeamPerm.All, Limit = System.Math.Max(0, limit), Name = parts.Length > 4 ? Clean(parts[4]) : "" });
                    break;
                case "m" when parts.Length >= 3 && int.TryParse(parts[1], out var uid) && int.TryParse(parts[2], out var rank):
                    book.Members[uid] = rank;
                    break;
            }
        }

        if (book.Ranks.Count < 2 || book.Ranks[0].Id != LeaderId) {
            return DefaultBook();
        }

        // (the leader's rank always has everything)
        book.Ranks[0].Perms = (int)TeamPerm.All;
        book.Ranks[0].Limit = 0;
        if (book.Find(book.Default) is null || book.Default == LeaderId) {
            book.Default = book.Ranks[book.Ranks.Count - 1].Id;
        }

        return book;
    }

    internal static string Clean(string? text)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > NameMax ? clean[..NameMax].Trim() : clean;
    }

    /// <summary>
    ///     That team's book as it is now (the default one for a team that never edited its ranks)
    /// </summary>
    internal static Book BookOf(int teamId)
    {
        // (one shared default book, never changed: a change is made on a copy, see Answer)
        if (!Source.TryGetValue(teamId, out var text) || string.IsNullOrEmpty(text)) {
            return SharedDefault;
        }

        if (Parsed.TryGetValue(teamId, out var cached) && ReferenceEquals(cached.Text, text)) {
            return cached.Book;
        }

        var book = Unpack(text);
        Parsed[teamId] = (text, book);
        return book;
    }

    /// <summary>
    ///     The team edited its ranks (the default book is not written until then)
    /// </summary>
    internal static bool Edited(int teamId) => Source.ContainsKey(teamId);

    internal static string NameOf(Rank rank)
    {
        return rank.Name.Length > 0 ? rank.Name : rank.Id is >= LeaderId and <= RecruitId ? $"emp_rank_name_{rank.Id}".lang() : "emp_rank_new".lang();
    }

    /// <summary>
    ///     That member's rank: the leader's for the leader, the one it was given while it is still in the team, the
    ///     team's default one otherwise
    /// </summary>
    internal static Rank RankOf(int charaUid, int teamId)
    {
        var book = BookOf(teamId);
        if (Teams.LeaderOf(teamId) == charaUid) {
            return book.Ranks[0];
        }

        return book.Members.TryGetValue(charaUid, out var id) && id != LeaderId && book.Find(id) is { } given
            ? given
            : book.Find(book.Default) ?? book.Ranks[book.Ranks.Count - 1];
    }

    /// <summary>
    ///     The place of that member's rank in its team's list: 0 for the leader, higher for lower ranks
    /// </summary>
    internal static int PlaceOf(int charaUid, int teamId)
    {
        return Teams.LeaderOf(teamId) == charaUid ? 0 : System.Math.Max(1, BookOf(teamId).IndexOf(RankOf(charaUid, teamId).Id));
    }

    // ---- the checks (the host's, and the pages')

    /// <summary>
    ///     That player of that team (its own by default) may do this. The leader may do everything; with the host box
    ///     "Team leaders manage their team" off, a member has the rights every member had before ranks
    /// </summary>
    internal static bool Can(int charaUid, TeamPerm perm, int teamId = -1)
    {
        if (teamId < 0) {
            teamId = Teams.TeamOf(charaUid);
        }

        // (the leader first: it is a member, and has everything)
        if (teamId <= Teams.Wanderers) {
            return false;
        }

        if (charaUid != 0 && Teams.LeaderOf(teamId) == charaUid) {
            return true;
        }

        if (!Teams.MembersOfTeam(teamId).Contains(charaUid)) {
            return false;
        }

        var perms = On ? (TeamPerm)RankOf(charaUid, teamId).Perms : MemberDefault;
        return (perms & perm) == perm;
    }

    /// <summary>
    ///     The team in which that player may do this while the leaders manage their team (as <see cref="Teams.LedBy" />
    ///     for the leader), none otherwise
    /// </summary>
    internal static int Manages(int charaUid, TeamPerm perm)
    {
        return On && Teams.TeamOf(charaUid) is var team and > Teams.Wanderers && Can(charaUid, perm, team) ? team : Teams.Wanderers;
    }

    /// <summary>
    ///     That member's rank is above the other's (the leader is above everyone, nobody above the leader)
    /// </summary>
    internal static bool Outranks(int charaUid, int otherUid, int teamId)
    {
        return charaUid != otherUid && Teams.LeaderOf(teamId) != otherUid && PlaceOf(charaUid, teamId) < PlaceOf(otherUid, teamId);
    }

    /// <summary>
    ///     How much a player may do on its own team's land (<see cref="Teams.LevelOn" />): build, open chests, or visit
    /// </summary>
    internal static int LandCap(int charaUid, int owner)
    {
        if (owner <= 0) {
            return Teams.Build;
        }

        return Can(charaUid, TeamPerm.Build, owner) ? Teams.Build : Can(charaUid, TeamPerm.Chests, owner) ? Teams.Use : Teams.Visit;
    }

    /// <summary>
    ///     That player may manage the base on this land: a land of no team, or its team's rank allows it
    /// </summary>
    internal static bool MayManageBase(Chara chara, Zone? zone)
    {
        return !chara.IsPlayer || Teams.LandOwnerOf(zone) is var owner && (owner <= 0 || Can(chara.uid, TeamPerm.Base, owner));
    }

    /// <summary>
    ///     That player may claim a land with a deed, or abandon one: a player in no team claims its own
    /// </summary>
    internal static bool MayClaim(Chara chara)
    {
        return !chara.IsPlayer || !NetSession.Instance.Rules.UseTeams || Teams.TeamOf(chara.uid) is var team && (team == Teams.Wanderers ||
            Can(chara.uid, TeamPerm.Claim, team));
    }

    private static int Today() => (int)(System.DateTime.UtcNow - new System.DateTime(2020, 1, 1)).TotalDays;

    /// <summary>
    ///     How many pieces that player may still take from its team's boxes today (int.MaxValue: no limit)
    /// </summary>
    internal static int WithdrawLeft(int charaUid, int teamId)
    {
        if (!Can(charaUid, TeamPerm.Withdraw, teamId)) {
            return 0;
        }

        var limit = On ? RankOf(charaUid, teamId).Limit : 0;
        if (limit <= 0) {
            return int.MaxValue;
        }

        var taken = TakenToday.TryGetValue(charaUid, out var t) && t.Day == Today() ? t.Taken : 0;
        return System.Math.Max(0, limit - taken);
    }

    /// <summary>
    ///     On the host: that player may take from this box of an owner (<see cref="TeamBoxes" />). A personal box, the
    ///     world's: yes; a team's: its rank's Withdraw, while its daily limit is not reached
    /// </summary>
    internal static bool MayWithdraw(int charaUid, int owner)
    {
        return owner <= 0 || WithdrawLeft(charaUid, owner) > 0;
    }

    internal static bool MayDeposit(int charaUid, int owner)
    {
        return owner <= 0 || Can(charaUid, TeamPerm.Deposit, owner);
    }

    /// <summary>
    ///     On the host, a take from a team's box: no more than the rank's limit leaves today, and what is taken is
    ///     counted
    /// </summary>
    internal static int ClampWithdraw(Chara taker, Thing thing, int num)
    {
        if (!KeepsBook || TeamBoxes.OwnerOf(thing.GetRootCard(), out _) is not (> 0 and var owner)) {
            return num;
        }

        var left = WithdrawLeft(taker.uid, owner);
        if (left == int.MaxValue) {
            return num;
        }

        num = System.Math.Min(num, left);
        if (num > 0) {
            NoteTaken(taker.uid, num);
        } else {
            Denied(taker.uid);
        }

        return num;
    }

    private static void NoteTaken(int charaUid, int num)
    {
        var day = Today();
        var taken = TakenToday.TryGetValue(charaUid, out var t) && t.Day == day ? t.Taken : 0;
        TakenToday[charaUid] = (day, taken + num);
    }

    /// <summary>
    ///     The local player may not pick this thing out of its team's bank (the host's own player against the book, a
    ///     guest against its copy: the host checks again)
    /// </summary>
    internal static bool RefusesTake(Chara pc, Thing thing)
    {
        if (!pc.IsPC || !TeamBoxes.On) {
            return false;
        }

        var root = thing.GetRootCard();
        if (KeepsBook) {
            // (the host's own player: the whole stack it picks counts against the rank's daily limit, as a guest's take)
            if (TeamBoxes.OwnerOf(root, out _) is not (> 0 and var owner)) {
                return false;
            }

            // (more than is left today: the part that is left is split off and taken, as a guest's take is cut)
            var left = WithdrawLeft(pc.uid, owner);
            if (left <= 0) {
                return true;
            }

            if (left == int.MaxValue) {
                return false;
            }

            if (thing.Num > left) {
                var part = thing.Split(left);
                NoteTaken(pc.uid, left);
                pc.Pick(part);
                return true;
            }

            NoteTaken(pc.uid, thing.Num);
            return false;
        }

        return root is not null && ReferenceEquals(root, TeamBoxes.TeamBankBox()) && WithdrawLeft(pc.uid, Teams.TeamOf(pc.uid)) <= 0;
    }

    /// <summary>
    ///     "Your rank does not allow this.", in the log of that player
    /// </summary>
    internal static void Denied(int charaUid)
    {
        Teams.Say(charaUid, "emp_rank_denied");
    }

    // ---- what the host is asked

    /// <summary>
    ///     The local player's change to its team's ranks: answered here in the game that keeps the book, asked of the
    ///     host otherwise
    /// </summary>
    internal static void Ask(RankOp op, int rank = 0, int value = 0, string text = "")
    {
        if (KeepsBook) {
            Answer(EClass.pc, op, rank, value, text);
        } else {
            PersonalQuests.TellHost(new TeamRankRequestDelta { Op = op, Rank = rank, Value = value, Text = text });
        }
    }

    /// <summary>
    ///     That player may edit this rank of its team: it manages ranks and the rank is below its own (the leader's is
    ///     nobody's to edit)
    /// </summary>
    internal static bool MayEdit(int charaUid, int teamId, int rankId)
    {
        var book = BookOf(teamId);
        var index = book.IndexOf(rankId);
        return On && index > 0 && Can(charaUid, TeamPerm.Ranks, teamId) && index > PlaceOf(charaUid, teamId);
    }

    /// <summary>
    ///     That player may give that rank to that member: both below its own rank, never the leader's (handing the
    ///     team over is the leader's Promote)
    /// </summary>
    internal static bool MayAssign(int charaUid, int teamId, int memberUid, int rankId)
    {
        return MayEdit(charaUid, teamId, rankId) && memberUid != charaUid && Teams.MembersOfTeam(teamId).Contains(memberUid) &&
               Outranks(charaUid, memberUid, teamId);
    }

    /// <summary>
    ///     In the game that keeps the book: what that player asks, read against the book as it is now
    /// </summary>
    internal static void Answer(Chara chara, RankOp op, int rankId, int value, string text)
    {
        var team = Teams.TeamOf(chara.uid);
        if (!KeepsBook || !On || team == Teams.Wanderers || !Can(chara.uid, TeamPerm.Ranks, team)) {
            if (KeepsBook && team != Teams.Wanderers) {
                Denied(chara.uid);
            }

            return;
        }

        var book = Unpack(Pack(BookOf(team)));
        var leads = Teams.LeaderOf(team) == chara.uid;
        var own = leads ? TeamPerm.All : (TeamPerm)RankOf(chara.uid, team).Perms;
        var place = PlaceOf(chara.uid, team);
        var rank = book.Find(rankId);
        var index = book.IndexOf(rankId);
        var done = false;
        switch (op) {
            // (only the permissions it has itself: nobody gives more than it holds)
            case RankOp.SetPerms when rank is not null && MayEdit(chara.uid, team, rankId):
                var keep = rank.Perms & ~(int)own;
                rank.Perms = keep | value & (int)own & (int)TeamPerm.All;
                done = true;
                break;
            // (only a rank that withdraws itself, and never a looser limit than its own)
            case RankOp.SetLimit when rank is not null && MayEdit(chara.uid, team, rankId) && Limits.Contains(value) &&
                                      (own & TeamPerm.Withdraw) != 0 &&
                                      (leads || RankOf(chara.uid, team).Limit is var mine && (mine == 0 || value > 0 && value <= mine)):
                rank.Limit = value;
                done = true;
                break;
            case RankOp.Rename when rank is not null && MayEdit(chara.uid, team, rankId):
                rank.Name = Clean(text);
                done = true;
                break;
            case RankOp.Add when book.Ranks.Count < MaxRanks:
                var id = book.Ranks.Max(r => r.Id) + 1;
                // (a new rank speaks in the team chat and nothing more until it is given more)
                book.Ranks.Add(new() { Id = id, Name = Clean(text), Perms = (int)(TeamPerm.Chat & own) });
                done = true;
                break;
            case RankOp.Remove when rank is not null && MayEdit(chara.uid, team, rankId) && rankId != book.Default && book.Ranks.Count > 2:
                book.Ranks.Remove(rank);
                foreach (var uid in book.Members.Where(m => m.Value == rankId).Select(m => m.Key).ToArray()) {
                    book.Members.Remove(uid);
                }

                done = true;
                break;
            // (value -1 up, 1 down; never above its own rank, never in the leader's place)
            case RankOp.Move when rank is not null && MayEdit(chara.uid, team, rankId) && value is -1 or 1 &&
                                  index + value > place && index + value < book.Ranks.Count:
                book.Ranks.RemoveAt(index);
                book.Ranks.Insert(index + value, rank);
                done = true;
                break;
            case RankOp.SetDefault when rank is not null && MayEdit(chara.uid, team, rankId):
                book.Default = rankId;
                done = true;
                break;
            case RankOp.Assign when rank is not null && MayAssign(chara.uid, team, value, rankId):
                book.Members[value] = rankId;
                Teams.Say(value, "emp_rank_given", NameOf(rank));
                done = true;
                break;
        }

        if (!done) {
            Denied(chara.uid);
            return;
        }

        Write(team, book);
        EmpLog.Information("Team {TeamId} ranks: {Op} rank {RankId} ({Value}) by {CharaUid}", team, op, rankId, value, chara.uid);
    }

    private static void Write(int teamId, Book book)
    {
        // (the members still in the team only: a player who left and comes back starts at the default rank)
        var members = Teams.MembersOfTeam(teamId).ToHashSet();
        foreach (var uid in book.Members.Keys.Where(uid => !members.Contains(uid)).ToArray()) {
            book.Members.Remove(uid);
        }

        ElinNetHost.TeamRankBook[teamId] = Pack(book);
        Teams.Tell();
    }

    /// <summary>
    ///     The leader handed the team over: the former leader takes the rank the new one had, so the new one's place
    ///     is not left above it
    /// </summary>
    internal static void Promoted(int teamId, int formerLeader, int newLeader)
    {
        if (!KeepsBook || !Edited(teamId)) {
            return;
        }

        var book = Unpack(Pack(BookOf(teamId)));
        var given = book.Members.TryGetValue(newLeader, out var id) ? id : book.Default;
        book.Members.Remove(newLeader);
        book.Members[formerLeader] = given;
        ElinNetHost.TeamRankBook[teamId] = Pack(book);
    }

    /// <summary>
    ///     What the host tells with the teams: the books of the teams that edited their ranks, each without the members
    ///     who left (a disbanded team's book goes)
    /// </summary>
    internal static Dictionary<int, string>? Told()
    {
        if (!KeepsBook) {
            return null;
        }

        var book = ElinNetHost.TeamRankBook;
        foreach (var id in book.Keys.ToArray()) {
            if (!ElinNetHost.TeamList.ContainsKey(id)) {
                book.Remove(id);
                continue;
            }

            // (a member who left, was put out or took another seat: back later, it starts at the default rank)
            var members = Teams.MembersOfTeam(id).ToHashSet();
            if (BookOf(id).Members.Keys.Any(uid => !members.Contains(uid))) {
                var copy = Unpack(Pack(BookOf(id)));
                foreach (var uid in copy.Members.Keys.Where(uid => !members.Contains(uid)).ToArray()) {
                    copy.Members.Remove(uid);
                }

                book[id] = Pack(copy);
            }
        }

        return book.Count == 0 ? null : new(book);
    }

    /// <summary>
    ///     A guest learns the host's books (none from an older host: every team has the default book)
    /// </summary>
    internal static void Learn(Dictionary<int, string>? told)
    {
        _told = told is null ? [] : new(told);
    }

    // ---- the game's own ways in, for the local player (the host checks a guest's again)

    /// <summary>
    ///     A deed is not read by a member whose rank may not claim lands
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitDeed), nameof(TraitDeed.OnRead))]
    private static bool OnReadDeed(Chara c)
    {
        if (!c.IsPC || !NetSession.Instance.InGame || MayClaim(c)) {
            return true;
        }

        SE.Beep();
        Msg.Say("emp_rank_denied".lang());
        return false;
    }

    /// <summary>
    ///     Describe for the bench and the console: each team's ranks and who holds which
    /// </summary>
    internal static string Describe()
    {
        var lines = new List<string>();
        foreach (var team in Teams.Ids().Where(id => id != Teams.Wanderers)) {
            var book = BookOf(team);
            lines.Add($"team {team} edited {Edited(team)} default {book.Default} ranks " +
                      string.Join(" | ", book.Ranks.Select(r => $"{r.Id}:{NameOf(r)}:{r.Perms}:{r.Limit}")));
            foreach (var uid in Teams.MembersOfTeam(team)) {
                lines.Add($"  member {uid} rank {RankOf(uid, team).Id} perms {(On ? RankOf(uid, team).Perms : (int)MemberDefault)}");
            }
        }

        return string.Join("\n", lines.DefaultIfEmpty("no teams"));
    }
}
