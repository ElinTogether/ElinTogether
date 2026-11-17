using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;

namespace ElinTogether.Net;

/// <summary>
///     The count of unpaid tax bills is the world's (<c>player.taxBills</c> of the game that hosts): the game adds
///     one per bill it makes and takes one off per bill paid, and takes 50 karma each month it stands at four or
///     more. Before 0.26.652 a bill paid by a player alone on another map was not always counted (three paid in a
///     row, one counted): such a world keeps a count above the bills that exist, with nothing left to pay to bring
///     it down ("phantom bills"). <br />
///     The count is brought down to the tax bills this game can find: in the bags of every character of the world
///     (the players' included), in the mail, the bank and the shipping box, and on the map shown with all it holds.
///     Never raised. A bill kept on a map that is not loaded is not seen: once the count is at zero the game
///     refuses such a bill and hands it back ("bad idea"), it is forgiven, nobody is charged twice. <br />
///     Once per world when it is hosted, and again from the host's settings tab when asked
/// </summary>
internal partial class ElinNetHost
{
    [ElinGameIOProperty("bills_repaired")]
    private static int[] BillsRepaired
    {
        get => field is { Length: 1 } ? field : field = new int[1];
        set;
    }

    private static IEnumerable<Thing> Within(Thing thing)
    {
        yield return thing;
        foreach (var held in thing.things.List(_ => true, true)) {
            yield return held;
        }
    }

    /// <summary>
    ///     The tax bills that exist, as far as this game can see
    /// </summary>
    internal static int CountTaxBills()
    {
        var charas = game.cards.globalCharas.Values.Concat(_map?.charas ?? []).Where(c => c is not null).Distinct();
        var loose = new List<Thing>();
        loose.AddRange(game.cards.listPackage);
        if (game.cards.container_deposit is { } bank) {
            loose.Add(bank);
        }

        loose.AddRange(TeamBoxes.All());
        if (game.cards.container_shipping is { } shipping) {
            loose.Add(shipping);
        }

        if (_map is not null) {
            loose.AddRange(_map.things);
        }

        return charas.Sum(c => c.things.List(t => t.id == "bill_tax", true).Count) +
               loose.Where(t => t is { isDestroyed: false }).SelectMany(Within).Distinct().Count(t => t.id == "bill_tax");
    }

    /// <param name="again">asked by the host: also when this world was already looked at</param>
    /// <returns>how many bills the count was lowered by</returns>
    internal static int RepairBillCount(bool again = false)
    {
        if (!core.IsGameStarted || player is null || NetSession.Instance.Transport is not ElinNetHost ||
            (!again && BillsRepaired[0] != 0)) {
            return 0;
        }

        BillsRepaired[0] = 1;
        var found = CountTaxBills();
        var phantom = player.taxBills - found;
        if (phantom <= 0) {
            return 0;
        }

        EmpLog.Information("Unpaid tax bills: the world counted {Count}, {Found} exist: {Phantom} that nothing could pay are taken off",
            player.taxBills, found, phantom);
        player.taxBills = found;
        TeamBills.Fit();
        return phantom;
    }

    [ElinGameIOProperty("public_bills_forgiven")]
    private static int[] PublicBillsForgiven
    {
        get => field is { Length: 1 } ? field : field = new int[1];
        set;
    }

    /// <summary>
    ///     User 2026-11-17 ("cancel and delete all the tax bills"): on the public worlds, players were billed while
    ///     nobody of theirs was connected (GitHub issues 44 and 45: "behind 56 tax bills", karma gone). Once per
    ///     world: every tax bill this game can find is destroyed, the counts go to zero, and the karma the four-bill
    ///     rule took (50 a month from the fourth unpaid bill) goes back to that owner's players, never above a new
    ///     player's
    /// </summary>
    internal static void ForgivePublicBills()
    {
        if (!core.IsGameStarted || player is null || NetSession.Instance.Transport is not ElinNetHost { IsZoneSession: false } ||
            !PublicWorld || PublicBillsForgiven[0] != 0) {
            return;
        }

        PublicBillsForgiven[0] = 1;
        var owed = new Dictionary<int, int>(TeamTaxBills);
        var returned = 0;
        foreach (var (uid, standing) in PlayerStandings.ToArray()) {
            if (standing.Length < 2 || owed.GetValueOrDefault(StoryOwner.Of(uid)) is not (>= 4 and var count)) {
                continue;
            }

            var karma = standing[StandingKarma];
            standing[StandingKarma] = Math.Max(karma, Math.Min(karma + 50 * (count - 3), NewPlayerKarma));
            returned += standing[StandingKarma] - karma;
        }

        var charas = game.cards.globalCharas.Values.Concat(_map?.charas ?? []).Where(c => c is not null).Distinct();
        var loose = new List<Thing>(game.cards.listPackage);
        loose.AddRange(TeamBoxes.All());
        if (game.cards.container_deposit is { } bank) {
            loose.Add(bank);
        }

        if (_map is not null) {
            loose.AddRange(_map.things);
        }

        var bills = charas.SelectMany(c => c.things.List(t => t.id == "bill_tax", true))
            .Concat(loose.Where(t => t is { isDestroyed: false }).SelectMany(Within).Where(t => t.id == "bill_tax"))
            .Distinct().ToList();
        foreach (var bill in bills) {
            game.cards.listPackage.Remove(bill);
            bill.Destroy();
        }

        EmpLog.Information("Public world: {Bills} tax bills destroyed, counts {Owed} and {World} set to zero, {Karma} karma given back",
            bills.Count, string.Join(",", owed.Select(kv => kv.Key + ":" + kv.Value)), player.taxBills, returned);
        TeamTaxBills.Clear();
        player.taxBills = 0;
        TeamBills.Fit();
    }

    /// <summary>
    ///     A public world: the peer-hosted one, or the dedicated official server
    /// </summary>
    internal static bool PublicWorld => PeerHosting.Active || Steam.OfficialServer.Hosting;
}
