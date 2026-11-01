using System.Linq;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     The money a base earns while the host runs it goes to the base's owner, not to "the player" (the host): the
///     goods a guest put on sale pay that guest at the price its own skills make (Negotiation reads "the player"), and
///     the day's income (shops, inn, tourism: FactionBranch.GetDailyIncome) goes to the player who claimed the land, or
///     the members of the team that holds it, equal parts. A land the host owns pays the host as the game does.
///     Always on, like the other host/guest equality fixes
/// </summary>
[HarmonyPatch]
internal static class BaseIncome
{
    /// <summary>
    ///     On a card a guest put on sale: its character number, written by the host when it applies the change
    /// </summary>
    internal const string SellerKey = "emp_seller";

    // on a land: the player who claimed it (Teams.OnClaimed)
    internal const int ClaimerKey = 47110001;

    /// <summary>
    ///     The player in no team a land is for: its own owner in the book first (a character back from a solo save
    ///     took it over), else the one who claimed it
    /// </summary>
    internal static int ClaimerOf(Zone zone)
    {
        return Teams.SoloOwnerOf(zone) is > 0 and var solo ? solo : zone.GetInt(ClaimerKey);
    }

    private static ElinNetHost? Host => NetSession.Instance.Connection is ElinNetHost { IsZoneSession: false } host ? host : null;

    // ---- goods put on sale by a guest

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AI_Shopping), nameof(AI_Shopping.Buy))]
    private static void BeforeBuy(Card dest, out (int Before, int Host, int Seller, int Uid) __state)
    {
        __state = default;
        var uid = dest.GetInt(SellerKey);
        if (Host is null || !dest.isSale || uid == 0 ||
            Host.ActiveRemoteCharas.Values.FirstOrDefault(c => c.uid == uid) is not { } seller) {
            return;
        }

        var price = dest.GetPrice(CurrencyType.Money, true, PriceType.PlayerShop);
        int sellerPrice;
        using (Models.RemoteCraft.AsCrafter(seller, false)) {
            sellerPrice = dest.GetPrice(CurrencyType.Money, true, PriceType.PlayerShop);
        }

        __state = (EClass.Branch.incomeShop, price, sellerPrice, uid);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AI_Shopping), nameof(AI_Shopping.Buy))]
    private static void AfterBuy((int Before, int Host, int Seller, int Uid) __state)
    {
        // (sold: the branch counted the host's price; it is the seller's instead)
        if (__state.Uid == 0 || Host is not { } host || EClass.Branch.incomeShop - __state.Before != __state.Host) {
            return;
        }

        EClass.Branch.incomeShop = __state.Before;
        Give(host, __state.Uid, __state.Seller);
    }

    private static void Give(ElinNetHost host, int uid, int amount)
    {
        if (amount <= 0) {
            return;
        }

        if (uid == EClass.pc.uid) {
            EClass.pc.Pick(ThingGen.Create("money").SetNum(amount));
        } else {
            host.OweMoney(uid, amount);
        }
    }

    // ---- the day's income

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FactionBranch), nameof(FactionBranch.GetDailyIncome))]
    private static bool OnDailyIncome(FactionBranch __instance)
    {
        // a player alone at its base would pay itself the whole of it from its copy, whoever owns the land: it
        // waits, and goes to the host with the base, which pays it as here (LeasedBase.TakeIncome, lease audit
        // 2026-10-29, #5)
        if (NetSession.Instance is { IsZoneAuthority: true, Transport: ElinNetClient }) {
            return false;
        }

        if (Host is not { } host) {
            return true;
        }

        var zone = __instance.owner;
        var team = Teams.TeamOfZone(zone);
        // (the commons has no owner: what it earns goes to the shared bank)
        var owners = (team != Teams.Wanderers ? Teams.MembersOfTeam(team) : Commons.Is(zone) ? [] : [ClaimerOf(zone)])
            .Where(uid => uid != 0).ToArray();
        if (owners.Length == 0 && ElinNetHost.SavedRemoteCharas.Count == 0 || owners.Length > 0 && owners.All(uid => uid == EClass.pc.uid)) {
            return true;
        }

        // a land of nobody known (claimed before the claimer was written down) in a world others play in: the
        // shared bank, never the host's purse
        var bank = owners.Length == 0;

        Pay(ref __instance.incomeShop, ref __instance.statistics.shop, "getIncomeShop", false);
        Pay(ref __instance.incomeInn, ref __instance.statistics.inn, "getIncomeInn", true);
        Pay(ref __instance.incomeTourism, ref __instance.statistics.tourism, "getIncomeTourism", true);
        return false;

        // as the game's own, to the owners in equal parts (the first gets what does not divide)
        void Pay(ref int n, ref int stat, string lang, bool tax)
        {
            if (tax && !__instance.IsTaxFree) {
                n = n * 10 / 100;
            }

            if (n == 0) {
                return;
            }

            Msg.Say(lang, Lang._currency(n, "money"), zone.Name);
            stat += n;
            if (bank) {
                EClass.game.cards.container_deposit.AddThing(ThingGen.Create("money").SetNum(n));
            }

            for (var i = 0; i < owners.Length; i++) {
                Give(host, owners[i], n / owners.Length + (i == 0 ? n % owners.Length : 0));
            }

            n = 0;
        }
    }
}
