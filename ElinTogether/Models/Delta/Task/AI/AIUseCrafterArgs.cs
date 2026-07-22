using System.Collections.Generic;
using System.Linq;
using ElinTogether.Elements;
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
    public required List<RemoteCard> Ingredients { get; init; }

    public static AIUseCrafterArgs Create(AI_UseCrafter ai)
    {
        return new() {
            Factory = ai.crafter.owner,
            Duration = ai.crafter.GetDuration(ai, ai.crafter.GetCostSp(ai)),
            Ingredients = ai.ings.Select(t => (RemoteCard)t).ToList(),
        };
    }

    public override AIAct CreateSubAct()
    {
        return DelegateProgress.Create(new AI_UseCrafter()).SetDuration(Duration, 5);
    }
}