using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaFaithDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required string FaithId { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        if (game.religions.Find(FaithId) is not { } religion) {
            EmpLog.Warning("Unknown religion {FaithId} for chara {Uid}, heretic!!",
                FaithId, chara.uid);
            return;
        }

        if (chara.faith == religion) {
            return;
        }

        if (net is ElinNetHost host) {
            if (host.ActiveRemoteCharas.TryGetValue(OriginPeer, pc) != chara) {
                EmpLog.Warning("Rejecting religion change of {Uid} from peer {PeerIndex}",
                    chara.uid, OriginPeer);
                return;
            }

            using var _ = Simulate();
            religion.JoinFaith(chara);
            net.Delta.AddRemote(this);
            return;
        }

        // client sim
        religion.JoinFaith(chara);
    }
}