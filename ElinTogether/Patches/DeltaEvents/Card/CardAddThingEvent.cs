using System;
using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.AddThing), typeof(Thing), typeof(bool), typeof(int), typeof(int))]
internal static class CardAddThingEvent
{
    private static readonly List<Thing> _pendingAbilityFakeCards = [];

    internal static bool AbilityLayoutDirty;

    internal static void FlushPendingAbilityFakeCard()
    {
        if (_pendingAbilityFakeCards.Count == 0 && !AbilityLayoutDirty) {
            return;
        }

        // (issues 83, 85, 86: away on a map it holds, the player's game is that map's host: the layout of its spells
        // never reached the world, which made its tokens again from the old one when it came back)
        var main = NetSession.Instance.Connection as ElinNetClient ??
                   (NetSession.Instance is { IsAway: true, Transport: ElinNetClient away } ? away : null);
        if (main is not null) {
            foreach (var ab in _pendingAbilityFakeCards) {
                if (ab.isDestroyed || string.IsNullOrEmpty(ab.c_idAbility) || ab.GetRootCard() != EClass.pc) {
                    continue;
                }

                CardCache.UndoDestroy(ab);
                AbilityLayoutDirty = true;
            }

            if (AbilityLayoutDirty) {
                var layout = CollectLayout();
                EmpLog.Debug("Reporting ability layout, {LayoutCount} entries", layout.Count);

                var report = new InvPlaceAbilityDelta {
                    Layout = layout,
                };
                if (NetSession.Instance.Connection is ElinNetClient client) {
                    client.Delta.AddRemote(report);
                } else {
                    main.SendWhileAway(report);
                }
            }
        }

        AbilityLayoutDirty = false;
        _pendingAbilityFakeCards.Clear();
    }

    internal static List<InvPlaceAbilityDelta.AbilityTokenSlot> CollectLayout()
    {
        var layout = new List<InvPlaceAbilityDelta.AbilityTokenSlot>();
        foreach (var token in EClass.pc.things) {
            if (token is { trait: TraitAbility, isDestroyed: false } && !string.IsNullOrEmpty(token.c_idAbility)) {
                layout.Add(new() {
                    Alias = token.c_idAbility,
                    InvX = token.invX,
                    InvY = token.invY,
                });
            }
        }

        return layout;
    }

