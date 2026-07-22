using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Card surrogate
/// </summary>
[MessagePackObject]
public class RemoteCard : IEquatable<RemoteCard>
{
    public enum CardType : byte
    {
        Thing,
        Chara,
    }

    [Key(0)]
    public required int Uid { get; init; }

    [Key(1)]
    public required CardType Type { get; init; }

    [Key(2)]
    public RemoteCard? Parent { get; init; }

    [Key(3)]
    public LZ4Bytes? Data { get; set; }

    [Key(4)]
    public int Num { get; set; }

    public bool Equals(RemoteCard? other)
    {
        if (other is null) {
            return false;
        }

        if (ReferenceEquals(this, other)) {
            return true;
        }

        return Uid == other.Uid && Type == other.Type;
    }

    [return: NotNullIfNotNull("card")]
    public static RemoteCard? Create(Card? card, bool addToCache = false, bool withData = false)
    {
        if (card is null) {
            return null;
        }

        var num = card.Num;
        if (card.uid < 0 && CardCache.Find(-card.uid) is Thing source) {
            card = source;
        }

        if (NetSession.Instance.IsHost && addToCache) {
            CardCache.Add(card);
        }

        return new() {
            Uid = card.uid,
            Type = card is Thing ? CardType.Thing : CardType.Chara,
            // do not compress parent
            Parent = card.parentCard,
            Data = withData ? LZ4Bytes.Create(card) : null,
            Num = num,
        };
    }

    [return: NotNullIfNotNull("card")]
    public static implicit operator RemoteCard?(Card? card)
    {
        return Create(card);
    }

    public static implicit operator Chara?(RemoteCard? remote)
    {
        return remote?.Find() as Chara;
    }

    public static implicit operator Thing?(RemoteCard? remote)
    {
        return remote?.Find() as Thing;
    }

    public static implicit operator Card?(RemoteCard? remote)
    {
        return remote?.Find();
    }

    public Card? Find()
    {
        var card = CardCache.Find(Uid);
        if (card is not null) {
            return card;
        }

        if (NetSession.Instance.IsHost) {
            return null;
        }

        if (Type == CardType.Chara) {
            card = EClass.game?.cards.globalCharas.GetValueOrDefault(Uid);
        }

        card ??= Type switch {
            CardType.Thing => Data?.Decompress<Thing>(),
            CardType.Chara => Data?.Decompress<Chara>(),
            _ => null,
        };

        if (card is null && Parent is not null) {
            card = Parent.Find()?.things.Find(Uid);
        }

        if (card is not null) {
            CardCache.Set(card);
        }

        return card;
    }

    public override string ToString()
    {
        return $"{Find()}";
    }

    public static bool operator ==(RemoteCard? lhs, RemoteCard? rhs)
    {
        if (lhs is null) {
            return rhs is null;
        }

        return lhs.Equals(rhs);
    }

    public static bool operator !=(RemoteCard? lhs, RemoteCard? rhs)
    {
        return !(lhs == rhs);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) {
            return false;
        }

        if (ReferenceEquals(this, obj)) {
            return true;
        }

        if (obj.GetType() != GetType()) {
            return false;
        }

        return Equals((RemoteCard)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Uid, Type);
    }
}