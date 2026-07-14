using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class InvSaveDataDelta : ElinDelta
{
    [Key(0)]
    public required string WindowId { get; init; }

    [Key(1)]
    public required LZ4Bytes Data { get; init; }

    [Key(2)]
    public required bool IsShop { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Data.Decompress<Window.SaveData>() is not { } data) {
            return;
        }

        if (Window.dictData.TryGetValue(WindowId, out var saveData)) {
            saveData.CopyFrom(data);
        }

        // refresh sort
        var pref = EMono.player.pref;
        if (IsShop) {
            pref.sortInvShop = data.sortMode;
            pref.sort_ascending_shop = data.sort_ascending;
        } else {
            pref.sortInv = data.sortMode;
            pref.sort_ascending = data.sort_ascending;
        }

        var inv = LayerInventory.listInv.Find(l => l.invs[0].window.idWindow == WindowId)?.invs[0];
        if (inv == null) {
            Window.dictData[WindowId] = data;
            return;
        }

        inv.window.saveData.CopyFrom(data);
        inv.RefreshWindow();

        // refresh share button
        var flag = data.sharedType == ContainerSharedType.Shared;
        inv.window.buttonShared.image.sprite = flag ? EMono.core.refs.icons.shared : EMono.core.refs.icons.personal;
        inv.window.buttonShared.tooltip.lang = flag ? "hintShared" : "hintPrivate";
    }
}