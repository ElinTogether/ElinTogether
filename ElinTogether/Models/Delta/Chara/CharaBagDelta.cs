using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Helper.Extensions;
using ElinTogether.Net;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

/// <summary>
///     One player character in full: what it carries, wears and holds, bags in bags included. <br />
///     Without <see cref="Things" />: a game standing on the map asks the game that keeps it, its copy of that bag
///     differs (<see cref="NetDesync" />). With: the keeper's copy, sent to everyone in the stream of its deltas
///     (filled when the list goes out: what was sent before is in it, what comes after is not). The keeper's copy
///     is the reference: it is the one saved, and the one a player gets back when it connects again
/// </summary>
[MessagePackObject]
public class CharaBagDelta : ElinDelta
{
    // one answer per character, whoever asked: it goes to everyone, so it serves every game that asked in the
    // same comparison, and no player can make the host send a whole bag more often than that
    private const float AnswerGap = 30f;

    private static readonly Dictionary<int, float> _answered = [];

    [Key(0)]
    public int Uid { get; set; }

    [Key(1)]
    public LZ4Bytes? Things { get; set; } = null;

    /// <summary>
    ///     <see cref="NetDesync.Bag" /> of the keeper's copy: the one the asking game compared with, then the one of
    ///     <see cref="Things" />
    /// </summary>
    [Key(2)]
    public int Mix { get; set; }

    /// <summary>
    ///     Uid of the thing in its hands, 0 for none
    /// </summary>
    [Key(3)]
    public int Held { get; set; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Things is null) {
            Answer(net);
            return;
        }

        if (net.IsHost) {
            EmpLog.Warning("Refusing {DeltaType} from peer {PeerIndex}, uid {Uid}",
                nameof(CharaBagDelta), OriginPeer, Uid);
            return;
        }

