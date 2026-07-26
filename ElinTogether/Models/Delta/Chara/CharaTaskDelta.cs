using ElinTogether.Elements;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaTaskDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required TaskArgsBase? TaskArgs { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        var act = TaskArgs?.CreateSubAct();
        if (net.IsHost && TaskCache.GetRequiredPos(act) is { } pos &&
            TaskCache.IsPosTaken(pos, chara)) {
            EmpLog.Debug("Task {Act} on {Tile} refused for chara {Uid}, pos already taken",
                act!.GetType().Name, pos, Owner.Uid);
            TaskCache.RequestCancel(net, Owner, act!);
            return;
        }

        // relay to clients
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (chara.ai is not GoalRemote remote) {
            return;
        }

        // now assign new task or reset
        using (Simulate(net.IsHost && RemoteCraft.IsHostRun(act))) {
            remote.InsertAction(act);
        }
    }
}