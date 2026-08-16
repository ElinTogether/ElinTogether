using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaFeatPointDelta : ElinDelta
{
    internal override OverrideOrder Order => OverrideOrder.Last;

    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required int Feat { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host) {
            return;
        }

        if (Owner.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        if (host.ActiveRemoteCharas.TryGetValue(OriginPeer, pc) != chara) {
            return;
        }

        if (Feat < 0) {
            return;
        }

        chara.feat = Feat;
    }
}