        Repair(net);
    }

    private void Answer(ElinNetBase net)
    {
        // only a player standing here asks, and only for a player standing here
        if (net is not ElinNetHost host || !host.ActiveRemoteCharas.ContainsKey(OriginPeer) || Player(Uid) is null) {
            return;
        }

        var now = Time.unscaledTime;
        if (_answered.TryGetValue(Uid, out var next) && now < next) {
            return;
        }

        _answered[Uid] = now + AnswerGap;

        EmpLog.Information("Player {PeerIndex} asks for the bag of {Uid} again, its copy differs: sent to everyone",
            OriginPeer, Uid);

        net.Delta.AddRemote(new CharaBagDelta {
            Uid = Uid,
        });
    }

    protected override bool OnRefresh()
    {
        if (Things is not null) {
            return true;
        }

        if (Player(Uid) is not { } chara) {
            return false;
        }

        Things = LZ4Bytes.Create(chara.things.Where(t => !NetDesync.Skipped(t)).ToList());
        Mix = NetDesync.Bag(chara);
        Held = chara.held is Thing held && !NetDesync.Skipped(held) ? held.uid : 0;
        return true;
    }

    private static Chara? Player(int uid)
    {
        return NetSession.Instance.CurrentPlayers.Exists(p => p?.CharaUid == uid)
            ? _map?.charas.Find(c => c.uid == uid && !c.isDead)
            : null;
    }

    /// <summary>
    ///     Bring our copy of that bag to the keeper's, card by card: a card both have stays the object it is here
    ///     (windows, the hotbar and tasks point at it), only its place, amount and slot change. Cards waiting for
    ///     their number and ability tokens are ours alone and stay
    /// </summary>
    private void Repair(ElinNetBase net)
    {
        if (Player(Uid) is not { } chara) {
            return;
        }

        var before = NetDesync.Bag(chara);
        if (before == Mix) {
            return;
        }

        if (NetDesync.BagRepairRefused(chara, before, Mix) is { } why) {
            EmpLog.Information("Bag of {Uid} differs from its keeper's and stays as it is: {Why}",
                Uid, why);
            return;
        }

        // what the keeper has, holders before what they hold, every card taken out of its holder
        var want = new List<(Thing Thing, int Holder, int Slot, int InvX, int InvY)>();
        void Walk(Thing thing, int holder)
        {
            if (NetDesync.Skipped(thing)) {
                return;
            }

            want.Add((thing, holder, holder == chara.uid ? thing.c_equippedSlot : 0, thing.invX, thing.invY));
            thing.c_equippedSlot = 0;

            var held = thing.things.ToList();
            thing.things.Clear();
            foreach (var inner in held) {
                inner.parent = null;
                Walk(inner, thing.uid);
            }
        }

        foreach (var thing in Things!.Decompress<List<Thing>>()) {
            Walk(thing, chara.uid);
        }

        var slotOf = new Dictionary<int, int>();
        foreach (var entry in want) {
            slotOf[entry.Thing.uid] = entry.Slot;
        }

        var have = new Dictionary<int, Thing>();
        foreach (var thing in chara.things.Flatten()) {
            if (!NetDesync.Skipped(thing)) {
                have[thing.uid] = thing;
            }
        }

        int added = 0, removed = 0, moved = 0, counted = 0, worn = 0;
        var slots = chara.body.slots;

        // off: what is not worn there, or not in that slot
        foreach (var slot in slots) {
            if (slot.thing is { } on && !NetDesync.Skipped(on) && slotOf.GetValueOrDefault(on.uid) != slot.index + 1) {
                chara.body.Unequip(slot);
                worn++;
            }
        }

        var placed = new Dictionary<int, Thing>();
        foreach (var (theirs, holderUid, _, invX, invY) in want) {
            Card? holder = holderUid == chara.uid ? chara : placed.GetValueOrDefault(holderUid);
            if (holder is null) {
                continue;
            }

            // the card we know under that number, wherever it lies here (another bag, the floor), else theirs
            var mine = have.GetValueOrDefault(theirs.uid) ?? CardCache.Find(theirs.uid) as Thing;
            if (!chara.IsPC && !NetDesync.RepairOwnBag && mine?.GetRootCard() == pc) {
                // we carry it ourselves: our own bag is not touched from here
                continue;
            }

            if (mine is null || mine.isDestroyed) {
                mine = theirs;
                game.cards.uidNext = PendingUid.Above(game.cards.uidNext, theirs.uid);
                added++;
            } else if (mine.parent != holder) {
                moved++;
            }

            CardCache.Set(mine);
            placed[mine.uid] = mine;

            if (mine.parent != holder && holder.AddThing(mine, false, invX, invY) == mine) {
                if (invX >= 0) {
                    mine.invX = invX;
                }

                if (invY >= 0) {
                    mine.invY = invY;
                }
            }

            // curse, enchant and coating are the keeper's too
            if (mine != theirs && ((int)mine.blessedState, mine.encLV, mine.isAcidproof) !=
                ((int)theirs.blessedState, theirs.encLV, theirs.isAcidproof)) {
                CardItemStateDelta.Land(mine, (int)theirs.blessedState, theirs.encLV, theirs.isAcidproof);
            }

            if (mine != theirs && mine.Num != theirs.Num) {
                // the way a client is given an amount, see CardModNumEvent
                new CardModNumDelta {
                    Card = mine,
                    Num = theirs.Num,
                }.Apply(net);
                counted++;
            }
        }

        // out: what the keeper does not have there. A card waiting for its number goes on to the keeper by itself
        foreach (var thing in have.Values) {
            if (placed.ContainsKey(thing.uid) || thing.isDestroyed || thing.GetRootCard() != chara) {
                continue;
            }

            foreach (var ours in thing.things.Flatten().Where(t => !t.isDestroyed && NetDesync.Skipped(t)).ToList()) {
                chara.AddThing(ours, false);
            }

            thing.parent?.RemoveCard(thing);
            CardCache.KeepAlive(thing);
            removed++;
        }

        // on
        foreach (var (theirs, holderUid, slotIndex, _, _) in want) {
            if (holderUid != chara.uid || !placed.TryGetValue(theirs.uid, out var mine) || mine.parent != chara) {
                continue;
            }

            var slot = slotIndex > 0 && slotIndex <= slots.Count ? slots[slotIndex - 1] : null;
            if (slot is null || slot.elementId != mine.category.slot) {
                // flagged without a slot holding it
                if (!slots.Exists(s => s.thing == mine)) {
                    mine.c_equippedSlot = 0;
                }

                continue;
            }

            if (slot.thing != mine) {
                // vanilla Equip trusts the flag, see CharaEquipDelta
                mine.c_equippedSlot = 0;
                chara.body.Equip(mine, slot, false);
                worn++;
            }
        }

        chara.SetDirtyWeight();

        if (chara.IsPC) {
            LayerInventory.SetDirtyAll();
            WidgetCurrentTool.dirty = true;
        } else if (chara.ai is not GoalRemote { child.status: AIAct.Status.Running }) {
            // as CharaSwitchHeldDelta shows what a player holds
            if (Held == 0) {
                if (chara.held is not null) {
                    chara.PickHeld();
                }
            } else if (placed.TryGetValue(Held, out var inHand) && chara.held != inHand && inHand.GetRootCard() == chara) {
                chara.HoldCard(inHand);
            }
        }

        EmpLog.Warning("Bag of {Uid} brought to its keeper's copy (ours {Own}): {Added} added, {Removed} removed, {Moved} moved, {Counted} amounts, {Worn} worn or taken off, same now {Same}",
            Uid, chara.IsPC, added, removed, moved, counted, worn, NetDesync.Bag(chara) == Mix);
    }
}
