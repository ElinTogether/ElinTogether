using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ThingDelta : ElinDelta
{
    [IgnoreMember]
    public bool Valid;

    [Key(0)]
    public required RemoteCard? Thing { get; init; }

    [Key(1)]
    public required string Slot { get; init; }

    protected override void OnApply(ElinNetBase net) { }
}