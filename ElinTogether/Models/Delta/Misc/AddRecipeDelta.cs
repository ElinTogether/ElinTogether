using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class AddRecipeDelta : ElinDelta
{
    [Key(0)]
    public required string RecipeId { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        player.recipes.Add(RecipeId, !player.recipes.IsKnown(RecipeId));
    }
}