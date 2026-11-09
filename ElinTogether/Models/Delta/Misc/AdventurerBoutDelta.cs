using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A guest's bout with an adventurer, told to the host (see <see cref="AdventurerBout" />): it starts, it is won,
///     lost, or left. From the host to everyone: what the adventurer keeps of its bouts (the date of the last one, how
///     often it was beaten), which decides whether the game's dialog offers a bout
/// </summary>
[MessagePackObject]
public class AdventurerBoutDelta : ElinDelta
{
    public const int Start = 0;
    public const int Won = 1;
    public const int Lost = 2;
    public const int Left = 3;
    public const int Marks = 4;
    public const int Returned = 5;

    [Key(0)]
    public required int Kind { get; init; }

    [Key(1)]
    public required int TargetUid { get; init; }

    /// <summary>
    ///     Marks: the game's CINT.dateBout of the adventurer
    /// </summary>
    [Key(2)]
    public int DateBout { get; init; }

    /// <summary>
    ///     Marks: how often it was beaten in a bout (int 111, recruiting opens once it was)
    /// </summary>
    [Key(3)]
    public int Beaten { get; init; }

    /// <summary>
    ///     Returned, host -> the guest of the bout, away: the host's adventurer as it stands back in its town, and where
    ///     (its game dropped its own copy when it left the bout, see AdventurerBout.Leave)
    /// </summary>
    [Key(4)]
    public LZ4Bytes? Chara { get; init; }

    [Key(5)]
    public int ZoneUid { get; init; }

    [Key(6)]
    public int X { get; init; }

    [Key(7)]
    public int Z { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        // (only the host says what the adventurer keeps; a bout is the sender's own, AdventurerBout checks whose)
        return Kind is Marks or Returned ? "not a request" : NoCharacter(sender);
    }

    protected override void OnApply(ElinNetBase net)
    {
        switch (net) {
            case ElinNetHost host when Kind is not (Marks or Returned):
                AdventurerBout.OnGuest(this, host);
                break;
            case ElinNetClient when Kind == Marks:
                AdventurerBout.Learn(this);
                break;
            case ElinNetClient when Kind == Returned:
                AdventurerBout.BackHome(this);
                break;
        }
    }
}
