using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaActPerformDelta : ElinDelta
{
    // builtin acts that are not instantiated
    private static readonly Dictionary<int, Act> _builtInMapping = new() {
        [ABILITY.ActWait] = ACT.Wait,
        [ABILITY.ActMelee] = ACT.Melee,
        [ABILITY.ActThrow] = ACT.Throw,
        [ABILITY.ActRanged] = ACT.Ranged,
        [ABILITY.ActKick] = ACT.Kick,
        [ABILITY.ActChat] = ACT.Chat,
        [ABILITY.ActPick] = ACT.Pick,
        [ABILITY.ActItem] = ACT.Item,
    };
    private static bool _staticMapped;

    [Key(0)]
    public required int ActId { get; init; }

    [Key(1)]
    public required RemoteCard Owner { get; init; }

    [Key(2)]
    public required RemoteCard? TargetCard { get; init; }

    [Key(3)]
    public required Position? Pos { get; init; }

    public static CharaActPerformDelta Create(Act act)
    {
        ApplyBuiltInMapping();

        return new() {
            ActId = act.id,
            Owner = Act.CC,
            TargetCard = Act.TC,
            Pos = Act.TP,
        };
    }

    protected override void OnApply(ElinNetBase net)
    {
        ApplyBuiltInMapping();

        // we do not apply to ourselves
        if (Owner.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        // reperform act
        var act = _builtInMapping.GetValueOrDefault(ActId);
        act ??= chara.elements.GetElement(ActId)?.act ?? ACT.Create(ActId);
        act.id = ActId;

        // pos compensation if high rtt
        var target = TargetCard?.Find();
        var pos = Pos;
        if (target is Chara { isDead: false, IsInActiveMap: true } targetChara &&
            pos is not null && targetChara.pos.Distance(pos) <= 2) {
            pos = targetChara.pos;
        }

        act.Perform(chara, target, pos);
    }

    private static void ApplyBuiltInMapping()
    {
        if (_staticMapped) {
            return;
        }

        foreach (var (k, v) in _builtInMapping) {
            v.id = k;
        }
        _staticMapped = true;
    }
}