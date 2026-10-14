using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class InvSaveDataDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Container { get; init; }

    [Key(1)]
    public required LZ4Bytes Data { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Container.Find() is not { isDestroyed: false } container) {
            EmpLog.Warning("Dropping {DeltaType} from peer {PeerIndex}, uid {Uid} cannot be resolved here",
                nameof(InvSaveDataDelta), OriginPeer, Container.Uid);
            return;
        }

        if (Data.Decompress<Window.SaveData>() is not { } data) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (container.c_windowSaveData is not { } saveData) {
            container.c_windowSaveData = data;
        } else {
            saveData.autodump = data.autodump;
            saveData.priority = data.priority;
            saveData.flag = data.flag;
            saveData.sharedType = data.sharedType;
            saveData.category = data.category;
            saveData.sortMode = data.sortMode;
            saveData.sort_ascending = data.sort_ascending;
            saveData.alwaysSort = data.alwaysSort;
            saveData.excludeDump = data.excludeDump;
            saveData.excludeCraft = data.excludeCraft;
            saveData.compress = data.compress;
            saveData.advDistribution = data.advDistribution;
            saveData.noRotten = data.noRotten;
            saveData.onlyRottable = data.onlyRottable;
            saveData.cats = data.cats;
            saveData.filter = data.filter;
            saveData._filterStrs = null;
        }

        foreach (var layer in LayerInventory.listInv) {
            var inv = layer.invs[0];
            if (inv.owner.Container != container) {
                continue;
            }

            if (inv.isList) {
                inv.RefreshList();
            } else {
                inv.RefreshGrid();
            }
        }
    }
}