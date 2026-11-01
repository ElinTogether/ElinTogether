using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

public enum TeamAsk : byte
{
    /// <summary>
    ///     A team named after the player, which it leads
    /// </summary>
    Found,

    /// <summary>
    ///     Id is the team
    /// </summary>
    Join,

    Leave,

    /// <summary>
    ///     The leader lets in the player who asked. Id is that player's chara uid
    /// </summary>
    Accept,

    /// <summary>
    ///     Id is the chara uid of the player who asked
    /// </summary>
    Refuse,

    /// <summary>
    ///     The container, door or bed the player installed on its team's land is its own. Id is the card's uid
    /// </summary>
    Own,

    /// <summary>
    ///     The player's own card is the team's again. Id is the card's uid
    /// </summary>
    Share,

    // (the team page, council 2026-10-28: the leader's choices, behind the host box "Team leaders manage their team")

    /// <summary>
    ///     The leader renames its team. Text is the new name
    /// </summary>
    Rename,

    /// <summary>
    ///     The leader puts a member out. Id is that member's chara uid
    /// </summary>
    Kick,

    /// <summary>
    ///     The leader hands the team to a member. Id is that member's chara uid
    /// </summary>
    Promote,

    /// <summary>
    ///     The leader ends its team: its members become Wanderers, its lands nobody's
    /// </summary>
    Disband,

    /// <summary>
    ///     The leader invites a player who is in the game. Id is that player's chara uid
    /// </summary>
    Invite,

    /// <summary>
    ///     The invited player joins. Id is the team
    /// </summary>
    AcceptInvite,

    /// <summary>
    ///     The invited player declines. Id is the team
    /// </summary>
    RefuseInvite,

    /// <summary>
    ///     The leader sets the rule of one of its team's lands (box "Land rules"). ZoneUid is the land, Id the rule
    ///     (<see cref="Teams.PackRule" />)
    /// </summary>
    LandRule,

    /// <summary>
    ///     The leader opens (Id 1) or closes (Id 0) the joining of its team: open, anyone joins at once
    /// </summary>
    SetOpen,

    /// <summary>
    ///     The leader sets its team's banner (host box "Team banners"). Id is the emblem, ZoneUid the colour
    /// </summary>
    Banner,

    /// <summary>
    ///     The player shares (Id 1) or stops sharing (Id 0) its own quests with its team (host box "Teammates share
    ///     quests", <see cref="TeamQuests" />)
    /// </summary>
    ShareQuests,

    /// <summary>
    ///     The player, accepted by a team while in another one, joins it. Id is the team that accepted
    /// </summary>
    JoinOffered,

    /// <summary>
    ///     The player, accepted by a team while in another one, keeps its team. Id is the team that accepted
    /// </summary>
    KeepTeam,
}

/// <summary>
///     A player's choice in the team menus (<see cref="Teams" />), asked of the host of the world, which keeps the
///     book: it is checked there against the book as it is now, for the player who sent it
/// </summary>
[MessagePackObject]
public class TeamRequestDelta : ElinDelta
{
    [Key(0)]
    public required TeamAsk Kind { get; init; }

    [Key(1)]
    public int Id { get; init; }

    /// <summary>
    ///     The land it is about, when it is about one
    /// </summary>
    [Key(2)]
    public int ZoneUid { get; init; }

    /// <summary>
    ///     A name typed by the player (a team to found), kept short by the host
    /// </summary>
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

        Teams.Answer(sender, Kind, Id, ZoneUid, Text ?? "");
    }
}
