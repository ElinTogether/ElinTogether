using System.Collections.Generic;
using ElinTogether.API.SourceValidation;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaTaskCancelDelta : ElinDelta
{
    public const int ForceCancelCountRequired = 2;

    public static readonly Dictionary<int, int> LastCancelDelta = [];

    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required int ActId { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara) {
            return;
        }

        var type = ActMappingValidator.Default.IdToActMapping[ActId];
        var ai = chara.ai.Current;
        while (ai is not null && ai.GetType() != type) {
            ai = ai.parent;
        }

        if (!LastCancelDelta.TryAdd(Owner.Uid, 1)) {
            LastCancelDelta[Owner.Uid]++;
        }

        if (ai is not { status: AIAct.Status.Running }) {
            if (LastCancelDelta.GetValueOrDefault(Owner.Uid) >= ForceCancelCountRequired) {
                EmpLog.Warning("Force cancelling possibly stuck act {ActId}, {ActType} on chara {Uid}",
                    ActId, type.Name, Owner.Uid);
            } else {
                return;
            }
        }

        // relay to clients
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        LastCancelDelta.Remove(Owner.Uid);

        ai.Stub_Cancel();
    }
}