using System;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class BranchResourceModDelta : ElinDelta
{
    [Key(0)]
    public required int ZoneUid { get; init; }

    [Key(1)]
    public required int ResourceType { get; init; }

    [Key(2)]
    public required int Amount { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // host only
        if (net is not ElinNetHost) {
            return;
        }

        if (!Enum.IsDefined(typeof(HomeResourceType), ResourceType)) {
            EmpLog.Warning("Refusing branch resource mod from peer {PeerIndex}, {ResourceTypeRaw} undefined",
                OriginPeer, ResourceType);
            return;
        }

        if (_zone.uid != ZoneUid) {
            EmpLog.Warning("Refusing stale branch resource mod from peer {PeerIndex}, zone {ZoneUid} invalid",
                OriginPeer, ZoneUid);
            return;
        }

        var type = (HomeResourceType)ResourceType;
        if (Branch?.resources.Get(type.ToString()) is not { } resource) {
            EmpLog.Warning("Refusing branch resource mod from peer {PeerIndex}, zone {ZoneUid} invalid",
                OriginPeer, ZoneUid);
            return;
        }

        EmpLog.Debug("Mod branch resource {ResourceType} by {ResourceAmount} in zone {ZoneUid}",
            type, Amount, ZoneUid);

        using var _ = Simulate();
        resource.Mod(Amount, false);
    }
}