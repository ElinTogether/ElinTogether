using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Which policies of the base are on. A player switches them in its policy window, on its own copy of the base:
///     a guest's never reached the host, who runs the base, and the host's never reached the guests <br />
///     The whole set is sent, not the click: applying it twice changes nothing
/// </summary>
[MessagePackObject]
public class PolicyStateDelta : ElinDelta
{
    [Key(0)]
    public required int[] Active { get; init; }

    internal static int[] Read(FactionBranch branch)
    {
        return branch.policies.list.Where(policy => policy.active).Select(policy => policy.id).OrderBy(id => id).ToArray();
    }

    // policies the policy window never lets be on together (ContentPolicy.CanActivate)
    private static readonly (int, int)[] Exclusive = [(2515, 2516), (2814, 2823)];

    /// <summary>
    ///     A set the policy window could make: within the base's points, never two that exclude each other (security
    ///     audit 2026-10-27, item 10: every policy at once, past what the base affords)
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (_zone?.branch is not { } branch) {
            return null;
        }

        var cost = branch.policies.list.Where(policy => Active.Contains(policy.id)).Sum(policy => policy.Cost);
        // (a base that already spends more, its points lowered since, may keep what it has)
        if (cost > System.Math.Max(branch.MaxAP, branch.policies.CurrentAP())) {
            return $"policies costing {cost}, the base has {branch.MaxAP}";
        }

        return Exclusive.Any(pair => Active.Contains(pair.Item1) && Active.Contains(pair.Item2)) ? "two policies that exclude each other" : null;
    }

    internal override void OnHostRefused(ElinNetHost host)
    {
        if (_zone?.branch is { } branch) {
            host.SendDeltaTo(OriginPeer, new PolicyStateDelta { Active = Read(branch) });
        }
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (_zone?.branch is not { } branch) {
            return;
        }

        if (net is ElinNetHost host) {
            // a player standing on this base, not one travelling
            if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender) || host.IsAwayPeer(OriginPeer)) {
                return;
            }

            // another team's land: its laws stay, the sender gets them back (independence audit, base management 1)
            if (!Teams.CanTouch(sender, _zone) || !TeamRanks.MayManageBase(sender, _zone)) {
                host.SendDeltaTo(OriginPeer, new PolicyStateDelta { Active = Read(branch) });
                return;
            }

            // only the host manages the base: the sender gets the set the host keeps, which puts its copy back
            if (NetSession.Instance.Rules.HostManagesBase && !host.IsZoneSession) {
                host.SendDeltaTo(OriginPeer, new PolicyStateDelta { Active = Read(branch) });
                return;
            }

            host.Delta.AddRemote(this);
        }

        foreach (var policy in branch.policies.list) {
            policy.active = Active.Contains(policy.id);
        }

        branch.policies.RefreshEffects();

        // the window open here shows it
        if (ui.GetLayer<LayerPolicy>() is { } layer) {
            layer.RefreshPolicyIcons();
        }
    }
}
