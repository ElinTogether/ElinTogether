using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Council 2026-10-25, host option "Teams pay their own land" (dev/PLAN_teams_knowledge.md). The game makes one
///     tax bill a month for every land of the player faction, on the host's fame, and counts the unpaid ones for the
///     world. With the box on, each team that holds land gets its own bill, for its own lands (lands of no team: one
///     bill per player who claimed them, its own): the base tax and the fame tax of its members' highest fame, lowered by
///     its own lands' tax evasion. Each owner's unpaid bills are counted apart (<see cref="ElinNetHost.TeamTaxBills" />,
///     keyed as <see cref="StoryOwner" /> keys owners: a player in no team under minus its chara uid, never with the
///     other players in no team, user 2026-10-30); four or more cost karma to that owner's players only. A bill knows
///     its team by its <c>refVal</c> (team + 1, and its claimer for one of no team), which also keeps two teams' bills
///     from stacking. The world's count (<c>player.taxBills</c>) stays the sum, so a vanilla save, or the
///     box off, finds every bill as the game counts them. Bills older than the box belong to no team: they weigh on
///     every team, as the world's did
/// </summary>
[HarmonyPatch]
internal static class TeamBills
{
    // the game that keeps the book: the host of the world, or a game playing alone
    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    /// <summary>
    ///     The box as this game knows it: the host's own setting, a guest's from the host's rules
    /// </summary>
    internal static bool On => KeepsBook
        ? EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamBills.Value
        : NetSession.Instance.Rules is { UseTeams: true, TeamBills: true };

    /// <summary>
    ///     The team a tax bill was made for, null for one of no team (older than the box)
    /// </summary>
    internal static int? TeamOf(Thing bill)
    {
        return bill.id == "bill_tax" && bill.refVal > 0 ? bill.refVal - 1 : null;
    }

    /// <summary>
    ///     The owner a tax bill is counted for: its team, minus the claimer for a bill of the lands of a player in no
    ///     team (0 for an older bill of no team), null for one older than the box
    /// </summary>
    internal static int? OwnerOf(Thing bill)
    {
        return TeamOf(bill) is 0 && bill.GetInt(BaseIncome.ClaimerKey) is > 0 and var claimer ? -claimer : TeamOf(bill);
    }

    // the key the guests are told the world's bills of no team under (was -1, a player's key now)
    private const int UnassignedKey = int.MinValue;

    private static Dictionary<int, int> Owed => ElinNetHost.TeamTaxBills;

    /// <summary>
    ///     The world's unpaid bills that are no team's
    /// </summary>
    private static int Unassigned => Mathf.Max(0, EClass.player.taxBills - Owed.Values.Sum());

    /// <summary>
    ///     The unpaid bills that weigh on that owner (a team, or minus a player in no team): its own and the world's of
    ///     no team (for a player in no team, also the ones counted for all of them before 2026-10-30)
    /// </summary>
    internal static int UnpaidOf(int team)
    {
        var told = KeepsBook ? Owed : Teams.ToldBills;
        var shared = team < 0 ? told.GetValueOrDefault(Teams.Wanderers) : 0;
        return told.GetValueOrDefault(team) + shared + (KeepsBook ? Unassigned : told.GetValueOrDefault(UnassignedKey));
    }

    /// <summary>
    ///     What every game is told with the teams: none while the box is off
    /// </summary>
    internal static Dictionary<int, int>? Told()
    {
        return On ? new Dictionary<int, int>(Owed) { [UnassignedKey] = Unassigned } : null;
    }

    /// <summary>
    ///     A tax bill was paid in the game that keeps the book (the world's count already went down): its owner's too. A
    ///     bill of no team told without its claimer is the payer's own; one counted for all the players in no team before
    ///     2026-10-30 lowers that old count
    /// </summary>
    internal static void OnPaid(int? team, int payer = 0)
    {
        var id = team is 0 && payer > 0 ? -payer : team ?? 0;
        if (id < 0 && Owed.GetValueOrDefault(id) <= 0) {
            id = Teams.Wanderers;
        }

        if (!KeepsBook || team is null || Owed.GetValueOrDefault(id) <= 0) {
            return;
        }

        if (--Owed[id] == 0) {
            Owed.Remove(id);
        }

        EmpLog.Information("Team {TeamId} paid a tax bill, {Count} left unpaid", id, Owed.GetValueOrDefault(id));
        Fit();
        Tell();
    }

    /// <summary>
    ///     The teams' counts never hold more than the world's (lowered by the repair of phantom bills, by a cheat):
    ///     the largest is lowered first
    /// </summary>
    internal static void Fit()
    {
        while (Owed.Count > 0 && Owed.Values.Sum() > EClass.player.taxBills) {
            var most = Owed.OrderByDescending(o => o.Value).First().Key;
            if (--Owed[most] <= 0) {
                Owed.Remove(most);
            }
        }
    }

