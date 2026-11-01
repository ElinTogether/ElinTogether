using System.Collections.Generic;
using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models.AI;

[MessagePackObject]
public class AIUseCrafterArgs : TaskArgsBase
{
    [Key(0)]
    public required RemoteCard Factory { get; init; }

    [Key(1)]
    public required int Duration { get; init; }

    [Key(2)]
    public required int Num { get; init; }

    [Key(3)]
    public required List<RemoteCard> Targets { get; init; }

    [Key(4)]
    public required List<int> Required { get; init; }

    [Key(5)]
    public required bool Repeat { get; init; }

    [Key(6)]
    public required string? RecipeId { get; init; }

    [Key(7)]
    public required int RecipeMat { get; init; }

    /// <summary>
    ///     The crafter never made this recipe: which recipes a player made is in its own game, not the host's
    /// </summary>
    [Key(8)]
    public bool FirstTime { get; init; }

    /// <summary>
    ///     Fortune roll: the roll counter and the lucky coins of the player who rolls, its own (the game reads the
    ///     host's). Kept up to date by the host across the rounds of one request. Null from an older peer: the host's
    /// </summary>
    [Key(9)]
    public int? SeedFortune { get; set; }

    [Key(10)]
    public int? LuckyCoin { get; set; }

    public static AIUseCrafterArgs Create(AI_UseCrafter ai)
    {
        var targets = ai.layer?.GetTargets() ?? [];
        if (ai.ings.Count == 0) {
            ai.ings = [..targets];
        }

        return new() {
            Factory = ai.crafter.owner,
            Duration = ai.crafter.GetDuration(ai, ai.crafter.GetCostSp(ai)),
            Num = ai.num,
            Targets = [..targets.Select(thing => (RemoteCard)thing)],
            Required = [..targets.Select((_, i) => ai.layer!.GetReqIngredient(i))],
            Repeat = ai.layer?.RepeatAI ?? false,
            RecipeId = ai.recipe?.id,
            RecipeMat = ai.recipe?.idMat ?? -1,
            FirstTime = ai.recipe?.HasFirstTimeBonus() ?? false,
            SeedFortune = ai.crafter is TraitRollingFortune ? EClass.player.seedFortune : null,
            LuckyCoin = ai.crafter is TraitRollingFortune ? EClass.player.luckycoin : null,
        };
    }

    public override AIAct CreateSubAct()
    {
        if (!NetSession.Instance.IsHost) {
            return DelegateProgress.Create(typeof(AI_UseCrafter)).SetDuration(Duration, 5);
        }

        var crafter = TaskCache.FindOrAdopt(Factory) switch {
            Chara chara => new TraitSelfFactory { owner = chara },
            Thing { trait: TraitCrafter trait } => trait,
            _ => null,
        };

        if (crafter is null) {
            EmpLog.Information("Remote craft refused: crafter uid {Uid} is not here", Factory.Uid);
            return new NoGoal();
        }

        Recipe? recipe = null;
        if (RecipeId is not null) {
            if (RecipeManager.dict.TryGetValue(RecipeId) is not { } source) {
                return new NoGoal();
            }

            recipe = Recipe.Create(source, RecipeMat);
            BindIngredients(recipe);
        }

        if (recipe is not null && crafter is TraitFactory factory) {
            factory.recipe = recipe;
        }

        var act = new AI_UseCrafter {
            crafter = crafter,
            num = Num,
            recipe = recipe,
        };

        RemoteCraft.Attach(act, this);
        return act;
    }

    private void BindIngredients(Recipe recipe)
    {
        recipe.BuildIngredientList();

        var slots = recipe.ingredients;
        var bound = new List<object>();
        for (var i = 0; i < Targets.Count && i < slots.Count; i++) {
            if (TaskCache.FindOrAdopt(Targets[i]) is not Thing thing) {
                continue;
            }

            slots[i].SetThing(thing);
            bound.Add(new {
                Uid = thing.uid,
                thing.Num,
            });
        }

        if (Targets.Count > slots.Count) {
            EmpLog.Warning("Remote craft recipe {RecipeId} has less ingredient slots {@CraftIngs}",
                recipe.id, Targets);
        }

        EmpLog.Debug("Remote craft recipe {RecipeId} bound ings {@CraftIngs}, mat {MaterialId}",
            recipe.id, bound, recipe.GetMainMaterial().id);
    }
}