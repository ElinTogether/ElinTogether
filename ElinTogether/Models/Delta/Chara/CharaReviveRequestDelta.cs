using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A guest asks the host to bring back a dead companion at the barman's: its own game would pay and lose the
///     revive (Chara.Revive of a non-player is dropped there), and its payment would leave the companion dead.
///     The host checks, takes the price from that player's purse once and revives it next to that player, see
///     <see cref="RemoteRevivePatch" />. The companion keeps its owner
/// </summary>
[MessagePackObject]
public class CharaReviveRequestDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Target { get; init; }

    /// <summary>
    ///     GitHub issue 59: sent by a player travelling alone, who paid in its own game (its purse lives there while it
    ///     travels, its character comes back with its upload). Only for a dead player, revived where it fell
    /// </summary>
    [Key(1)]
    public bool PaidAway { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // all of it is read again from this game's state: a request that comes twice finds it alive and pays nothing
        if (net is not ElinNetHost host || host.IsAwayPeer(OriginPeer) != PaidAway ||
            (PaidAway ? host.PlayerChara(OriginPeer) : host.ActiveRemoteCharas.GetValueOrDefault(OriginPeer)) is not { } sender ||
            Target.Find() is not Chara { isDead: true, isSummon: false, IsGlobal: true } dead || !dead.CanRevive() ||
            (PaidAway && !PlayerRevive.IsPlayerChara(dead)) ||
            (dead.faction != pc.faction && !PlayerRevive.IsPlayerChara(dead)) ||
            ((dead.IsPC || dead.IsRemotePlayer || PlayerRevive.IsPlayerChara(dead)) && !PlayerRevive.Active) || dead == sender) {
            // a dead player is not on the barman's list of a solo game: it has its own way back (but in permadeath
            // with PermadeathRevive, see PlayerRevive)
            return;
        }

        if (!PaidAway) {
            // the price the guest saw: its own Charisma
            int cost;
            using (RemoteCraft.AsCrafter(sender)) {
                cost = CalcMoney.Revive(dead);
            }

            if (sender.GetCurrency() < cost) {
                return;
            }

            // as the host's own gesture, the purse of the sender reaches its game as any other change made here to its things
            using (Simulate()) {
                sender.ModCurrency(-cost);
            }
        }

        if (PlayerRevive.IsPlayerChara(dead)) {
            PlayerRevive.ReviveOnHost(dead, sender);
            return;
        }

        using var simulate = Simulate();
        dead.GetRevived();
    }
}
