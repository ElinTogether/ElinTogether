using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaRemoveFromGameDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // this is a client operation
        if (net.IsHost) {
            return;
        }

        // the host takes a departing player (and its companions, and what it summoned) off its map and tells
        // everyone, that player included
        if (Owner.Find() is not Chara chara || chara.IsPC || chara.IsCompanionOf(pc) ||
            (chara.c_uidMaster != 0 && chara.c_uidMaster == pc.uid)) {
            return;
        }

        pc.party.RemoveMemberHere(chara);
        game.cards.globalCharas.Remove(chara);
        if (chara.currentZone == _zone && _map.charas.Contains(chara)) {
            _zone.RemoveCard(chara);
        } else {
            // not on this map: its position is from another one, which may not exist here
            chara.parent = null;
            chara.currentZone = null;
        }

        // gone from this world: a stale cached copy would be reused when it comes back (CardGenDelta)
        // and its later destruction would reach the host
        CardCache.Remove(chara.uid);
        foreach (var thing in chara.things.Flatten()) {
            CardCache.Remove(thing.uid);
        }
    }
}