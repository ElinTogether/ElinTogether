using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;

namespace ElinTogether.Net;

/// <summary>
///     Shipping per player, see ElinNetHostShipping
/// </summary>
internal partial class ElinNetClient
{
    /// <summary>
    ///     Travelling alone (or hosting a zone): the shipping box here is a copy, the goods go to the real host
    /// </summary>
    /// <param name="box">see <see cref="ShippingDeposit.Box" />: the bank and the delivery box are the world's too</param>
    internal bool ForwardShippingDeposit(Thing thing, int shipperUid, int box = 0)
    {
        if (!Session.IsAway || IsZoneSession) {
            return false;
        }

        Host.Send(new ShippingDeposit {
            Thing = LZ4Bytes.Create(thing),
            Shipper = shipperUid,
            Box = box,
        });

        EmpLog.Debug("Sent {CardId} x{CardNum} to the shipping box of the host for chara {Uid}",
            thing.id, thing.Num, shipperUid);

        // it is in the box of the host now
        thing.Destroy();

        // and must not be in the bag the host keeps for this player: quitting before the next checkpoint
        // would leave it sold and kept
        _nextCheckpoint = 0;

        AskWorldBoxSoon(box);
        return true;
    }

    private readonly HashSet<int> _askingBoxes = [];

    /// <summary>
    ///     After deposits: ask once for what the box holds now, for the window that is open (many deposits, one answer)
    /// </summary>
    private void AskWorldBoxSoon(int box)
    {
        if (!_askingBoxes.Add(box)) {
            return;
        }

        this.StartDeferredCoroutine(() => {
            _askingBoxes.Remove(box);
            if (ShippingHelper.WorldBox(box) is { } container && LayerInventory.IsOpen(container)) {
                AskWorldBox(box);
            }
        }, 0.3f);
    }

    /// <summary>
    ///     The boxes shared by the whole world (shipping, deliveries, bank) are the host's: empty in the copy
    ///     of a travelling player, taking from a copy would duplicate what the host still has
    /// </summary>
    internal static void EmptyWorldContainers()
    {
        EmptyWorldContainer(game.cards.container_shipping);
        EmptyWorldContainer(game.cards.container_deliver);
        EmptyWorldContainer(game.cards.container_deposit);
    }

    /// <summary>
    ///     One box only: closing a window must not empty the pictures of another window still open
    /// </summary>
    internal static void EmptyWorldContainer(Thing? container)
    {
        if (container is null) {
            return;
        }

        foreach (var thing in container.things.ToArray()) {
            thing.Destroy();
        }
    }

    /// <summary>
    ///     Alone away (the game runs as single player): the boxes of the world can show what the host holds
    /// </summary>
    // away, alone or in a zone session (its guest, or holding it for guests: GitHub issue 51, the bank window stayed
    // empty and a guest's gold went into an empty copy): this link is the world host's in every case
    internal bool MirrorsWorldBoxes => Session.IsAway && !IsZoneSession && core.IsGameStarted;

    /// <summary>
    ///     Ask the host what a box of the world holds, and with takeNum for that many of its stack takeUid
    /// </summary>
    internal bool AskWorldBox(int box, int takeUid = 0, int takeNum = 0)
    {
        if (!MirrorsWorldBoxes || box < 0) {
            return false;
        }

        Host.Send(new ShippingDeposit {
            Thing = LZ4Bytes.Empty,
            Shipper = 0,
            Box = box,
            Ask = true,
            TakeUid = takeUid,
            TakeNum = takeNum,
        });

        return true;
    }

