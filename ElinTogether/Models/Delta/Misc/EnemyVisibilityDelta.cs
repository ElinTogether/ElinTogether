using System.Linq;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class EnemyVisibilityDelta : ElinDelta
{
    [Key(0)]
    public required int PlayerId { get; init; }

    [Key(1)]
    public required bool Visible { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            var visiblePrev = ActionModeCombat.EnemyVisibility.Values.Any(v => v);
            ActionModeCombat.EnemyVisibility[PlayerId] = Visible;
            var visible = ActionModeCombat.EnemyVisibility.Values.Any(v => v);
            if (visible == visiblePrev) {
                return;
            }

            net.Delta.AddRemote(new EnemyVisibilityDelta {
                PlayerId = pc.uid,
                Visible = visible,
            });

            return;
        }

        ActionModeCombat.EnemyVisibility[PlayerId] = Visible;
        if (CharaVisibilityChangeEvent.HasNoEnemyInSight()) {
            if (ActionModeCombat.EnemyVisibility.TryGetValue(pc.uid, out var value) && value is false) {
                return;
            }

            ActionModeCombat.EnemyVisibility[pc.uid] = false;
            net.Delta.AddRemote(new EnemyVisibilityDelta {
                PlayerId = pc.uid,
                Visible = false,
            });
        }
    }
}