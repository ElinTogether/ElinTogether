using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     The teams (<see cref="Teams" />), from the host: the whole book, each time it changes and when a player joins.
///     The rule runs in every game, a guest's own included
/// </summary>
[MessagePackObject]
public class TeamsDelta : ElinDelta
{
    /// <summary>
    ///     Team id -> leader uid, open (1) or not (0), then the member chara uids
    /// </summary>
    [Key(0)]
    public required Dictionary<int, int[]> TeamList { get; init; }

    [Key(1)]
    public required Dictionary<int, string> Names { get; init; }

    /// <summary>
    ///     Zone uid -> team id
    /// </summary>
    [Key(2)]
    public required Dictionary<int, int> Zones { get; init; }

    /// <summary>
    ///     Card uid -> owner chara uid
    /// </summary>
    [Key(3)]
    public required Dictionary<int, int> Owners { get; init; }

    /// <summary>
    ///     Chara uid -> the team it asks to join, waiting for the leader
    /// </summary>
    [Key(4)]
    public Dictionary<int, int>? Requests { get; init; }

    /// <summary>
    ///     Team id -> its unpaid tax bills, -1 -> the world's bills of no team (<see cref="TeamBills" />); none while
    ///     the box is off
    /// </summary>
    [Key(5)]
    public Dictionary<int, int>? Bills { get; init; }

    /// <summary>
    ///     Team id -> the furthest main-quest phase it took part in (<see cref="TeamStory" />); none while the box is off
    /// </summary>
    [Key(6)]
    public Dictionary<int, int>? Story { get; init; }

    /// <summary>
    ///     Chara uid -> the team that invited it, waiting for its answer
    /// </summary>
    [Key(7)]
    public Dictionary<int, int>? Invites { get; init; }

    /// <summary>
    ///     Zone uid -> the rule of that land (<see cref="Teams.PackRule" />); a land with none has the default
    /// </summary>
    [Key(8)]
    public Dictionary<int, int>? ZoneRules { get; init; }

    /// <summary>
    ///     Team id -> its banner, emblem and colour (<see cref="TeamBanner" />); none while the box is off
    /// </summary>
    [Key(9)]
    public Dictionary<int, (int Icon, int Color)>? Banners { get; init; }

    /// <summary>
    ///     Team id -> the source id of the game's thing chosen as its emblem (<see cref="TeamBanner" />); an older host
    ///     sends none and the element emblem of <see cref="Banners" /> is drawn
    /// </summary>
    [Key(10)]
    public Dictionary<int, string>? Things { get; init; }

    /// <summary>
    ///     The chara uids of the players who share their quests with their team (<see cref="TeamQuests" />); none while
    ///     the box is off
    /// </summary>
    [Key(11)]
    public int[]? QuestSharers { get; init; }

    /// <summary>
    ///     Chara uid -> the team that accepted its request while it was in another team, waiting for its choice
    /// </summary>
    [Key(12)]
    public Dictionary<int, int>? Offers { get; init; }

    /// <summary>
    ///     Team id -> its ranks and who holds which (<see cref="TeamRanks.Pack" />), for the teams that edited them; an
    ///     older host sends none and every team has the default ranks
    /// </summary>
    [Key(13)]
    public Dictionary<int, string>? Ranks { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return "not a request";
    }

    protected override void OnApply(ElinNetBase net)
    {
        // the host's own book is the one in its save; a zone hosted by a client learns it as the client it also is
        if (net is ElinNetHost || FromPeer) {
            return;
        }

        Teams.Learn(this);
    }
}
