using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ShippingResultDelta : ElinDelta
{
    [Key(0)]
    public required long[] Ints { get; init; }

    [Key(1)]
    public required string[][] ItemStrs { get; init; }

    [Key(2)]
    public required int ShipNum { get; init; }

    [Key(3)]
    public required long ShipMoney { get; init; }

    [Key(4)]
    public required int BranchLv { get; init; }

    [Key(5)]
    public required int BranchExp { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            return;
        }

        var result = new ShippingResult {
            ints = [..Ints],
        };
        foreach (var strs in ItemStrs) {
            result.items.Add(new() {
                _strs = strs,
            });
        }

        if (player.shippingResults.LastItem() is { } last && last.rawDate == result.rawDate) {
            return;
        }

        player.shippingResults.Add(result);
        while (player.shippingResults.Count > 10) {
            player.shippingResults.RemoveAt(0);
        }

        player.stats.shipNum = ShipNum;
        player.stats.shipMoney = ShipMoney;

        var zone = game.spatials.Find(result.uidZone) ?? pc.homeZone;
        if (BranchLv > 0 && zone?.branch is { } branch) {
            branch.lv = BranchLv;
            branch.exp = BranchExp;
            branch.statistics.ship += result.GetIncome();
        }

        player.showShippingResult = core.config.game.showShippingResult;

        EmpLog.Debug("Shipping result {ShipIncome} {ShipItemCount} {ZoneUid}",
            result.GetIncome(), result.items.Count, result.uidZone);
    }
}