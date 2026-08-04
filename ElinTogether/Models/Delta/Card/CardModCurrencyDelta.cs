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

    protected override void OnApply(ElinNetBase net)
    {
        // client -> host intent only
        if (net is not ElinNetHost host) {
            return;
        }

        if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var chara)) {
            return;
        }

        EmpLog.Debug("Mod currency {CurrencyId} by {CurrencyAmount} on chara {Uid}",
            CurrencyId, Amount, chara.uid);

        using var _ = Simulate();
        chara.ModCurrency(Amount, CurrencyId);
    }
}