    [HarmonyPrefix]
    internal static bool OnCardAddThing(Card __instance, Thing t, bool tryStack, int destInvX, int destInvY)
    {
        if (ShippingHelper.FillingMirror) {
            return true;
        }

        // alone away, an open box of the world shows what the host holds, see ShippingHelper.MirrorKey
        var hostUid = t.GetInt(ShippingHelper.MirrorKey);
        if (NetSession.Instance.Transport is ElinNetClient { MirrorsWorldBoxes: true } mirror) {
            var inBox = ShippingHelper.WorldBoxIndex(__instance.GetRootCard());
            if (hostUid != 0) {
                if (inBox >= 0) {
                    // moved around in the picture
                    return true;
                }

                // out of the box: the picture goes, the host gives the real one (or what is left of it)
                mirror.AskWorldBox(t.GetInt(ShippingHelper.MirrorBoxKey) - 1, hostUid, t.Num);
                t.Destroy();
                return false;
            }

            // into a bag that lies in the box: that bag is a picture, the thing goes to the box itself
            if (inBox >= 0 && ShippingHelper.WorldBoxIndex(__instance) < 0 && mirror.ForwardShippingDeposit(t, 0, inBox)) {
                return false;
            }
        } else if (hostUid != 0) {
            // a picture that outlived its window (a zone session started meanwhile): it is nothing
            t.Destroy();
            return false;
        }

        // shipping box: tagged with the player shipping it; travelling, the goods go to the real host
        var shipper = 0;
        if (!ElinDelta.IsRemoteStateLanding && ShippingHelper.IsShippingBox(__instance)) {
            shipper = ShippingHelper.Enabled ? ShippingHelper.ShipperOverride ?? EClass.pc.uid : 0;
            if (ShippingHelper.ShipperOverride is null && NetSession.Instance.Connection is not ElinNetClient &&
                NetSession.Instance.Transport is ElinNetClient away && away.ForwardShippingDeposit(t, shipper)) {
                return false;
            }

            t.SetInt(ShippingHelper.ShipperKey, shipper);
        }

        // the bank and the delivery box: travelling, they are empty copies here, what goes in is sent to the
        // real ones instead of being lost with the copy
        // (a guest of a zone session too: straight to the world host, not through the holder's empty copy, issue 51)
        if (!ElinDelta.IsRemoteStateLanding && ShippingHelper.OtherWorldBox(__instance) is var box and not 0 &&
            NetSession.Instance.Transport is ElinNetClient { MirrorsWorldBoxes: true } travelling &&
            travelling.ForwardShippingDeposit(t, 0, box)) {
            return false;
        }

        // (issues 83, 85, 86: alone on a map of its own there is no connection; a spell laid on the belt there is told
        // to the world all the same, see FlushPendingAbilityFakeCard)
        if (t.trait is TraitAbility && __instance.IsPC && NetSession.Instance is { IsAway: true, Connection: null }) {
            AbilityLayoutDirty = true;
        }

        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsRemoteStateLanding) {
            if (RemoteCraft.ProductReceiver is not null) {
                EmpLog.Warning("Suppressed add-thing of {Uid} during remote craft, IsApplying guard hit",
                    t.uid);
            }

            return true;
        }

        if (__instance.GetBool("emp_creating")) {
            return true;
        }

        // ability fake card
        if (t.trait is TraitAbility) {
            if (connection.IsClient && __instance.IsPC && PendingUid.IsPending(t.uid)) {
                _pendingAbilityFakeCards.Add(t);
            }

            return true;
        }

        // the split menu: the part split off goes back beside the stack it came from, and the stack itself is carried
        // away. The host makes that split, see CardSplitStackDelta
        if (connection.IsClient && !tryStack && SplitMenuPatch.IsSplitting && PendingUid.IsPending(t.uid) && PendingSplit.Resolve(t.uid) is var origin and > 0 &&
            CardCache.Find(origin) is Thing { parent: var at } source && at == __instance && !PendingUid.IsPending(__instance.uid)) {
            connection.Delta.AddRemote(new CardSplitStackDelta {
                Source = source,
                Parent = __instance,
                Num = t.Num,
                Pending = t.uid,
            });
            return true;
        }

        // client pending uid
        if (connection.IsClient && (PendingUid.IsPending(t.uid) || PendingUid.IsPending(__instance.uid))) {
            return true;
        }

        if (!CardCache.Contains(__instance) && !CardCache.TryAdopt(__instance)) {
            if (connection.IsHost) {
                if (!ZoneActivateEvent.IsHappening) {
                    EmpLog.Verbose("Skipping add-thing sync of {Uid} into uncached parent {ParentUid}",
                        t.uid, __instance.uid);
                }
                return true;
            }

            EmpLog.Warning("Suppressed add-thing of {Uid} into uncached parent {ParentUid}",
                t.uid, __instance.uid);
            return false;
        }

        connection.Delta.AddRemote(new CardAddThingDelta {
            Thing = t,
            Parent = __instance,
            TryStack = tryStack,
            DestInvX = destInvX,
            DestInvY = destInvY,
            Shipper = shipper,
        });

        return true;
    }

    extension(Card card)
    {
        [HarmonyReversePatch(HarmonyReversePatchType.Snapshot)]
        internal Thing Stub_AddThing(Thing thing, bool tryStack, int destInvX, int destInvY)
        {
            throw new NotImplementedException("Card.AddThing");
        }
    }
}