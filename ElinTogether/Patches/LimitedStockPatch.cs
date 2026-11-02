using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     What a shop sells once per world ("limited stock": skill books, recipes, the weapons of a few merchants) is
///     sold once per PLAYER: the game makes one such item for the world and the first player who bought it left
///     nothing for the others. <br />
///     The item in the merchant's chest is the ledger: it carries how many each player bought already. When
///     some leave the chest for a player, the game that keeps the shop lays the whole stack in the chest again
///     and notes them for that player; nobody can buy more than the stack holds, in all. At each restock the game is let to make every limited item again (its
///     own memory of them, <c>player.noRestocks</c>, is set aside for the time of the restock), and what the chest
///     already holds is kept instead of the new one: a world played before this finds its limited items back at the
///     next restock, for everyone. Not in a world nobody else plays in: the game's own rule
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class LimitedStockPatch
{
    private const string BuyersKey = "emp_limited_buyers";

    // the chest the last whole stack was made for
    private static Thing? _chest;

    // sizes of the limited stacks of the chest being restocked, by card number
    private static readonly Dictionary<int, int> _sizes = [];

    // CINT.noRestock: "limited stock"
    private const int Limited = 101;

    // (the host's checkbox: its own setting when no session is open, else the rule it told the players)
    private static bool Shared => (NetSession.Instance.Transport is not null || ElinNetHost.IsSharedWorld) &&
                                  (NetSession.Instance.Transport is null
                                      ? EmpConfig.Server.OncePerPlayer.Value
                                      : NetSession.Instance.Rules.OncePerPlayer);

    // "uid:how many,uid:how many"
    private static Dictionary<string, int> Buyers(Thing thing)
    {
        var buyers = new Dictionary<string, int>();
        foreach (var entry in (thing.GetStr(BuyersKey) ?? "").Split(',')) {
            var parts = entry.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var count)) {
                buyers[parts[0]] = buyers.GetValueOrDefault(parts[0]) + count;
            }
        }

        return buyers;
    }

    private static void Note(Thing thing, Dictionary<string, int> buyers)
    {
        thing.SetStr(BuyersKey, buyers.Count == 0 ? null : string.Join(",", buyers.Select(b => b.Key + ":" + b.Value)));
    }

    private static bool Same(Thing a, Thing b)
    {
        return a.id == b.id && a.idSkin == b.idSkin && a.trait.IdNoRestock == b.trait.IdNoRestock;
    }

    /// <summary>
    ///     How many of these that player may still take, at most <paramref name="num" />: all of them for anything
    ///     but a limited item of a shop, for which it is the stack less what that player bought of it already
    /// </summary>
    internal static int Left(Thing thing, Chara player, int num)
    {
        if (thing is not { parent: Thing { id: "chest_merchant" } } || thing.GetInt(Limited) == 0) {
            return num;
        }

        return System.Math.Min(num, thing.Num - Buyers(thing).GetValueOrDefault(player.uid.ToString()));
    }

    /// <summary>
    ///     The player bought all of it already: a beep and a line, as the game does for what cannot be bought.
    ///     Bought a part of it: no more than what is left for that player. A purchase in the game that keeps the
    ///     shop is noted here; a client's is noted by that game when it hands the item (ThingRequest)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(InvOwner.Transaction), nameof(InvOwner.Transaction.Process))]
    internal static bool OnBuy(InvOwner.Transaction __instance, out (Thing? Left, Thing Whole)? __state)
    {
        __state = null;
        if (__instance.thing is not { } thing) {
            return true;
        }

        var left = Left(thing, EClass.pc, __instance.num);
        if (left <= 0) {
            SE.Beep();
            Msg.Say("emp_ui_limited_bought".lang());
            return false;
        }

        __instance.num = left;
        if (NetSession.Instance.Connection is not ElinNetClient && Whole(thing, left, EClass.pc) is { } whole) {
            __state = (left < thing.Num ? thing : null, whole);
        }

        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InvOwner.Transaction), nameof(InvOwner.Transaction.Process))]
    internal static void OnBought(bool __result, (Thing? Left, Thing Whole)? __state)
    {
        if (__result && __state is var (left, whole)) {
            Lay(left, whole);
        }
    }

    /// <summary>
    ///     In the game that keeps the shop, BEFORE some of a limited item leave the merchant's chest for that
    ///     player: the whole stack as the chest must hold it afterwards, with these noted for that player. Null
    ///     for anything else. A new card, written before it is laid: who bought what travels with the card to every
    ///     game, a note changed on a card already there would stay in this one
    /// </summary>
    internal static Thing? Whole(Thing thing, int num, Chara buyer)
    {
        if (!Shared || thing is not { parent: Thing { id: "chest_merchant" } } || thing.GetInt(Limited) == 0) {
            return null;
        }

        _chest = thing.parent as Thing;

        var buyers = Buyers(thing);
        buyers[buyer.uid.ToString()] = buyers.GetValueOrDefault(buyer.uid.ToString()) + num;

        var whole = thing.Duplicate(thing.Num);
        whole.SetInt(Limited, 1);
        Note(whole, buyers);

        EmpLog.Information("Limited item {Id} x{Num} taken by player {Uid}: laid in the shop again for the others",
            thing.id, num, buyer.uid);
        return whole;
    }

    /// <summary>
    ///     AFTER they left: what stayed of the stack gives way to the whole one
    /// </summary>
    // ponytail: an item bought then put back in the same shop window lies beside the whole stack until the next
    // restock (one more copy on sale). Take it back into the stack if players do that
    internal static void Lay(Thing? left, Thing whole)
    {
        // (null: the whole stack went, and it is the very card the buyer holds, in its bag or still in its hand)
        if (left is { isDestroyed: false, parent: Thing { id: "chest_merchant" } }) {
            left.Destroy();
        }

        _chest?.AddThing(whole, false);
    }

    /// <summary>
    ///     A restock, in the game that keeps the shop: the game forgets, for that long, which limited items this
    ///     merchant already offered, and makes them all again
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Trait), nameof(Trait.OnBarter))]
    internal static void OnRestock(Trait __instance, out Dictionary<string, HashSet<string>>? __state)
    {
        __state = null;
        _sizes.Clear();
        if (!Shared || !NetSession.Instance.IsHost || EClass.player?.noRestocks is not { } all ||
            __instance.owner is not { } owner) {
            return;
        }

        // (a limited item made again piles up on the one the chest holds: its size before, to tell)
        foreach (var held in owner.things.Find("chest_merchant")?.things.Where(t => t.GetInt(Limited) != 0) ?? []) {
            _sizes[held.uid] = held.Num;
        }

        // (the game notes them under the merchant, and under the merchant and a skin)
        var keys = all.Keys.Where(k => k == owner.id || k.StartsWith(owner.id + "_skin")).ToList();
        if (keys.Count == 0) {
            return;
        }

        __state = keys.ToDictionary(k => k, k => all[k]);
        foreach (var key in keys) {
            all.Remove(key);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Trait), nameof(Trait.OnBarter))]
    internal static void OnRestockEnd(Trait __instance, Dictionary<string, HashSet<string>>? __state)
    {
        if (__state is not null && EClass.player?.noRestocks is { } all) {
            foreach (var (key, offered) in __state) {
                if (all.TryGetValue(key, out var now)) {
                    now.UnionWith(offered);
                } else {
                    all[key] = offered;
                }
            }
        }

        if (!Shared || !NetSession.Instance.IsHost ||
            __instance.owner?.things.Find("chest_merchant") is not { } chest) {
            return;
        }

        // made again while the chest still held it. Piled up on it: the stack is the larger of the two, not their sum
        foreach (var held in chest.things.Where(t => t.GetInt(Limited) != 0).ToList()) {
            if (_sizes.TryGetValue(held.uid, out var before) && held.Num > before) {
                held.ModNum(System.Math.Max(before, held.Num - before) - held.Num);
            }
        }

        _sizes.Clear();

        // Laid beside it: the one of the chest stays, it knows who bought what, and is made whole again (a stack
        // partly sold before this, or a shop window that never closed)
        var twice = chest.things
            .Where(t => t.GetInt(Limited) != 0)
            .GroupBy(t => (t.id, t.idSkin, t.trait.IdNoRestock))
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var group in twice) {
            var keep = group.FirstOrDefault(t => Buyers(t).Count > 0) ?? group.First();
            var whole = group.Max(t => t.Num);
            foreach (var extra in group.Where(t => t != keep).ToList()) {
                extra.Destroy();
            }

            if (keep.Num < whole) {
                keep.ModNum(whole - keep.Num);
            }
        }
    }
}
