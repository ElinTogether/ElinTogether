using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CombatReadyDelta : ElinDelta
{
    [Key(0)]
    public required bool Ready { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // client -> host intent only
        if (net is not ElinNetHost host) {
            return;
        }

        if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var chara)) {
            return;
        }

        ActionModeCombat.OnRemoteTaskReport(chara.uid, Ready);
    }
}