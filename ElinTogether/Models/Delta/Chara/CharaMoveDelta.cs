using System.Collections.Generic;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaMoveDelta : ElinDelta
{
    private static readonly Dictionary<int, float> _recentMoves = [];

    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required Position Pos { get; init; }

    [Key(2)]
    public Card.MoveType MoveType { get; init; }

    internal override bool RequiresGameStarted => false;

    public static implicit operator CharaMoveDelta(Chara chara)
    {
        return new() {
            Owner = chara,
            Pos = chara.pos,
            MoveType = Card.MoveType.Force,
        };
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        // this only happens on game load
        if (core.game?.activeZone?.map is null) {
            net.Delta.DeferLocal(this);
            return;
        }

        // we do not apply to ourselves
        if (Owner.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        // drop this
        if (chara.currentZone != NetSession.Instance.CurrentZone) {
            return;
        }

        var pos = (Point)Pos;
        if (pos.IsInBounds &&
            (chara.pos.Equals(pos) || chara.Stub_Move(Pos, MoveType) == Card.MoveResult.Success)) {
            _recentMoves[chara.uid] = Time.unscaledTime;
        }
    }

    internal static bool HasRecentMove(Chara chara)
    {
        return _recentMoves.TryGetValue(chara.uid, out var at) && Time.unscaledTime - at < 1f;
    }

    internal static void ClearRecentMoves()
    {
        _recentMoves.Clear();
    }
}