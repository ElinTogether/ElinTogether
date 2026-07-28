using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaProgressBeginDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required Position Pos { get; init; }

    [Key(2)]
    public required int MaxProgress { get; init; }

    [Key(3)]
    public required int ActId { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        if (!ActMappingValidator.Default.IdToActMapping.TryGetValue(ActId, out var type)) {
            return;
        }

        if (chara.isDead) {
            if (net.IsHost) {
                EmpLog.Warning("Progress begin {ActType} of chara {Uid} refused, chara is dead, requesting cancel",
                    type.Name, Owner.Uid);
                net.Delta.AddRemote(new CharaTaskCancelDelta {
                    Owner = Owner,
                    ActId = ActId,
                });
            }

            return;
        }

        if (chara.ai is not GoalRemote remote) {
            return;
        }

        var ai = remote.Current;
        while (ai is not null && ai.GetType() != type && !DelegateProgress.Represents(ai, type)) {
            ai = ai.parent;
        }

        if (ai is null) {
            if (net.IsHost) {
                EmpLog.Warning("Progress begin {ActType} of chara {Uid} has no matching act, requesting cancel",
                    type.Name, Owner.Uid);
                net.Delta.AddRemote(new CharaTaskCancelDelta {
                    Owner = Owner,
                    ActId = ActId,
                });
            }

            return;
        }

        // advance to create progress
        chara.Stub_Move(Pos, Card.MoveType.Force);

        if (ai is not DelegateProgress) {
            using (Simulate(net.IsHost && RemoteCraft.IsHostRun(ai))) {
                var guard = 0;
                while (ai.child is not AIProgress { status: AIAct.Status.Running }) {
                    ai.Tick();
                    if (ai.status != AIAct.Status.Running || ++guard > 64) {
                        break;
                    }
                }
            }
        }

        if ((ai is DelegateProgress ? ai : ai.child) is not AIProgress { status: AIAct.Status.Running } child) {
            if (net.IsHost) {
                EmpLog.Warning("Progress begin {ActType} of chara {Uid} failed to reproduce, requesting cancel",
                    type.Name, Owner.Uid);
                TaskCache.RequestCancel(net, chara, ai);
            }

            return;
        }

        child.progress = net.IsHost ? 1 : HeldProgress.Held;

        // we don't want random max progress
        if (child is Progress_Custom p) {
            p.maxProgress = MaxProgress;
        }

        // relay to clients
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }
    }
}