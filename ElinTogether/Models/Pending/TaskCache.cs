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

        // not in the card cache is not gone: the card may be on this map or in the container the player
        // names, only never registered. It is the one the player means: registered, and the act is done next frame
        if (CardCache.Find(target.Uid) is null && FindUncached(target) is { isDestroyed: false } real) {
            CardCache.Set(real);
            _adopted++;
            EmpLog.Warning(
                "Uid {Uid} of {DeltaType} from peer {PeerIndex} is here but was not in the card cache, " +
                "adopted and replayed ({Adopted} adopted, {Refused} refused so far)",
                target.Uid, delta.GetType().Name, delta.OriginPeer, _adopted, _refused);
            net.Delta.DeferLocal(delta);
            return;
        }

        _refused++;
        EmpLog.Warning(
            "Refusing stale {DeltaType} from peer {PeerIndex}, uid {Uid} is gone here: only that player is told " +
            "to drop it ({Refused} refused, {Adopted} adopted so far)",
            delta.GetType().Name, delta.OriginPeer, target.Uid, _refused, _adopted);

        // host cannot continue client act here. Only the player who is wrong is told: sent to everyone, a number
        // this game does not know took the card away from every player who had it
        host.SendDeltaTo(delta.OriginPeer, new CardModNumDelta {
            Card = target,
            Num = 0,
        });

        if (host.ActiveRemoteCharas.TryGetValue(delta.OriginPeer, out var chara) &&
            (chara.ai as GoalRemote)?.child is { } act) {
            RequestCancel(net, chara, act);
        }
    }

    /// <summary>
    ///     The card a player names, adopted into the card cache when this game (the host) has it but uncached: in a
    ///     chest of the map, never registered. Null when it really is not here
    /// </summary>
    internal static Card? FindOrAdopt(RemoteCard? target)
    {
        if (target is null) {
            return null;
        }

        if (target.Find() is { } card) {
            return card;
        }

        if (!NetSession.Instance.IsHost || FindUncached(target) is not { isDestroyed: false } real) {
            return null;
        }

        CardCache.Set(real);
        _adopted++;
        EmpLog.Warning("Uid {Uid} named by a player is here but was not in the card cache, adopted ({Adopted} so far)",
            target.Uid, _adopted);
        return real;
    }

    private static int _adopted;
    private static int _refused;

    /// <summary>
    ///     The card as it really is in this game: in the container the sender names, else on this map
    /// </summary>
    private static Card? FindUncached(RemoteCard target)
    {
        if (target.Type == RemoteCard.CardType.Chara) {
            return EClass._map?.FindChara(target.Uid);
        }

        // (the chest itself may be one never registered)
        return FindOrAdopt(target.Parent)?.things.Find(target.Uid) ?? EClass._map?.FindThing(target.Uid);
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