using System.Collections.Generic;
using System.Linq;
using ElinTogether.Elements;
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
        if (RemoteCraft.ProductReceiver is not { } receiver || !__instance.IsPC) {
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

            List<Thing> targets = [..args.Targets.Select(remote => (remote.Find() as Thing)!)];
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
                if (!IsIngValid(targets[i], i)) {
                    NotifyClientCancel(act);
                    yield return act.Success();
                }
            }

            if (!crafter.IsFuelEnough(act.num, targets)) {
                Msg.Say("notEnoughFuel");
                NotifyClientCancel(act);
                yield return act.Success();
            }

            act.ings = [];
            // InsertAction, OnProgressComplete
            using (ElinDelta.Simulate()) {
                for (var i = 0; i < targets.Count; i++) {
                    var ing = targets[i].Split(i < args.Required.Count ? args.Required[i] : 1);
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
            var duration = crafter.GetDuration(act, cost);

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
                        recipe.TryGetFirstTimeBonus();
                    } else {
                        var t = crafter.Craft(act);
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

                    for (var i = 0; i < act.num; i++) {
                        var actor = act.owner;
                        actor.RemoveCondition<ConInvulnerable>();
                        EClass.player.invlunerable = false;
                        actor.elements.ModExp(e.id, cost * 12f * (100f + duration * 2f) / 100f);
                        actor.stamina.Mod(-cost);
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

        bool IsIngValid(Thing? t, int i)
        {
            if (t is null || t.isDestroyed) {
                return false;
            }

            var c = t.GetRootCard();
            if (c is { isChara: true, IsPCFaction: false }) {
                return false;
            }

            return crafter.IsFactory || crafter.IsCraftIngredient(t, i);
        }
    }
}