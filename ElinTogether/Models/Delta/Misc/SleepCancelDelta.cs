using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SleepCancelDelta : ElinDelta
{
    protected override void OnApply(ElinNetBase net)
    {
        // client -> host intent only
        if (net is not ElinNetHost host) {
            return;
        }

        if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var chara)) {
            return;
        }

        // too late if high rtt
        if (ui.GetLayer<LayerSleep>() is not null) {
            return;
        }

        EmpLog.Debug("Sleep cancel from {PeerIndex}", OriginPeer);

        using var _ = Simulate();
        chara.conSleep?.Kill();
    }
}