    /// <summary>
    ///     Net event: what a box of the world holds, and what the host took out of it for us
    /// </summary>
    private void OnWorldBox(ShippingPayout content)
    {
        var mirrors = MirrorsWorldBoxes && pc is not null;

        if (content.Taken is { } taken) {
            if (!mirrors) {
                // we moved on since asking: nothing was taken here, the host puts it back by itself
                return;
            }

            var thing = taken.Decompress<Thing>();
            // its uid comes from another world
            foreach (var card in thing.things.Flatten().Prepend(thing)) {
                game.cards.AssignUID(card);
            }

            if (Session.Connection is not null) {
                // a guest of a zone session (issue 51): its pick of a card the holder never saw was dropped, the gold
                // lost. Gold is paid in as gold (told to the holder as any gain), a thing is made known first
                if (thing.id == "money") {
                    EClass.pc.ModCurrency(thing.Num);
                    thing.Destroy();
                } else {
                    foreach (var card in thing.things.Flatten().Prepend(thing)) {
                        CardCache.Add(card);
                    }

                    EClass.pc.Pick(thing, false);
                }
            } else {
                EClass.pc.Pick(thing, false);
            }

            // the host keeps its copy aside until the bag it keeps for us carries this mark: it goes up
            // with the checkpoint asked for now, in the same character as the thing
            EClass.pc.SetInt(ShippingHelper.TookKey, content.TakenToken);
            _nextCheckpoint = 0;

            EmpLog.Debug("Took {CardId} x{CardNum} out of world box {Box}", thing.id, thing.Num, content.Box);
        } else if (content.Asked > 0 && mirrors) {
            EmpPop.Pop("emp_ui_thing_gone".lang());
        }

        if (!mirrors || content.BoxThings is null || ShippingHelper.WorldBox(content.Box) is not { } container) {
            return;
        }

        foreach (var old in container.things.ToArray()) {
            old.Destroy();
        }

        // only while its window is open: nothing else of this game may count on a picture (bills, month end)
        if (!LayerInventory.IsOpen(container)) {
            return;
        }

        ShippingHelper.FillingMirror = true;
        try {
            foreach (var thing in content.BoxThings.Decompress<List<Thing>>()) {
                foreach (var card in thing.things.Flatten().Prepend(thing)) {
                    card.SetInt(ShippingHelper.MirrorKey, card.uid);
                    card.SetInt(ShippingHelper.MirrorBoxKey, content.Box + 1);
                }

                container.AddThing(thing, false);
            }
        } finally {
            ShippingHelper.FillingMirror = false;
        }

        LayerInventory.SetDirtyAll();
    }

    /// <summary>
    ///     Net event: the morning sale of our goods
    /// </summary>
    private void OnShippingPayout(ShippingPayout payout)
    {
        if (IsZoneSession) {
            return;
        }

        if (payout.BoxThings is not null || payout.Taken is not null) {
            OnWorldBox(payout);
            return;
        }

        // right after arriving on the host map the zone may still be loading
        this.StartDeferredCoroutine(() => ApplyShippingPayout(payout),
            () => core.IsGameStarted && game.activeZone?.map is not null && pc is not null);
    }

    private void ApplyShippingPayout(ShippingPayout payout)
    {
        var result = new ShippingResult {
            ints = [..payout.Ints],
        };
        foreach (var strs in payout.ItemStrs) {
            result.items.Add(new() {
                _strs = strs,
            });
        }

        player.shippingResults.Add(result);
        while (player.shippingResults.Count > 10) {
            player.shippingResults.RemoveAt(0);
        }

        player.stats.shipNum = (int)System.Math.Min(payout.ShipNum, int.MaxValue);
        player.stats.shipMoney = payout.ShipMoney;

        if (!Session.IsAway && payout.BranchLv > 0 && (game.spatials.Find(result.uidZone) ?? pc.homeZone)?.branch is { } branch) {
            branch.lv = payout.BranchLv;
            branch.exp = payout.BranchExp;
        }

        // our currency is ours to change, wherever we are (on the host map the host hears of it,
        // see CardModCurrencyEvent)
        AddCurrencyLocal("money", payout.Money);
        AddCurrencyLocal("money2", payout.Bonus);

        // the host forgets what it owed as soon as it pays: the money has to be in the bag it keeps for us
        _nextCheckpoint = 0;

        if (result.items.Count > 0) {
            player.showShippingResult = core.config.game.showShippingResult;
        }

        EmpLog.Information("Shipping paid {ShipIncome} for {ShipItemCount} goods, received here {ShipLocal}",
            result.GetIncome(), result.items.Count, payout.Money);
    }

    private static void AddCurrencyLocal(string id, long amount)
    {
        while (amount > 0) {
            var part = (int)System.Math.Min(amount, int.MaxValue);
            amount -= part;
            pc.ModCurrency(part, id);
        }
    }
}
