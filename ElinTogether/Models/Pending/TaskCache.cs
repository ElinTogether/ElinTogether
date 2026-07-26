using System.Linq;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Net;

namespace ElinTogether.Models;

internal static class TaskCache
{
    internal static Position? GetRequiredPos(AIAct? act)
    {
        return act switch {
            BaseTaskHarvest task => task.pos,
            TaskCut task => task.pos,
            TaskChopWood task => task.pos,
            _ => null,
        };
    }

    internal static bool IsPosTaken(Position pos, Chara requester)
    {
        return EClass._map is { } map &&
               map.charas.Any(chara => chara != requester && pos == GetActPos(chara));
    }

    internal static void CancelClientAct(ElinNetBase net, ElinDelta delta, RemoteCard target)
    {
        if (net is not ElinNetHost host || delta.OriginPeer == 0) {
            return;
        }

        EmpLog.Warning("Refusing stale {DeltaType} from peer {PeerIndex}, uid {Uid} is gone here",
            delta.GetType().Name, delta.OriginPeer, target.Uid);

        // host cannot continue client act here
        net.Delta.AddRemote(new CardModNumDelta {
            Card = target,
            Num = 0,
        });

        if (host.ActiveRemoteCharas.TryGetValue(delta.OriginPeer, out var chara) &&
            (chara.ai as GoalRemote)?.child is { } act) {
            RequestCancel(net, chara, act);
        }
    }

    internal static void RequestCancel(ElinNetBase net, RemoteCard owner, AIAct act)
    {
        var actType = act is DelegateProgress delegated ? delegated.ActType : act.GetType();

        if (!ActMappingValidator.Default.ActToIdMapping.TryGetValue(actType, out var actId)) {
            return;
        }

        net.Delta.AddRemote(new CharaTaskCancelDelta {
            Owner = owner,
            ActId = actId,
        });
    }

    private static Position? GetActPos(Chara chara)
    {
        for (var act = chara.ai; act is not null; act = act.child) {
            if (act.status == AIAct.Status.Running && GetRequiredPos(act) is { } pos) {
                return pos;
            }
        }

        return null;
    }
}