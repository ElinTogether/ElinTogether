using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElinTogether.Helper.Extensions;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class InvPlaceAbilityDelta : ElinDelta
{
    public const string LayoutKey = "emp_ability_layout";

    [Key(0)]
    public required List<AbilityTokenSlot> Layout { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host) {
            return;
        }

        if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var chara)) {
            EmpLog.Warning("Dropping {DeltaType} from peer {PeerIndex}, no registered chara",
                nameof(InvPlaceAbilityDelta), OriginPeer);
            return;
        }

        var sb = new StringBuilder();
        foreach (var slot in Layout) {
            if (!sources.elements.alias.TryGetValue(slot.Alias, out var source) ||
                !chara.HasElement(source.id)) {
                EmpLog.Warning("Skipping layout entry from peer {PeerIndex}, chara {OwnerUid} has no element {ElementAlias}",
                    OriginPeer, chara.uid, slot.Alias);
                continue;
            }

            if (sb.Length > 0) {
                sb.Append(';');
            }

            sb.Append(slot.Alias).Append(',').Append(slot.InvX).Append(',').Append(slot.InvY);
        }

        chara.SetStr(LayoutKey, sb.Length > 0 ? sb.ToString() : null);

        EmpLog.Debug("Stored ability layout of chara {OwnerUid}, {LayoutCount} entries",
            chara.uid, Layout.Count);
    }

    internal static void InvalidateFakeAbilityCard(Chara chara)
    {
        foreach (var thing in chara.things.Flatten().ToList()) {
            if (thing is { trait: TraitAbility, isDestroyed: false }) {
                thing.Destroy();
            }
        }
    }

    public static List<AbilityTokenSlot> Parse(string? layout)
    {
        var slots = new List<AbilityTokenSlot>();
        if (layout is not { Length: > 0 }) {
            return slots;
        }

        foreach (var entry in layout.Split(';')) {
            var parts = entry.Split(',');
            if (parts.Length == 3 && int.TryParse(parts[1], out var x) && int.TryParse(parts[2], out var y)) {
                slots.Add(new() {
                    Alias = parts[0],
                    InvX = x,
                    InvY = y,
                });
            }
        }

        return slots;
    }

    [MessagePackObject]
    public class AbilityTokenSlot
    {
        [Key(0)]
        public required string Alias { get; init; }

        [Key(1)]
        public required int InvX { get; init; }

        [Key(2)]
        public required int InvY { get; init; }
    }
}