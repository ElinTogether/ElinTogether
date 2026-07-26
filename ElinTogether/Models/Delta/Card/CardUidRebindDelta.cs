using System.Collections.Generic;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardUidRebindDelta : ElinDelta
{
    [MessagePackObject]
    public class UidBind
    {
        [Key(0)]
        public required int Pending { get; init; }

        [Key(1)]
        public required int Real { get; init; }
    }

    [Key(0)]
    public required List<UidBind> Rebinds { get; init; }


    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            return;
        }

        foreach (var rebind in Rebinds) {
            if (CardCache.Find(rebind.Pending) is { } card) {
                CardCache.Rebind(card, rebind.Real);
            }
        }
    }
}