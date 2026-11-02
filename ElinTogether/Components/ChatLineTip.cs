using UnityEngine;
using UnityEngine.EventSystems;

namespace ElinTogether.Components;

/// <summary>
///     A Chat line holding an item link: the pointer over the line shows the item's own tooltip, as the inventory
///     does (Card.WriteNote in the game's "note" tooltip), for the line's first link
/// </summary>
internal sealed class ChatLineTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    internal Thing? Thing;

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (Thing is not { } thing || TooltipManager.Instance == null) {
            return;
        }

        TooltipManager.Instance.ShowTooltip(new() {
            enable = true,
            id = "note",
            onShowTooltip = tip => thing.WriteNote(tip.note),
        }, transform);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (TooltipManager.Instance != null) {
            TooltipManager.Instance.HideTooltips();
        }
    }

    private void OnDisable() => OnPointerExit(null!);
}
