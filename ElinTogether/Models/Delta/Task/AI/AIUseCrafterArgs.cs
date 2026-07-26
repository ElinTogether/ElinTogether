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
        };
    }

    public override AIAct CreateSubAct()
    {
        if (!NetSession.Instance.IsHost) {
            return DelegateProgress.Create(new AI_UseCrafter()).SetDuration(Duration, 5);
        }

        var crafter = Factory.Find() switch {
            Chara chara => new TraitSelfFactory { owner = chara },
            Thing { trait: TraitCrafter trait } => trait,
            _ => null,
        };

        if (crafter is null) {
            return new NoGoal();
        }

        Recipe? recipe = null;
        if (RecipeId is not null) {
            if (RecipeManager.dict.TryGetValue(RecipeId) is not { } source) {
                return new NoGoal();
            }

            recipe = Recipe.Create(source, RecipeMat);
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
}