using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Mailbox (host box "Mailbox"): what a player asks the host about its letters. The host never takes the sender's
///     word for anything: the sender is the character of the peer, every item uid is looked up in that character's
///     own bag, the letter taken or deleted must be addressed to it (<see cref="Mailbox" />)
/// </summary>
[MessagePackObject]
public class MailRequestDelta : ElinDelta
{
    public const int List = 0;
    public const int Send = 1;
    public const int Take = 2;
    public const int Delete = 3;
    public const int Read = 4;
    // an admin: read a letter marked broken once more (Mailbox.Retry)
    public const int Retry = 5;

    [Key(0)]
    public required int Kind { get; init; }

    // the letter taken, deleted or read
    [Key(1)]
    public int LetterId { get; set; }

    // a letter sent: the chara uid of its recipient, its text, gold and the uids of the stacks of the sender's bag
    [Key(2)]
    public int To { get; set; }

    [Key(3)]
    public string Text { get; set; } = "";

    [Key(4)]
    public int Gold { get; set; }

    [Key(5)]
    public int[] Items { get; set; } = [];

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return Kind is < List or > Retry ? "no such request" : NoCharacter(sender);
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost host) {
            Mailbox.OnRequest(host, OriginPeer, this);
        }
    }
}

/// <summary>
///     Mailbox: the host tells a player the letters it holds for it and who it can write to. Notice: something new
///     came (the player just joined, or a letter arrived), its game says so if a letter it may see waits unclaimed
/// </summary>
[MessagePackObject]
public class MailStateDelta : ElinDelta
{
    [Key(0)]
    public List<MailLetter> Letters { get; set; } = [];

    // who can be written to: chara uids and their names
    [Key(1)]
    public int[] Uids { get; set; } = [];

    [Key(2)]
    public string[] Names { get; set; } = [];

    // the text key of why the last request was refused, empty when it was not
    [Key(3)]
    public string Reason { get; set; } = "";

    [Key(4)]
    public bool Notice { get; set; }

    // the text key of what the last request did (sent, taken), empty for none
    [Key(5)]
    public string Done { get; set; } = "";

    // to an admin only: the letters of the whole book whose stacks could not be read (no stacks in them, only their
    // count), for "Try again"
    [Key(6)]
    public List<MailLetter> Broken { get; set; } = [];

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return "not a request";
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetClient) {
            Mailbox.Show(this);
        }
    }
}

/// <summary>
///     One letter as its recipient sees it. Items: each stack as the game writes a thing (LZ4Bytes of the thing),
///     read by the recipient's game for its name and icon only
/// </summary>
[MessagePackObject]
public class MailLetter
{
    [Key(0)]
    public int Id { get; set; }

    [Key(1)]
    public int From { get; set; }

    [Key(2)]
    public string FromName { get; set; } = "";

    [Key(3)]
    public ulong FromUser { get; set; }

    // when it was sent (or sent back), real time, seconds since 1970
    [Key(4)]
    public long Sent { get; set; }

    [Key(5)]
    public int Flags { get; set; }

    [Key(6)]
    public int Gold { get; set; }

    [Key(7)]
    public string Text { get; set; } = "";

    [Key(8)]
    public List<byte[]> Items { get; set; } = [];

    // who it is for, in the admins' list of broken letters only
    [Key(9)]
    public string ToName { get; set; } = "";
}
