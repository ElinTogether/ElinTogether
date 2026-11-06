using System.Linq;
using ElinTogether.Helper.Extensions;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     The deposit box of the copy shops (Kettle copies items, Demitas spellbooks): the dialog makes it in the game that
///     talks (<c>c_copyContainer</c> of the merchant), which a client's game would destroy with the end of the frame and
///     the item put in it with it. The client asks the host instead; the host makes the box once per merchant, whoever
///     asks first, as the game does, and answers with it. The client then plays the step again on that box, where a drop
///     is a drop into any other box of the host <br />
///     The copies themselves are the host's restock, see <see cref="OnBarterDelta" /> and <see cref="DramaCopyShopPatch" />
/// </summary>
[MessagePackObject]
public class CopyShopDelta : ElinDelta
{
    // the dialog that waits for the answer, played again when it comes
    private static DramaSequence? _asked;
    private static string? _askedStep;

    [Key(0)]
    public required RemoteCard Shop { get; init; }

    /// <summary>
    ///     None in the request, the box with what is in it in the answer
    /// </summary>
    [Key(1)]
    public RemoteCard? Container { get; init; }

    /// <summary>
    ///     The step "_copyItem" of a client whose merchant has no box from the host yet: true when the request replaces it
    /// </summary>
    internal static bool Ask(DramaSequence sequence)
    {
        if (NetSession.Instance.Connection is not ElinNetClient client || ElinDelta.IsApplying ||
            sequence.manager?.tg is not { chara: { } shop } || shop.trait.CopyShop == Trait.CopyShopType.None ||
            shop.c_copyContainer.IsHostOwned) {
            return false;
        }

        _asked = sequence;
        _askedStep = sequence.lastStep;
        client.Delta.AddRemote(new CopyShopDelta {
            Shop = shop,
        });
        return true;
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Shop.Find() is not Chara { isDestroyed: false } shop || shop.trait.CopyShop == Trait.CopyShopType.None) {
            return;
        }

        if (net is ElinNetHost host) {
            if (Container is not null || !host.ActiveRemoteCharas.ContainsKey(OriginPeer) || host.IsAwayPeer(OriginPeer)) {
                return;
            }

            var box = shop.c_copyContainer ??= ThingGen.Create("container_deposit");
            box.things.SetSize(shop.trait.NumCopyItem, 1);
            // a box that came with the save is not in the cache yet: the drops and the takes of the players are found there
            CardCache.CacheContainer(box.things);
            host.SendDeltaTo(OriginPeer, new CopyShopDelta {
                Shop = shop,
                Container = RemoteCard.Create(box, true, true),
            });
            return;
        }

        if (Container?.Find() is not Thing found) {
            return;
        }

        CardCache.CacheContainer(found.things);
        shop.c_copyContainer = found;
        // what came with it has the host's uids: keep this game's next one above them (as CardGenDelta does)
        foreach (var card in found.things.Flatten().Prepend(found)) {
            game.cards.uidNext = PendingUid.Above(game.cards.uidNext, card.uid);
        }

        // the dialog may be closed by now, or be another one
        if (_asked is not { isExited: false } sequence || sequence.manager == null || sequence.manager.layer == null ||
            sequence.manager.tg?.chara != shop || sequence.lastStep != _askedStep) {
            return;
        }

        _asked = null;
        // the step is the player's own gesture, not a state landing
        using var simulate = Simulate();
        sequence.Play("_copyItem");
    }
}
