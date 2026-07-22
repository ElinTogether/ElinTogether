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

    protected override void OnApply(ElinNetBase net)
    {
        var t = ((Thing)Thing)!.Split(Thing.Num);
        ActThrow.Throw(Owner, Point, Target, t, Method);

        if (net.IsHost) {
            net.Delta.AddRemote(new ActThrowDelta {
                Owner = Owner,
                Point = Point,
                Target = Target,
                Thing = t,
                Method = Method,
            });
        }
    }
}