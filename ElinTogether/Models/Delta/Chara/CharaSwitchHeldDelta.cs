using ElinTogether.Net;
using ElinTogether.Helper;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaSwitchHeldDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required RemoteCard? HeldMainHand { get; init; }

    [Key(2)]
    public required RemoteCard? HeldOffHand { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (chara.IsPC) {
            return;
        }

        // update tool visual
        chara.NetProfile.RemoteMainHand = new(HeldMainHand, false);
        chara.NetProfile.RemoteOffHand = new(HeldOffHand, false);

        // apply held visual
        if (HeldMainHand?.Find() is { } mainHand &&
            HeldOffHand?.Find() is { } offHand &&
            mainHand == offHand &&
            mainHand.GetRootCard() == chara) {
            chara.HoldCard(mainHand);
        }
    }

    public static CharaSwitchHeldDelta Create()
    {
        var hideWeapon = pc.combatCount <= 0 && core.config.game.hideWeapons;

        RemoteCard? heldMainHand = player.currentHotItem.RenderThing
                                   ?? (hideWeapon ? pc.held : pc.body.slotMainHand?.thing);
        RemoteCard? heldOffHand = core.config.game.showOffhand
            ? pc.body.slotOffHand?.thing
            : pc.held;

        // held item override
        if (pc.held is not null) {
            heldMainHand = heldOffHand = pc.held;
        }

        return new() {
            Owner = pc,
            HeldMainHand = heldMainHand,
            HeldOffHand = heldOffHand,
        };
    }
}