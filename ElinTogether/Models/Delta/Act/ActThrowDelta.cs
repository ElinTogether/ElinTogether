using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ActThrowDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required Position Point { get; init; }

    [Key(2)]
    public required RemoteCard Target { get; init; }

    [Key(3)]
    public required RemoteCard Thing { get; init; } // split

    [Key(4)]
    public required ThrowMethod Method { get; init; }

    [Key(5)]
    public required int SplitNum { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (!core.IsGameStarted) {
            return;
        }

        var t = ((Thing)Thing)!.Split(SplitNum);
        if (net.IsHost) {
            net.Delta.DeferRemote(new ActThrowDelta {
                Owner = Owner,
                Point = Point,
                Target = Target,
                Thing = t,
                Method = Method,
                SplitNum = SplitNum,
            });
        }

        ActThrow.Throw(Owner, Point, Target, t, Method);
    }
}