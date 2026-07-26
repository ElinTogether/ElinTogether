using System.Collections.Generic;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaProgressCompleteDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required int CompletedActId { get; init; }

    [Key(2)]
    public required List<ElinDelta> DeltaList { get; init; }

    public static CharaProgressCompleteDelta? Current { get; private set; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara) {
            return;
        }

        // complete remote tasks because we assigned them max value to prevent randomness
        var type = ActMappingValidator.Default.IdToActMapping[CompletedActId];
        var ai = chara.ai.Current;
        while (ai is not null && ai.GetType() != type) {
            ai = ai.parent;
        }

        if (ai is null) {
            return;
        }

        if (!ai.IsChildRunning) {
            EmpLogger.Debug("CharaProgressCompleteDelta: child not running");
        }

        if (ai.child is null) {
            return;
        }

        Current = this;
        try {
            ai.child.OnProgressComplete();
            ai.child.Success();

            ai.Tick();
            if (ai.status != AIAct.Status.Running) {
                chara.SetNoGoal();
            }

            CharaPickThingDelta.CanApplyOnPC = true;
            DeltaList.ForEach(action => action.Apply(net));
        } finally {
            CharaPickThingDelta.CanApplyOnPC = false;
            Current = null;
        }

        if (chara.IsPC) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (chara.ai is not GoalRemote remote) {
            return;
        }

        remote.InsertAction(null);
    }
}