using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaMakeAllyDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public bool ShowMsg { get; init; }

    [Key(2)]
    public required string? TemporaryAllyName { get; init; }

    /// <summary>
    ///     Chara uid of the player the companion follows, 0 for the party leader
    /// </summary>
    [Key(3)]
    public int OwnerUid { get; init; }

    /// <summary>
    ///     Already of the base, it only joins the party (see PartyJoinEvent)
    /// </summary>
    [Key(4)]
    public bool JoinOnly { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            // reject every single chara make ally delta from clients
            return;
        }

        if (game?.activeZone?.map is null) {
            net.Delta.DeferLocal(this);
            return;
        }

        if (Owner.Find() is not Chara chara) {
            // chara doesn't exist yet
            net.Delta.DeferLocal(this);
            return;
        }

        if (chara.IsPC) {
            return;
        }

        chara.c_altName = TemporaryAllyName;
        // received whole from the host, already one of the world's characters there: the game only lists
        // the ones it makes global itself, and this one would be missing from the list (and from a trip)
        if (chara.IsGlobal && game.cards.globalCharas.Find(chara.uid) is null) {
            game.cards.globalCharas.Add(chara);
        }

        // (never on a player, see CompanionHelper.CompanionOwnerUid)
        chara.SetInt(CompanionHelper.OwnerKey, chara.GetBool("remote_chara") ? 0 : OwnerUid);
        if (JoinOnly) {
            pc.party.AddMemeber(chara, ShowMsg);
            return;
        }

        // a companion that only comes back is one of ours already: the game's recruiting forgets the tile it was
        // told to stay on ("stay here"), which this game then no longer had while the host did
        var stays = chara.IsPCFaction ? chara.orgPos : null;
        chara.Stub_MakeAlly(ShowMsg);
        if (stays is not null) {
            chara.orgPos = stays;
        }
    }
}