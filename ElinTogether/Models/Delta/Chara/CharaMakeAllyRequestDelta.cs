using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaMakeAllyRequestDelta : ElinDelta
{
    // recruitItems
    private static readonly string[] _excluded = ["mamani2"];

    [Key(0)]
    public required RemoteCard? Owner { get; init; }

    [Key(1)]
    public required string? LocalCardId { get; init; }

    [Key(2)]
    public bool IsCopy { get; init; }

    [Key(3)]
    public bool ShowMsg { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host) {
            return;
        }

        if (Owner is null) {
            ReplayLocalCopy(host);
            return;
        }

        if (Owner.Find() is not Chara { isDead: false } chara) {
            return;
        }

        if (chara.IsPCParty) {
            RefundRecruitCost(host, chara);
            return;
        }

        using var _ = Simulate();
        chara.MakeAlly(ShowMsg);
    }

    private void ReplayLocalCopy(ElinNetHost host)
    {
        if (LocalCardId is null || !_excluded.Contains(LocalCardId)) {
            return;
        }

        var receiver = host.ActiveRemoteCharas.TryGetValue(OriginPeer, pc);
        using var _ = Simulate();
        var copy = CharaGen.Create(LocalCardId);
        _zone.AddCard(copy, receiver.pos.GetNearestPoint());
        copy.isCopy = IsCopy;
        copy.MakeAlly(ShowMsg);
    }

    // should work?
    private void RefundRecruitCost(ElinNetHost host, Chara chara)
    {
        if (chara.trait.CanInvite || chara.source.recruitItems.IsEmpty()) {
            return;
        }

        var reqs = chara.source.recruitItems[0].Split('/');
        if (reqs.Length < 2) {
            return;
        }

        var receiver = host.ActiveRemoteCharas.TryGetValue(OriginPeer, pc);
        using var _ = Simulate();
        var refund = ThingGen.Create(reqs[0]).SetNum(reqs[1].ToInt());
        _zone.AddCard(refund, receiver.pos);
    }

    public static CharaMakeAllyRequestDelta Create(Chara chara, bool msg)
    {
        var pending = PendingUid.IsPending(chara.uid);
        return new() {
            Owner = pending ? null : chara,
            LocalCardId = pending ? chara.id : null,
            IsCopy = chara.isCopy, // keep sync with host
            ShowMsg = msg,
        };
    }
}