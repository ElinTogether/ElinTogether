using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

public enum RankOp : byte
{
    /// <summary>Value is the rank's permissions (<see cref="TeamPerm" />)</summary>
    SetPerms,

    /// <summary>Value is the rank's daily withdraw limit (one of <see cref="TeamRanks.Limits" />)</summary>
    SetLimit,

    /// <summary>Text is the rank's new name (empty: its default name)</summary>
    Rename,

    /// <summary>A new rank at the bottom of the list. Text is its name</summary>
    Add,

    Remove,

    /// <summary>Value -1 moves the rank up, 1 down</summary>
    Move,

    /// <summary>The rank new members get</summary>
    SetDefault,

    /// <summary>Value is the chara uid of the member given the rank</summary>
    Assign,
}

/// <summary>
///     A change to the ranks of the sender's team (<see cref="TeamRanks" />), asked of the host of the world, which
///     keeps the book: checked there against the sender's rank as it is now
/// </summary>
[MessagePackObject]
public class TeamRankRequestDelta : ElinDelta
{
    [Key(0)]
    public required RankOp Op { get; init; }

    [Key(1)]
    public int Rank { get; init; }

    [Key(2)]
    public int Value { get; init; }

    [Key(3)]
    public string? Text { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return NoCharacter(sender);
    }

    protected override void OnApply(ElinNetBase net)
    {
        // (a zone hosted by a client keeps no book)
        if (!FromPeer || net is not ElinNetHost { IsZoneSession: false } host || host.PlayerChara(OriginPeer) is not { } sender) {
            return;
        }

        TeamRanks.Answer(sender, Op, Rank, Value, Text ?? "");
    }
}
