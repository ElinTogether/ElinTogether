using System;
using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaReviveDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required string? LastWords { get; init; }

    [Key(2)]
    public Position? Pos { get; set; }

    /// <summary>
    ///     The death penalty of a player alone was applied by the game that simulates the map
    /// </summary>
    [Key(3)]
    public bool Penalty { get; set; }

    /// <summary>
    ///     World day (+1, so that 0 means none) a player's character was made in this world
    /// </summary>
    public const string BornKey = "emp_born";

    /// <summary>
    ///     The game protects a character from the death penalty for its first 90 days (Chara.Revive counts the
    ///     world's days, which is the character's own age for the host's, made with the world). A guest's character
    ///     counts from the day it was made here; one without a day (an older world, an imported one) counts from
    ///     the start of the world, as before
    /// </summary>
    internal static bool IsProtected(Chara chara)
    {
        var born = chara.GetInt(BornKey) is > 0 and var day ? day - 1 : 0;
        return player.stats.days - born <= 90 && !game.principal.disableDeathPenaltyProtection;
    }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        // a guest asks for its own character only, the revive of anything else is the host's (CharaReviveEvent)
        return NoCharacter(sender) ?? NotOwned(sender, Owner, "revived");
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara) {
            return;
        }

        if (net is ElinNetHost host) {
            // permadeath with PermadeathRevive: a dead player waits for a friend at a bartender, never stands up by itself
            if (PlayerRevive.Active && chara.isDead && PlayerRevive.IsPlayerChara(chara) && !PlayerRevive.Reviving) {
                return;
            }

            // only the game that simulates the map says so
            Penalty = false;

            // duplicate
            if (!chara.isDead) {
                Pos = chara.pos;
                host.Delta.AddRemote(this);
                return;
            }

            Point point;
            if (Pos is { IsInActiveMapBounds: true } requested) {
                point = requested;
            } else if (chara.IsInActiveMap) {
                point = chara.pos.Copy();
            } else {
                point = pc.pos.GetNearestPoint() ?? pc.pos.Copy();
            }

            if (!chara.pos.IsValid) {
                chara.pos.Set(point.x, point.z);
            }

            chara.Revive(point, true);

            // a player who dies here pays what one playing alone pays, by the same rules: after day 90, unless
            // its will spares it, a share of its gold falls where it stands. Once, here; the experience of its
            // attributes is its own game's to take (below). Council decision of 2026-10-04, see MODLOG
            if (chara.IsRemotePlayer && !IsProtected(chara) &&
                !(chara.things.Find("letter_will") is not null && rnd(10) == 0)) {
                Penalty = true;
                if (chara.GetCurrency() is var gold and > 0) {
                    var lost = Math.Max(1, gold / 3 + rnd(gold / 3 + 1));
                    using var simulate = Simulate();
                    using (MsgRelayContext.RedirectTo(chara)) {
                        Msg.Say("panaltyMoney", chara, Lang._currency(lost));
                    }

                    chara.ModCurrency(-lost);
                    _zone.AddCard(ThingGen.CreateCurrency(lost), chara.pos);
                }
            }

            using (Simulate()) {
                chara.MakeGrave(LastWords);
            }

            // Zone.AddCard during Revive
            if (chara.IsActiveRemoteChara) {
                chara.SetAI(GoalRemote.Default);
            }

            Pos = point;
            host.Delta.AddRemote(this);
        } else {
            // (reaching a player away on a map of its own: only its own revive, the others are about the host map)
            if (NetSession.Instance.IsAway && !chara.IsPC) {
                return;
            }

            // duplicate
            if (chara.isDead) {
                // (a point of the host's map means nothing on the map a player away holds)
                if (Pos is { IsInActiveMapBounds: true } pos && !NetSession.Instance.IsAway) {
                    if (!chara.pos.IsValid) {
                        chara.pos.Set(pos.X, pos.Z);
                    }

                    chara.Revive(pos, true);
                } else if (chara.IsPC && chara.pos is { IsValid: true, IsInBounds: true } fell) {
                    // GitHub issue 70: revived by a player on another map (no point of this map in the delta). The
                    // game puts a character back on the map only with a point: where it fell
                    chara.Revive(fell.Copy(), true);
                } else {
                    chara.Revive(msg: true);
                }
            }

            // recursion
            if (chara.IsPC) {
                player.deathDialog = false;
                // (permadeath: its next death is told again, see PermadeathPatch)
                chara.SetBool(PlayerRevive.DeadFlag, false);

                // the gold was taken where the map is simulated; the experience of its attributes is its own
                if (Penalty) {
                    using var _ = Simulate();
                    foreach (var element in chara.elements.dict.Values.ToArray()) {
                        if (rnd(5) == 0 && element.IsMainAttribute) {
                            chara.elements.ModExp(element.id, -500f);
                        }
                    }
                }

                // then to where it picked on the death screen, as the host goes
                (net as ElinNetClient)?.OnRevived();
            }
        }

        EmpLog.Debug("Revive chara {Uid} at {@Pos}",
            chara.uid, Pos);

        // add back to party
        if (chara is { c_wasInPcParty: true, IsPCParty: false }) {
            pc.party.AddMemeber(chara);
        }
    }
}