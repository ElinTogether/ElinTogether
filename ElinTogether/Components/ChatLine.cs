using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ElinTogether.Components;

/// <summary>
///     A Chat line the pointer can use: over a line holding an item link, the item's own tooltip, as the inventory
///     shows it (Card.WriteNote in the game's "note" tooltip); a click on another player's line, the game's context
///     menu with what can be done about that player (write to it, call it, mute it)
/// </summary>
internal sealed class ChatLine : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    internal Thing? Thing;
    internal Func<IEnumerable<(string Label, Action Act)>>? Menu;

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (Thing is not { } thing || TooltipManager.Instance == null) {
            return;
        }

        TooltipManager.Instance.ShowTooltip(new() {
            enable = true,
            id = "note",
            // (a copy that belongs nowhere: a note that cannot be written is a plain name, never an error)
            onShowTooltip = tip => {
                try {
                    thing.WriteNote(tip.note);
                } catch (Exception ex) {
                    EmpLog.Information(ex, "Chat item link tooltip of {Id} could not be written", thing.id);
                    tip.note.Clear();
                    tip.note.AddText(thing.Name);
                    tip.note.Build();
                }
            },
        }, transform);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (Thing != null && TooltipManager.Instance != null) {
            TooltipManager.Instance.HideTooltips();
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (Menu == null || new List<(string Label, Action Act)>(Menu()) is not { Count: > 0 } entries) {
            return;
        }

        var menu = EClass.ui.CreateContextMenuInteraction();
        foreach (var (label, act) in entries) {
            var action = act;
            menu.AddButton(label, () => action());
        }

        menu.Show();
    }

    private void OnDisable() => OnPointerExit(null!);
}
