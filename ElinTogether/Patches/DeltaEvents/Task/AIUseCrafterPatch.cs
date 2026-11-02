using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Models.AI;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

// TODO 1: ask Redgeioz to rework this
// TODO 2: ask Redgeioz to rework this
// TODO 3: ask Redgeioz to rework this
[HarmonyPatch]
internal static class AIUseCrafterPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AI_UseCrafter), nameof(AI_UseCrafter.Run))]
    internal static bool OnCraftRun(AI_UseCrafter __instance, ref IEnumerable<AIAct.Status> __result)
    {
        if (!RemoteCraft.TryGet(__instance, out var args)) {
            return true;
        }

        __result = NetSession.Instance.IsClient
            ? RunClient(__instance, args)
            : RunRemote(__instance, args);
        return false;
    }

    private static IEnumerable<AIAct.Status> RunClient(AI_UseCrafter act, AIUseCrafterArgs args)
    {
        var crafter = act.crafter;
        var round = 0;

        act.owner.LookAt(crafter.owner.pos);

        while (true) {
            round++;
            EmpLog.Debug("Client craft round {CraftRound} of chara {OwnerUid}, waiting for host completion",
                round, act.owner.uid);
            // use held for clients
            var cost = crafter.GetCostSp(act);
            var progress = new HeldProgress {
                onProgressComplete = () => {
                    var e = act.owner.elements.GetOrCreateElement(crafter.IDReqEle(act.recipe?.source));
                    for (var i = 0; i < act.num; i++) {
                        act.owner.RemoveCondition<ConInvulnerable>();
                        EClass.player.invlunerable = false;
                        act.owner.elements.ModExp(e.id, cost * 12f * (100f + args.Duration * 2f) / 100f);
                        act.owner.stamina.Mod(-cost);
                        if (act.owner.isDead) {
                            break;
                        }
                    }
                },
            }.SetDuration(args.Duration, 5);
            yield return act.Do(progress);

            if (progress.status == AIAct.Status.Fail || crafter.CloseOnComplete) {
                yield return act.Cancel();
            }

            if (!crafter.IsConsumeIng) {
                if (act.layer) {
                    act.layer.ClearButtons();
                }

                break;
            }

            if (!args.Repeat) {
                break;
            }
        }

        yield return act.Success();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AI_UseCrafter), nameof(AI_UseCrafter.OnEnd))]
    internal static bool OnEnd(AI_UseCrafter __instance)
    {
        if (!RemoteCraft.TryGet(__instance, out var args) || NetSession.Instance.IsClient) {
            __instance.ings ??= [];
            return true;
        }

        var crafter = __instance.crafter;

        // is applying
        using var simulate = ElinDelta.Simulate();

        var ings = __instance.ings ?? [];
        for (var i = 0; i < ings.Count; i++) {
            var ing = ings[i];
            if (ing is null || ing.isDestroyed || !ing.ExistsOnMap || __instance.owner is not { } owner) {
                continue;
            }

            ing.isHidden = false;
            // split(1) == self
            if ((i < args.Targets.Count ? args.Targets[i].Find() : null) is Thing { isDestroyed: false } origin
                && origin != ing) {
                EmpLog.Debug("Remote craft returning ing {Uid} num {CardNum} to origin {TargetUid}",
                    ing.uid, ing.Num, origin.uid);
                origin.ModNum(ing.Num);
                ing.Destroy();
            } else {
                owner.AddThing(ing);
            }
        }

        if (crafter.AutoTurnOff && crafter.owner.isOn) {
            crafter.Toggle(false);
        }

        if (!crafter.idSoundBG.IsEmpty()) {
            EClass.Sound.Stop(crafter.idSoundBG);
        }

        crafter.OnEndAI(__instance);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.AddCard))]
    internal static bool OnAddProduct(Card __instance, Card c, ref Card __result)
    {
        // the receiver itself is "the player" while it stands in, see RemoteCraft.AsCrafter
        if (RemoteCraft.ProductReceiver is not { } receiver || !__instance.IsPC || __instance == receiver) {
            return true;
        }

        __result = receiver.AddCard(c);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.HoldCard))]
    internal static bool OnHoldProduct(Chara __instance, Card t)
    {
        if (RemoteCraft.ProductReceiver is not { } receiver || !__instance.IsPC) {
            return true;
        }

        if (t.GetRootCard() != receiver) {
            receiver.AddCard(t);
        }

        return false;
    }

    private static void NotifyClientCancel(AI_UseCrafter act)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host || act.owner is not { } owner) {
            return;
        }

        EmpLog.Debug("Remote craft aborted on host, cancelling act {ActType} of chara {Uid}",
            nameof(AI_UseCrafter), owner.uid);
        TaskCache.RequestCancel(host, owner, act);
    }

    /// <summary>
    ///     The game's first craft bonus looks at the host's list of recipes made and gives the tickets to the host.
    ///     For another player it is that player's list, told with the request, and the tickets are its own
    /// </summary>
    private static void GiveFirstTimeBonus(AI_UseCrafter act, AIUseCrafterArgs args, Recipe recipe)
    {
        if (!args.FirstTime || recipe.IsStaticLV() || NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        var num = 1 + recipe.source.GetReqSkill().Value / 20;
        using (MsgRelayContext.RedirectTo(act.owner)) {
            Msg.Say("firstTimeCraft", recipe.Name);
        }

        act.owner.Pick(ThingGen.Create("ticket_fortune").SetNum(num));

        // teams learning on their own: the list of recipes made is the crafter's team's, its teammates' games and
        // the host's book note it too
        var team = TeamKnowledge.On;
        foreach (var (peerId, chara) in host.ActiveRemoteCharas) {
            if (chara == act.owner || (team && TeamKnowledge.Shares(act.owner.uid, chara.uid))) {
                host.SendDeltaTo(peerId, new CraftFirstTimeDelta {
                    RecipeId = recipe.id,
                });
            }
        }

        TeamKnowledge.Crafted(act.owner.uid, recipe.id);
    }

    /// <summary>
    ///     TraitCrafter.Craft adds the idea it just taught to "the player" and plays "idea": the host's, whoever crafts.
    ///     For a guest the host takes it back out and the crafter's game gets it (host alone, or the host crafting: as the game)
    /// </summary>
    private static void TeachCrafter(Chara crafter, HashSet<int> before)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host ||
            host.ActiveRemoteCharas.FirstOrDefault(p => p.Value == crafter) is not { Value: not null } peer) {
            return;
        }

        foreach (var id in EClass.player.knownCraft.Except(before).ToArray()) {
            EClass.player.knownCraft.Remove(id);
            host.SendDeltaTo(peer.Key, new KnownCraftDelta { Id = id });
        }
    }

    /// <summary>
    ///     A fortune roll reads and raises the roll counter and the lucky coins of "the player" (TraitCrafter.Craft,
    ///     FortuneRollData.GetPrize): for another player they are its own, told with the request, and given back to
    ///     its game after the roll (audit 2026-10-23). Host alone, or an older peer: the host's, as in the game
    /// </summary>
    private static ScopeExit? RollAs(AI_UseCrafter act, AIUseCrafterArgs args)
    {
        if (act.crafter is not TraitRollingFortune || args.SeedFortune is not { } seed ||
            NetSession.Instance.Connection is not ElinNetHost host ||
            host.ActiveRemoteCharas.FirstOrDefault(p => p.Value == act.owner) is not { Value: not null } peer) {
            return null;
        }

        var player = EClass.player;
        var (ownSeed, ownCoin) = (player.seedFortune, player.luckycoin);
        player.seedFortune = seed;
        player.luckycoin = args.LuckyCoin ?? 0;

        return new() {
            OnExit = () => {
                // the next round of the same request goes on from here
                args.SeedFortune = player.seedFortune;
                args.LuckyCoin = player.luckycoin;
                host.SendDeltaTo(peer.Key, new FortuneCountersDelta {
                    SeedFortune = player.seedFortune,
                    LuckyCoin = player.luckycoin,
                });
                (player.seedFortune, player.luckycoin) = (ownSeed, ownCoin);
            },
        };
    }

    private static IEnumerable<AIAct.Status> RunRemote(AI_UseCrafter act, AIUseCrafterArgs args)
    {
        var crafter = act.crafter;
        var round = 0;

        act.owner.LookAt(crafter.owner.pos);

        while (true) {
            round++;
            // layer
            if (crafter.owner.isDestroyed) {
                NotifyClientCancel(act);
                yield return act.Success();
            }

            if (!crafter.idSoundBG.IsEmpty()) {
                SE.Play(crafter.idSoundBG);
            }

            // (an ingredient in a chest of the map may be one this game never registered: GitHub issue 33, the
            // guest's garbage in a sturdy box beside the Millstone)
            List<Thing> targets = [..args.Targets.Select(remote => (TaskCache.FindOrAdopt(remote) as Thing)!)];
            var blessed = BlessedState.Normal;

            using (ElinDelta.Simulate()) {
                foreach (var t in targets) {
                    if (t is { isDestroyed: false } && t.GetRootCard() != act.owner
                                                    && t.GetRootCard() == t && t.parent is not Card) {
                        EmpLog.Warning("Craft target {Uid} not held by initiator {OwnerUid}, reclaiming",
                            t.uid, act.owner.uid);
                        act.owner.AddThing(t);
                    }
                }
            }

            for (var i = 0; i < targets.Count; i++) {
                if (!IsIngValid(targets, i)) {
                    // logged and told: the player saw its craft run and nothing come of it (GitHub issue 33)
                    EmpLog.Information(
                        "Remote craft of chara {OwnerUid} at {CrafterId} refused: ingredient {Slot} (uid {Uid}) {Reason}",
                        act.owner.uid, crafter.owner.id, i, i < args.Targets.Count ? args.Targets[i]?.Uid ?? 0 : 0,
                        targets[i] is { isDestroyed: false } refused ? $"{refused.id} is not taken here" : "is not here");
                    using (MsgRelayContext.RedirectTo(act.owner)) {
                        Msg.SayNothingHappen();
                    }

                    NotifyClientCancel(act);
                    yield return act.Success();
                }
            }

            if (!crafter.IsFuelEnough(act.num, targets)) {
                using (MsgRelayContext.RedirectTo(act.owner)) {
                    Msg.Say("notEnoughFuel");
                }

                NotifyClientCancel(act);
                yield return act.Success();
            }

            act.ings = [];
            // InsertAction, OnProgressComplete
            using (ElinDelta.Simulate()) {
                for (var i = 0; i < targets.Count; i++) {
                    // two players crafting from one stack: never more than it still holds, that would make a copy
                    var ing = targets[i].Split(Math.Clamp(i < args.Required.Count ? args.Required[i] : 1, 1, targets[i].Num));
                    act.ings.Add(ing);
                    EmpLog.Debug(
                        "Remote craft round {CraftRound} split ing {Uid} num {CardNum}, origin {TargetUid} num left {TargetNum}",
                        round, ing.uid, ing.Num, targets[i].uid, targets[i].isDestroyed ? 0 : targets[i].Num);

                    switch (ing.blessedState) {
                        case <= BlessedState.Cursed when blessed > ing.blessedState:
                        case > BlessedState.Normal when blessed == BlessedState.Normal:
                            blessed = ing.blessedState;
                            break;
                    }

                    if (crafter.IsConsumeIng) {
                        var c = EClass._zone.AddCard(ing, crafter.owner.ExistsOnMap ? crafter.owner.pos : act.owner.pos);
                        c.altitude = crafter.owner.ExistsOnMap ? 0 : 1;
                        if (crafter.animeType == TraitCrafter.AnimeType.Microwave) {
                            c.isHidden = true;
                        }
                    }
                }
            }

            var requireOn = crafter.IsRequireFuel || crafter.ToggleType != ToggleType.None;
            if (requireOn && !crafter.owner.isOn) {
                using var toggleSimulate = ElinDelta.Simulate();
                crafter.Toggle(true);
            }

            var cost = crafter.GetCostSp(act);
            int duration;
            using (RemoteCraft.AsCrafter(act.owner)) {
                duration = crafter.GetDuration(act, cost);
            }

            var progress = new Progress_Custom {
                canProgress = () => {
                    if (requireOn && !crafter.owner.isOn) {
                        return false;
                    }

                    if (act.ings.Any(ing => ing.isDestroyed || (crafter.IsConsumeIng && !ing.ExistsOnMap))) {
                        return false;
                    }

                    return !crafter.owner.isDestroyed;
                },
                onProgressBegin = () => {
                    if (crafter is TraitRollingFortune) {
                        crafter.owner.animeCounter = 0.01f;
                    }
                },
                onProgress = _ => {
                    if (crafter.owner.ExistsOnMap && !act.owner.pos.Equals(crafter.owner.pos)) {
                        act.owner.LookAt(crafter.owner);
                    }

                    act.owner.PlaySound(crafter.idSoundProgress);

                    if (crafter.owner.ExistsOnMap) {
                        switch (crafter.animeType) {
                            case TraitCrafter.AnimeType.Microwave:
                            case TraitCrafter.AnimeType.Pot:
                                crafter.owner.renderer.PlayAnime(crafter.IdAnimeProgress);
                                break;
                        }
                    }

                    foreach (var ing in act.ings) {
                        ing.renderer.PlayAnime(crafter.IdAnimeProgress);
                    }
                },
                onProgressComplete = () => {
                    using var simulate = ElinDelta.Simulate();

                    if (crafter.StopSoundProgress) {
                        EClass.Sound.Stop(crafter.idSoundProgress);
                    }

                    act.owner.PlaySound(crafter.idSoundComplete);
                    var e = act.owner.elements.GetOrCreateElement(crafter.IDReqEle(act.recipe?.source));

                    if (act.recipe is { } recipe) {
                        RemoteCraft.ProductReceiver = act.owner;
                        try {
                            using var _ = MsgRelayContext.RedirectTo(act.owner);
                            using var standIn = RemoteCraft.AsCrafter(act.owner);
                            for (var i = 0; i < act.num; i++) {
                                recipe.Craft(blessed, i == 0, act.ings, crafter);
                            }
                        } finally {
                            RemoteCraft.ProductReceiver = null;
                        }

                        EClass.Sound.Play("craft");
                        var pos = crafter.owner.ExistsOnMap ? crafter.owner.pos : act.owner.pos;
                        Effect.Get("smoke").Play(pos);
                        Effect.Get("mine").Play(pos)
                            .SetParticleColor(recipe.GetColorMaterial().GetColor())
                            .Emit(10 + EClass.rnd(10));
                        act.owner.renderer.PlayAnime(AnimeID.JumpSmall);
                        GiveFirstTimeBonus(act, args, recipe);
                    } else {
                        Thing? t;
                        var knownBefore = new HashSet<int>(EClass.player.knownCraft);
                        using (MsgRelayContext.RedirectTo(act.owner))
                        using (RemoteCraft.AsCrafter(act.owner))
                        using (RollAs(act, args)) {
                            t = crafter.Craft(act);
                        }

                        TeachCrafter(act.owner, knownBefore);

                        if (t is not null) {
                            if (t.category.ignoreBless == 0) {
                                t.SetBlessedState(blessed);
                            }

                            t.PlaySoundDrop(false);
                            EClass._zone.AddCard(t, act.owner.pos);
                            t.Identify(false);
                            act.owner.Pick(t);
                        }
                    }

                    for (var i = 0; i < act.ings.Count; i++) {
                        if (crafter.ShouldConsumeIng(crafter.GetSource(act), i)) {
                            act.ings[i].Destroy();
                        }
                    }

                    foreach (var ing in act.ings) {
                        if (ing.ExistsOnMap) {
                            act.owner.Pick(ing);
                        }
                    }

                    if (crafter.IsRequireFuel) {
                        crafter.owner.ModCharge(-crafter.FuelCost * act.num);
                        if (crafter.owner.c_charges <= 0) {
                            crafter.owner.c_charges = 0;
                            crafter.Toggle(false);
                        }
                    }

                    // a player's stamina is its own game's (CharaStaminaDelta): its RunClient spends it, and the
                    // overwork its game deals is told here. Spent again on this copy, which may be behind, the
                    // guest took overwork blows with stamina to spare, twice when it really ran out (GitHub issue 64)
                    var ownStamina = !act.owner.IsRemotePlayer;
                    for (var i = 0; i < act.num; i++) {
                        var actor = act.owner;
                        actor.RemoveCondition<ConInvulnerable>();
                        EClass.player.invlunerable = false;
                        actor.elements.ModExp(e.id, cost * 12f * (100f + duration * 2f) / 100f);
                        if (ownStamina) {
                            actor.stamina.Mod(-cost);
                        }

                        if (actor.isDead) {
                            break;
                        }
                    }

                    EmpLog.Debug("Remote craft round {CraftRound} complete for chara {OwnerUid}, ings {@CraftIngs}",
                        round, act.owner.uid,
                        act.ings.Select(ing => new { Uid = ing.uid, Destroyed = ing.isDestroyed }));

                    Rand.SetSeed();

                    if (crafter is TraitCookerMicrowave && act.recipe?.id == "onsentamago" && EClass.rnd(3) != 0) {
                        var cooking = act.owner.Evalue(SKILL.cooking);
                        var power = EClass.curve((200 + act.ings[0].Quality * 5) * (100 + cooking * 10) / 100, 400, 100);
                        ActEffect.ProcAt(EffectId.Explosive, power, BlessedState.Normal,
                            crafter.owner.ExistsOnMap ? crafter.owner : act.owner, act.owner, act.owner.pos,
                            true,
                            new() {
                                aliasEle = "eleImpact",
                            });
                    }
                },
            }.SetDuration(duration, 5);

            act.owner.SetTempHand(-1, -1);

            if (EClass.debug.godCraft) {
                progress.SetDuration(1, 1);
            }

            yield return act.Do(progress);

            if (progress.status == AIAct.Status.Fail || crafter.CloseOnComplete) {
                yield return act.Cancel();
            }

            if (!crafter.IsConsumeIng || !args.Repeat) {
                break;
            }
        }
        yield break;

        bool IsIngValid(List<Thing> ings, int i)
        {
            if (ings[i] is not { isDestroyed: false } t) {
                return false;
            }

            var c = t.GetRootCard();
            if (c is { isChara: true, IsPCFaction: false }) {
                return false;
            }

            // a chest or the ground of another team's land: its own game should not have offered it (Teams)
            if (!Teams.CanTake(act.owner, t)) {
                return false;
            }

            if (crafter.IsFactory) {
                return true;
            }

            // multi drag crafter
            if (i != 1) {
                return crafter.IsCraftIngredient(t, i);
            }

            var main = ings.Count > 0 ? ings[0] : null;
            foreach (var row in EClass.sources.recipes.rows) {
                if (!crafter.IsIngredient(0, row, main) || (main == t && main.Num < 2)) {
                    continue;
                }

                if (crafter.IsIngredient(i, row, t)) {
                    return true;
                }
            }

            return false;
        }
    }
}