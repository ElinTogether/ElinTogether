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

    protected override void OnApply(ElinNetBase net)
    {
        if (Data.Decompress<Window.SaveData>() is not { } data) {
            return;
        }

        if (Window.dictData.TryGetValue(WindowId, out var saveData)) {
            saveData.CopyFrom(data);
            return;
        }

        var inv = LayerInventory.listInv.Find(l => l.invs[0].window.idWindow == WindowId)?.invs[0];
        if (inv is null) {
            Window.dictData[WindowId] = data;
            return;
        }

        inv.window.saveData.CopyFrom(data);
        inv.RefreshWindow();

        // refresh share button
        var flag = data.sharedType == ContainerSharedType.Shared;
        inv.window.buttonShared.image.sprite = flag ? EMono.core.refs.icons.shared : EMono.core.refs.icons.personal;
        inv.window.buttonShared.tooltip.lang = flag ? "hintShared" : "hintPrivate";
        inv.window.buttonShared.ShowTooltipForced();
    }
}