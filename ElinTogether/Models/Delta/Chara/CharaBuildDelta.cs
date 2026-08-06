using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaBuildDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Held { get; init; }

    [Key(1)]
    public required RemoteCard Owner { get; init; }

    [Key(2)]
    public required Position Pos { get; init; }

    [Key(3)]
    public required int Dir { get; init; }

    [Key(4)]
    public required int Altitude { get; init; }

    [Key(5)]
    public required int BridgeHeight { get; init; }

    [Key(6)]
    public int TargetUid { get; set; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara || Held.Find() is not { } held) {
            return;
        }

        // relay to clients
        if (held.parent is not Card) {
            EmpLog.Warning("Refusing stale {DeltaType} from peer {PeerIndex}, held {Uid} is no longer in inventory",
                nameof(CharaBuildDelta), OriginPeer, held.uid);
            return;
        }

        var taskBuild = new TaskBuild {
            owner = chara,
            recipe = held.trait.GetRecipe(),
            held = held,
            pos = Pos,
            dir = Dir,
            altitude = Altitude,
            bridgeHeight = BridgeHeight,
        };

        if (taskBuild.useHeld && chara.held != held) {
            chara.HoldCard(held);
        }

        taskBuild.recipe._dir = Dir;
        taskBuild.OnProgressComplete();

        if (net.IsHost) {
            TargetUid = (taskBuild.target?.uid).GetValueOrDefault();
            net.Delta.AddRemote(this);
        } else if (TargetUid > 0 && taskBuild.target is { isDestroyed: false } target && target.uid != TargetUid) {
            if (CardCache.Find(TargetUid) is { } orphan && orphan != target) {
                CardCache.DelayDestroy(orphan);
            }

            CardCache.Rebind(target, TargetUid);
            EmpLog.Debug("Rebound built target of chara {OwnerUid} to host uid {Uid}",
                chara.uid, TargetUid);
        }
    }
}