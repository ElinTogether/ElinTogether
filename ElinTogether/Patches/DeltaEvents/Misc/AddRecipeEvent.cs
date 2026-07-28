using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class AddRecipeEvent
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RecipeManager), nameof(RecipeManager.Add))]
    internal static void OnAddRecipe(string id)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return;
        }

        connection.Delta.AddRemote(new AddRecipeDelta {
            RecipeId = id,
        });
    }
}