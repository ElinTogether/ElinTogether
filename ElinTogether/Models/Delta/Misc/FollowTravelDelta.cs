using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Follow across maps (GitHub issue 75, host box "Follow goes across maps"): between a follower's game and its
///     leader's, through the host like a private chat line (also to and from a player on a map of its own). Start and
///     Stop come from the follower; Where comes from the leader, after each zone it arrives in and each of its steps on
///     the world map, to its followers only; LeaderGone from the leader means it is dead. See <see cref="PlayerFollow" />
/// </summary>
[MessagePackObject]
public class FollowTravelDelta : ElinDelta
{
    internal const byte Start = 0;
    internal const byte Stop = 1;
    internal const byte Where = 2;
    internal const byte LeaderGone = 3;

    [Key(0)]
    public required byte Kind { get; init; }

    [Key(1)]
    public int FromUid { get; init; }

    [Key(2)]
    public int ToUid { get; init; }

    // Where: the zone the leader is in, its tile there, the transition its game entered it with
    [Key(3)]
    public int ZoneUid { get; init; }

    [Key(4)]
    public int X { get; init; }

    [Key(5)]
    public int Z { get; init; }

    [Key(6)]
    public int State { get; init; }

    [Key(7)]
    public int EnterX { get; init; }

    [Key(8)]
    public int EnterZ { get; init; }

    /// <summary>
    ///     A player speaks as its own character, when the host lets Follow travel
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (!PlayerFollow.Travels) {
            return "follow across maps is off";
        }

        if (sender?.uid != FromUid) {
            return "a follow sent as another player";
        }

        return Kind > LeaderGone ? "an unknown follow message" : null;
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (!core.IsGameStarted) {
            return;
        }

        if (EClass.pc is { } pc && ToUid == pc.uid) {
            PlayerFollow.Receive(this);
        } else if (net is ElinNetHost host && FromPeer && NetSession.Instance.Transport == host) {
            PlayerFollow.Relay(host, this);
        }
    }
}
