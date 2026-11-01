using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaFeatPointDelta : ElinDelta
{
    internal override OverrideOrder Order => OverrideOrder.Last;

    internal override int OverrideKey => Owner.Uid;

    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required int Feat { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara { IsPC: false } chara || Feat < 0) {
            return;
        }

        // from the host: a companion it simulates (see CharaFeatPointEvent), or another player's character
        if (net is not ElinNetHost host) {
            chara.feat = Feat;
            return;
        }

        if (host.ActiveRemoteCharas.TryGetValue(OriginPeer, pc) != chara) {
            return;
        }

        chara.feat = Feat;
        host.SendDeltaToAllExcept(OriginPeer, this);
    }
}