using System.Collections.Immutable;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ElementChangeDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required int Element { get; init; }

    [Key(2)]
    public required ImmutableArray<int> Value { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        var ele = chara.elements.GetOrCreateElement(Element);
        ele.vBase = Value[0];
        ele.vExp = Value[1];
        ele.vPotential = Value[2];
        ele.vTempPotential = Value[3];

        ele.CheckLevelBonus(chara.elements);
        ele.OnChangeValue();

        if (ele is { vBase: 0, vExp: 0, vPotential: 0, vTempPotential: 0 }) {
            chara.elements.Remove(Element);
        }
    }

    protected override bool OnRefresh()
    {

        return true;
    }

    public static ElementChangeDelta Create(Card owner, Element element)
    {
        return new() {
            Owner = owner,
            Element = element.id,
            Value = [element.vBase, element.vExp, element.vPotential, element.vTempPotential],
        };
    }
}