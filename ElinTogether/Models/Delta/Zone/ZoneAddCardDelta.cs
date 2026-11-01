using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ZoneAddCardDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    [Key(1)]
    public required int ZoneUid { get; init; }

    [Key(2)]
    public required Position Pos { get; init; }

    /// <summary>
    ///     A guest puts down what it holds or what it reaches, on the map it is on; never a character but its own or
    ///     its companions', never into another zone (security audit 2026-10-27: any card went anywhere, gone in an
    ///     unloaded zone)
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (NoCharacter(sender) is { } none) {
            return none;
        }

        if (ZoneUid != _zone.uid) {
            return $"card {Card.Uid} sent to zone {ZoneUid}, not this map";
        }

        return Card.Find() is Chara target
            ? OwnsOrCompanion(sender!, target) ? null : $"character {target.uid} is not the sender's"
            : NotReachable(sender, Card);
    }

    internal override void OnHostRefused(ElinNetHost host)
    {
        SendBack(host, Card);
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (game?.activeZone?.map is null) {
            net.Delta.DeferLocal(this);
            return;
        }

        var zone = game.spatials.Find(ZoneUid) ?? SpatialGenEvent.TryPop(ZoneUid);
        if (zone is null) {
            return;
        }

        if (Card.Find() is not { } card) {
            TaskCache.CancelClientAct(net, this, Card);
            return;
        }

        if (card.isDestroyed) {
            EmpLog.Warning("Attempting to add destroyed item {Uid} to the zone at {@Pos}", card.uid, Pos);
            TaskCache.CancelClientAct(net, this, Card);
            return;
        }

        // an installed thing is not moved onto, nor off, the way to Vernis's mine, nor the furniture of Vernis not claimed
        // (MineWay): put back where it is
        if (net is ElinNetHost wayHost && FromPeer && card is Thing { IsInstalled: true } && card.parent == zone &&
            (MineWay.ProtectsCard(card) || MineWay.IsTownFurniture(card) || MineWay.Protects(Pos.X, Pos.Z, System.Math.Max(card.W, card.H) - 1))) {
            SendBack(wayHost, Card);
            return;
        }

        // relay to other clients
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        // do not add client characters from this delta
        if (card.IsPC) {
            return;
        }

        // do not update it again if same position
        // we derive all other state changes to sub deltas
        if (card.parent != zone || card.pos != Pos) {
            zone.AddCard(card, Pos.X, Pos.Z);
            EmpLog.Debug("Zone add card {Uid} at {@Pos}", card.uid, Pos);
        }
    }
}