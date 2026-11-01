using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardModCurrencyDelta : ElinDelta
{
    [Key(0)]
    public required int Amount { get; init; }

    [Key(1)]
    public required string CurrencyId { get; init; }

    /// <summary>
    ///     What the gold was spent on when the game says so: a shop's goods, a build's tiles (null for the rest). Gold
    ///     paid for goods does not pay a debt, a plot or an investment as well (<see cref="TakeSpent" />)
    /// </summary>
    [Key(2)]
    public string? Purpose { get; init; }

    /// <summary>
    ///     Host to the sender, when the hourly gain cap cut a gain short (public servers): how much of it was not paid
    ///     and what the host's copy of its purse holds now. Its game already added the whole gain (a sale: the item
    ///     went to the merchant), it takes back the part not paid
    /// </summary>
    [Key(3)]
    public int Cut { get; init; }

    [Key(4)]
    public int Purse { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetClient) {
            if (Cut > 0) {
                TakeBackCut();
            }

            return;
        }

        // client -> host intent only
        if (net is not ElinNetHost host) {
            return;
        }

        if (host.PlayerChara(OriginPeer) is not { } chara) {
            return;
        }

        if (Refused(host)) {
            return;
        }

        // a gain over the hour's cap is paid up to the cap, not refused whole (a sale that crossed it paid nothing
        // while the merchant kept the item)
        var amount = Amount;
        if (TrustEnforced && amount > 0) {
            amount = (int)GainRoom(OriginPeer, CurrencyId, amount);
            if (amount < Amount) {
                EmpLog.Warning("Cut {Cut} {CurrencyId} from peer {Peer}: over its hourly gain", Amount - amount, CurrencyId, OriginPeer);
                ElinNetHost.AdminLog($"cut {Amount - amount} {CurrencyId} from peer {OriginPeer}: over its hourly gain");
            }
        }

        if (Amount < 0) {
            Spent.Add((OriginPeer, UnityEngine.Time.realtimeSinceStartup, -Amount, CurrencyId, Purpose ?? ""));
            Spent.RemoveAll(s => UnityEngine.Time.realtimeSinceStartup - s.At > SpentWindow);
        }

        // away from the host map the purse travels with the character, its game already paid: only the record is kept
        if (host.IsAwayPeer(OriginPeer)) {
            return;
        }

        EmpLog.Debug("Mod currency {CurrencyId} by {CurrencyAmount} on chara {Uid}",
            CurrencyId, amount, chara.uid);

        if (amount != 0) {
            using var _ = Simulate();
            chara.ModCurrency(amount, CurrencyId);
        }

        if (amount < Amount) {
            host.SendDeltaTo(OriginPeer, new CardModCurrencyDelta {
                Amount = 0,
                CurrencyId = CurrencyId,
                Cut = Amount - amount,
                Purse = chara.GetCurrency(CurrencyId),
            });
        }
    }

    /// <summary>
    ///     The part of a gain the host did not pay leaves this purse, never more than that and never below what the
    ///     host's copy holds (its own count of the stack may have reached this game already)
    /// </summary>
    private void TakeBackCut()
    {
        if (EClass.pc is not { } pc) {
            return;
        }

        var take = System.Math.Min(Cut, pc.GetCurrency(CurrencyId) - Purse);
        if (take > 0) {
            using (CardModNumDelta.Overwriting()) {
                pc.ModCurrency(-take, CurrencyId);
            }
        }

        var text = "emp_gain_capped".lang();
        Msg.SayRaw(text);
        WidgetPopText.Say(text);
    }

    // the currencies of the game a player holds; and what one may gain in an hour on a server that enforces trust
    private static readonly System.Collections.Generic.Dictionary<string, int> HourlyGain = new() {
        ["money"] = 2_000_000, ["money2"] = 2_000, ["money3"] = 2_000, ["plat"] = 2_000, ["medal"] = 200,
        ["ticket_sky"] = 50, ["casino_coin"] = 200_000,
        // the recycler's and the Ecopo merchant's points (TraitMerchantEcopo), blue capsules (the Mani statue's
        // reroll, TraitPowerStatue), a town's influence (the elder's shop; the same cap as its dialogs, DialogFlagDelta)
        ["ecopo"] = 20_000, ["drug_blue"] = 20, ["influence"] = 50,
    };

    private static readonly System.Collections.Generic.Dictionary<(int Peer, string Id), (float Since, long Gained)> Gains = [];

    internal static System.Collections.Generic.IEnumerable<string> CurrencyIds => HourlyGain.Keys;

    /// <summary>
    ///     How much of that gain a player may still make this hour; the part allowed is counted. A visitor's own upload
    ///     of its character carries its gold, which cannot go through the deltas of the map it earned it on
    /// </summary>
    internal static long GainRoom(int peer, string id, long gain)
    {
        if (!HourlyGain.TryGetValue(id, out var cap)) {
            return gain;
        }

        var now = UnityEngine.Time.realtimeSinceStartup;
        if (!Gains.TryGetValue((peer, id), out var gained) || now - gained.Since > 3600f) {
            gained = (now, 0);
        }

        var allowed = System.Math.Max(0, System.Math.Min(gain, cap - gained.Gained));
        Gains[(peer, id)] = (gained.Since, gained.Gained + allowed);
        return allowed;
    }

    // what each player paid lately: what it says it paid elsewhere (its debt in gold, land in gold bars) must have
    // left its purse
    private const float SpentWindow = 120f;
    private static readonly System.Collections.Generic.List<(int Peer, float At, long Amount, string Id, string Purpose)> Spent = [];

    /// <summary>
    ///     Gold this player paid in the last two minutes, and forgets it (one payment pays one thing)
    /// </summary>
    /// <param name="since">only what it paid from then on (a duel's stake: after the yes)</param>
    /// <param name="currency">gold unless said (gold bars for land)</param>
    /// <param name="purpose">only what was paid for that (a build's tiles); none for the payments of no particular kind,
    ///     never a shop's goods</param>
    internal static long TakeSpent(int peer, long wanted, float since = 0f, string currency = "money", string purpose = "")
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        long found = 0;
        for (var i = 0; i < Spent.Count && found < wanted; i++) {
            if (Spent[i].Peer != peer || Spent[i].Id != currency || Spent[i].Purpose != purpose || now - Spent[i].At > SpentWindow || Spent[i].At < since) {
                continue;
            }

            var take = System.Math.Min(Spent[i].Amount, wanted - found);
            found += take;
            Spent[i] = Spent[i] with { Amount = Spent[i].Amount - take };
        }

        Spent.RemoveAll(s => s.Amount <= 0);
        return found;
    }

    /// <summary>
    ///     A guest says what it gained or spent of its own currency (it trades and loots on its own maps). On a public
    ///     or dedicated server (TrustEnforced): only the game's currencies; spending always, gains up to an hourly
    ///     amount (OnApply, GainRoom) (security audit 2026-10-27: a forged amount minted any sum, any id made any item)
    /// </summary>
    private bool Refused(ElinNetHost host)
    {
        if (!TrustEnforced || HourlyGain.ContainsKey(CurrencyId)) {
            return false;
        }

        EmpLog.Warning("Refused currency {CurrencyId} from peer {Peer}: not a currency of the game", CurrencyId, OriginPeer);
        return true;
    }
}