    private static void Tell()
    {
        (NetSession.Instance.Transport as ElinNetHost)?.TellTeams();
    }

    // a bill paid by the game's own PayBill (by hand at the tax chest, or by the bank): destroyed when paid
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InvOwnerDeliver), nameof(InvOwnerDeliver.PayBill))]
    private static void OnPayBill(Thing t, out bool __state)
    {
        __state = t is { id: "bill_tax", isDestroyed: false };
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InvOwnerDeliver), nameof(InvOwnerDeliver.PayBill))]
    private static void OnPaidBill(Thing t, bool __state)
    {
        // (box off too: a bill of a team paid meanwhile is no longer that team's when it is ticked again)
        if (__state && t.isDestroyed) {
            OnPaid(OwnerOf(t));
        }
    }

    /// <summary>
    ///     The month in the game that keeps the book (only the world's keeper runs it, see WorldKeeperHooks): the
    ///     game's own salary, then a bill per team instead of the world's one. Box off: the game's own
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(typeof(Faction), nameof(Faction.OnAdvanceMonth))]
    private static bool OnMonth(Faction __instance, bool __runOriginal)
    {
        if (!__runOriginal || !KeepsBook || __instance != EClass.pc.faction) {
            return __runOriginal;
        }

        var branches = __instance.GetChildren();
        // the commons pays no tax (host box "The start land belongs to everyone"): box off, the game's own bill
        // unless the commons is the only land
        var taxed = branches.Where(b => !Commons.Is(b.owner)).ToList();
        if (!On) {
            if (branches.Count == 0 || taxed.Count > 0) {
                return true;
            }

            VanillaSalary(__instance);
            return false;
        }

        if (branches.Count == 0) {
            return false;
        }

        PaySalary(__instance, branches);
        branches = taxed;

        // the extra tax a player chose to give is the world's: on the first bill only (each story its own: on its
        // team's bill, see TeamOwnStory). One bill per team; the lands of no team, one per player who claimed them
        var extra = EClass.player.extraTax;
        foreach (var lands in branches.GroupBy(b => Payer(b.owner)).OrderBy(g => g.Key.Team).ThenBy(g => g.Key.Claimer).ToArray()) {
            var (team, claimer) = lands.Key;
            // (a player in no team is billed alone, counted under minus its uid)
            var owner = team == Teams.Wanderers && claimer > 0 ? -claimer : team;
            extra = TeamOwnStory.ExtraTaxOf(team) ?? extra;
            var evasion = lands.Sum(b => b.Evalue(2119));
            var fame = team == Teams.Wanderers && claimer > 0 && PersonalQuests.Enabled ? ElinNetHost.FameOfPlayer(claimer) : TeamFame(team);
            var tax = Evade(__instance.GetBaseTax(false), evasion) + Evade(FameTax(fame), evasion) + extra;
            var bill = ThingGen.CreateBill(tax, true);
            bill.SetInt(35, extra);
            bill.refVal = team + 1;
            if (owner < 0) {
                bill.SetInt(BaseIncome.ClaimerKey, claimer);
            }

            extra = 0;
            Owed[owner] = Owed.GetValueOrDefault(owner) + 1;
            EmpLog.Information("Owner {Owner}: a tax bill of {Tax} for {Lands} lands", owner, tax, lands.Count());
            Msg.Say("emp_team_bill".Loc(owner < 0 ? Teams.PlayerName(claimer) : Teams.NameOf(team), Lang._currency(tax, "money")));
            // (paid by the bank at once when the policy says so: OnPaidBill lowers the team's count)
            Deliver(__instance, bill, team, claimer);
        }

        Msg.Say("bills", EClass.player.taxBills.ToString());
        if (EClass.game.principal.tax && !EClass.debug.godMode) {
            Punish();
        }

        Tell();
        return false;
    }

    /// <summary>
    ///     Who pays for a land: its team, or for a land of no team the player who claimed it (0: not known, and the
    ///     commons, which is nobody's)
    /// </summary>
    private static (int Team, int Claimer) Payer(Zone zone)
    {
        var team = Teams.TeamOfZone(zone);
        return team != Teams.Wanderers || Commons.Is(zone) ? (team, 0) : (team, BaseIncome.ClaimerOf(zone));
    }

    // the player a team's mail is in the name of (the Wanderers: the claimer)
    private static int Recipient(int team, int claimer)
    {
        return team != Teams.Wanderers ? Teams.MembersOf(team).FirstOrDefault() : claimer;
    }

    /// <summary>
    ///     User 2026-10-30 ("lots of tax bills and salaries spawned"): a bill went through the world's parcels
    ///     (World.SendPackage) with no name on it, into the mail of whatever base took the world's parcels first, which
    ///     on a dedicated server is always the commons (the start meadow, everyone's). A team's bill now goes straight
    ///     to that owner's own delivery box (the claimer's, for a land of no team), in its name, paid by that owner's
    ///     own bank when the policy says so. Box off, or nobody to bill: the game's own way
    /// </summary>
    private static void Deliver(Faction faction, Thing bill, int team, int claimer)
    {
        var recipient = Recipient(team, claimer);
        if (!TeamBoxes.On || recipient <= 0) {
            TeamBoxes.AsTeam(team, () => faction.TryPayBill(bill));
            return;
        }

        var owner = StoryOwner.Of(team, recipient);
        bill.SetInt(TeamMail.ParcelKey, recipient);
        TeamBoxes.As(owner, () => {
            if (faction.IsGlobalPolicyActive(2705) && EClass.game.cards.container_deposit.GetCurrency() > bill.c_bill) {
                InvOwnerDeliver.PayBill(bill, fromBank: true);
            } else {
                TeamBoxes.Box(owner, 1).AddCard(bill);
            }
        });
    }

    /// <summary>
    ///     Money of the world that is nobody's (a land of no owner known, the commons, what a split leaves): the world's
    ///     bank on a dedicated server (its parcel lay in the commons' mail for anyone to take), else the game's own
    ///     salary parcel
    /// </summary>
    private static void WorldMoney(int num)
    {
        if (ServerAvatar.Is(EClass.pc) && EClass.game.cards.container_deposit is { } bank) {
            bank.ModCurrency(num);
            return;
        }

        EClass.world.SendPackage(ThingGen.CreateParcel("parcel_salary", ThingGen.Create("money").SetNum(num)));
    }

    // the income part of Faction.OnAdvanceMonth alone (the world's only land is the commons: no bill)
    private static void VanillaSalary(Faction faction)
    {
        var num = faction.GetResidentTax() + faction.GetRankIncome() + faction.GetFactionSalary();
        if (num <= 0) {
            return;
        }

        var bank = EClass.game.cards.container_deposit;
        if (EClass.pc.homeBranch.policies.IsActive(2711) && (bank.GetCurrency() > 0 || !bank.things.IsFull())) {
            bank.ModCurrency(num);
            Msg.Say("bankIncome", Lang._currency(num, "money"));
        } else {
            WorldMoney(num);
        }
    }

    // as Faction.OnAdvanceMonth gives the month's income
    private static void PaySalary(Faction faction, List<FactionBranch> branches)
    {
        var num = 0;
        num += faction.GetResidentTax();
        num += faction.GetRankIncome();
        num += faction.GetFactionSalary();
        if (num <= 0) {
            return;
        }

        var bank = EClass.game.cards.container_deposit;
        var bankPays = EClass.pc.homeBranch.policies.IsActive(2711) && (bank.GetCurrency() > 0 || !bank.things.IsFull());
        // (each team's share goes in its own bank when they keep their own, see TeamBoxes)
        if (bankPays && !TeamBoxes.On) {
            bank.ModCurrency(num);
            Msg.Say("bankIncome", Lang._currency(num, "money"));
            return;
        }

        if (bankPays) {
            Msg.Say("bankIncome", Lang._currency(num, "money"));
        }

        // one share per team, paid to its members (their own accounts) instead of the world's mailbox: what a team's
        // lands earn is not the host's. The guilds' salary (what the lands do not explain) goes with the first team
        // that has players, like the extra tax; with the teams' boxes on it is each owner's own (see GuildSalaries)
        // or the world's (the left-over below)
        // (a land of no team: its claimer's; the commons: nobody's, the world's)
        var shares = branches.GroupBy(b => Payer(b.owner)).OrderBy(g => g.Key.Team).ThenBy(g => g.Key.Claimer)
            .Select(g => (Members: (g.Key.Team != Teams.Wanderers ? Teams.MembersOf(g.Key.Team) : g.Key.Claimer > 0 ? [g.Key.Claimer] : []).ToArray(),
                Share: g.Sum(b => b.GetResidentTax() + EClass.game.spatials.ranks.GetIncome(b.owner)))).ToList();
        var salary = Math.Max(0, num - shares.Sum(s => s.Share));
        var host = NetSession.Instance.Transport as ElinNetHost;
        var left = num;
        if (TeamBoxes.On) {
            left -= GuildSalaries(host, bankPays);
            salary = 0;
        }

        foreach (var (members, own) in shares) {
            var share = own;
            if (members.Length > 0) {
                share += salary;
                salary = 0;
            }

            share = Math.Min(share, left);
            if (host is null || members.Length == 0 || share <= 0) {
                continue;
            }

            left -= share;
            if (bankPays) {
                // (the owner's own bank: its team's, or the claimer's own for a land of no team)
                (TeamBoxes.On ? TeamBoxes.Box(StoryOwner.Of(members[0]), 0) : TeamBoxes.BankOfTeam(Teams.TeamOf(members[0])))!.ModCurrency(share);
                continue;
            }

            foreach (var uid in members) {
                host.PayPlayer(uid, share / members.Length + (uid == members[0] ? share % members.Length : 0));
            }
        }

        // (lands of no team, and what the split left: the world's bank or mailbox, as before)
        if (left > 0 && bankPays) {
            bank.ModCurrency(left);
        } else if (left > 0) {
            WorldMoney(left);
        }
    }

    // Guilds per owner: each owner is paid the salary of its own guild ranks (the game: rank * 100 in a guild it is a
    // member of). The local owner's ranks are the live ones, the others' their stored story. Box off for the guilds:
    // the salary stays in the sum, and what the lands do not explain goes to the world's bank or mailbox. Returns what
    // the live game's own ranks already counted in the month's sum (so the world's left-over does not pay it twice)
    private static int GuildSalaries(ElinNetHost? host, bool bankPays)
    {
        if (!TeamOwnStory.Guilds || host is null) {
            return 0;
        }

        var paidLive = 0;
        foreach (var owner in StoryOwner.All().ToArray()) {
            var pay = (TeamOwnStory.Of(owner).Guilds ?? []).Values
                .Where(g => g.Length == 3 && g[0] == (int)FactionRelation.RelationType.Member).Sum(g => g[1] * 100);
            if (pay <= 0) {
                continue;
            }

            if (owner == TeamOwnStory.LocalOwner) {
                paidLive = pay;
            }

            var members = (owner > 0 ? Teams.MembersOf(owner) : [-owner]).ToArray();
            if (bankPays) {
                TeamBoxes.Box(owner, 0).ModCurrency(pay);
            } else if (members.Length > 0) {
                foreach (var uid in members) {
                    host.PayPlayer(uid, pay / members.Length + (uid == members[0] ? pay % members.Length : 0));
                }
            }

            EmpLog.Information("{Owner}: {Pay} guild salary", StoryOwner.Name(owner), pay);
        }

        return paidLive;
    }

    // as Faction.EvadeTax, with the evasion of one team's lands
    private static int Evade(int v, int evasion)
    {
        return (int)(long)(v * 100f / (100f + Mathf.Sqrt(evasion * 5)));
    }

    // as Faction.GetFameTax, on that fame
    private static int FameTax(int fame)
    {
        var num = EClass.curve(fame * 2, 10000, 2000, 80);
        return num is > 1000000 or < 0 ? 1000000 : num;
    }

    /// <summary>
    ///     The highest fame of that team's players (the Wanderers: the players in no team). Without personal fame,
    ///     the world's
    /// </summary>
    private static int TeamFame(int team)
    {
        if (!PersonalQuests.Enabled) {
            return EClass.player.fame;
        }

        return ElinNetHost.PlayerCharaUids().Where(uid => Teams.TeamOf(uid) == team)
            .Select(ElinNetHost.FameOfPlayer).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    ///     Four unpaid bills or more cost 50 karma to the players of that team only (the game: to "the player" when
    ///     the world has four). Without personal karma there is one karma for everyone: once
    /// </summary>
    private static void Punish()
    {
        // (every team, and each player in no team on its own)
        var owners = Teams.Ids().Where(team => team != Teams.Wanderers)
            .Concat(ElinNetHost.PlayerCharaUids().Where(uid => Teams.TeamOf(uid) == Teams.Wanderers).Select(uid => -uid));
        var teams = owners.Distinct().Where(team => UnpaidOf(team) >= 4).ToArray();
        if (teams.Length == 0) {
            return;
        }

        if (!PersonalQuests.Enabled) {
            EClass.player.ModKarma(-50);
            return;
        }

        foreach (var team in teams) {
            EmpLog.Information("Owner {Owner}: {Count} unpaid tax bills, 50 karma from its players", team, UnpaidOf(team));
            if (StoryOwner.Of(EClass.player.uidChara) == team) {
                EClass.player.ModKarma(-50);
            }

            // (the others' karma is in their own games: told by the keeper, as WorldKeeperPlayers does)
            if (NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false } host && NetSession.Instance.Rules.UseWorldKeeper) {
                host.GiveKarmaToTeam(team, -50);
            }
        }
    }
}
