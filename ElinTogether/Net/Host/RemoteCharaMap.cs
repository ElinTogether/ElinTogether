using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ElinTogether.Net;

/// <summary>
///     ElinNetHost.ActiveRemoteCharas: peer id -> the character it plays on this map, with a count of each
///     character so that "is this one played by someone?" (IsRemotePlayer, IsActiveRemoteChara, asked for many cards
///     every frame) is one lookup instead of a walk over every player (efficiency audit 2026-11-03).
///     Still a Dictionary&lt;int, Chara&gt; for the bench tools that read it by reflection; the writes go through
///     the members below (hidden, not virtual: write it only through this type, never through a Dictionary or
///     IDictionary reference)
/// </summary>
internal sealed class RemoteCharaMap : Dictionary<int, Chara>
{
    private readonly Dictionary<Chara, int> _played = [];

    public new Chara this[int peerId]
    {
        get => base[peerId];
        set {
            if (TryGetValue(peerId, out var was)) {
                if (ReferenceEquals(was, value)) {
                    return;
                }

                Forget(was);
            }

            base[peerId] = value;
            _played[value] = _played.TryGetValue(value, out var n) ? n + 1 : 1;
        }
    }

    public new void Add(int peerId, Chara chara)
    {
        base.Add(peerId, chara);
        _played[chara] = _played.TryGetValue(chara, out var n) ? n + 1 : 1;
    }

    public new bool Remove(int peerId)
    {
        return Remove(peerId, out _);
    }

    public new bool Remove(int peerId, [MaybeNullWhen(false)] out Chara chara)
    {
        if (!base.Remove(peerId, out chara)) {
            return false;
        }

        Forget(chara);
        return true;
    }

    public new void Clear()
    {
        base.Clear();
        _played.Clear();
    }

    /// <summary>
    ///     That character is played by a peer on this map (one lookup)
    /// </summary>
    public new bool ContainsValue(Chara chara)
    {
        return chara is not null && _played.ContainsKey(chara);
    }

    private void Forget(Chara chara)
    {
        if (!_played.TryGetValue(chara, out var n)) {
            return;
        }

        if (n <= 1) {
            _played.Remove(chara);
        } else {
            _played[chara] = n - 1;
        }
    }
}
