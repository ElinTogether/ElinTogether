using System;
using System.Collections.Generic;
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

        if (NetSession.Instance.Connection is ElinNetClient client) {
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

                client.Delta.AddRemote(new InvPlaceAbilityDelta {
                    Layout = layout,
                });
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

        // client pending uid
        if (connection.IsClient && (PendingUid.IsPending(t.uid) || PendingUid.IsPending(__instance.uid))) {
            return true;
        }

        if (!CardCache.Contains(__instance) && !CardCache.TryAdopt(__instance)) {
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