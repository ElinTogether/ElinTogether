using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SleepRequestDelta : ElinDelta
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

        if (chara is { isDead: true } || chara.conSleep is not null) {
            return;
        }

        EmpLog.Debug("Sleep request from {PeerIndex}", OriginPeer);

        using var _ = Simulate();
        chara.AddCondition<ConSleep>(50, true);
    }
}