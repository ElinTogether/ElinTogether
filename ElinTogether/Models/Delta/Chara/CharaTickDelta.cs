using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaTickDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara) {
            return;
        }

        // do not remote tick a client
        if (chara.IsPC) {
            return;
        }

        if (!chara.IsInActiveMap) {
            return;
        }

        // we are host, relay the client tick to other players
        if (net is ElinNetHost host) {
            // clients only tick their remote pc
            if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender) || sender != chara) {
                EmpLog.Warning("Refusing remote tick on {Uid} from peer {PeerIndex}",
                    Owner.Uid, OriginPeer);
                return;
            }

            net.Delta.AddRemote(this);
            ActionModeCombat.OnRemotePlayerTick(chara);
        }

        // do a remote tick
        chara.Stub_Tick();
    }
}