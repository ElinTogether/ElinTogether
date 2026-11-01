using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using ElinTogether.Net;
using MessagePack;
using MessagePack.Formatters;

namespace ElinTogether.Models;

/// <summary>
///     Card surrogate
/// </summary>
[MessagePackObject]
[MessagePackFormatter(typeof(RemoteCardFormatter))]
public class RemoteCard : IEquatable<RemoteCard>
{
    public enum CardType : byte
    {
        Thing,
        Chara,
    }

    [Key(0)]
    public required int Uid { get; set; }

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

        if (addToCache) {
            CardCache.Add(card);
        }

        return new() {
            Uid = card.uid,
            Type = card is Thing ? CardType.Thing : CardType.Chara,
            // do not compress parent
            Parent = card.parentCard,
            Data = withData ? LZ4Bytes.Create(card) : null,
            Num = card.Num,
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
        if (PendingUid.IsPending(Uid) && PendingRebind.Resolve(Uid) is var adopted and > 0) {
            Uid = adopted;
        }

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
            card.uid = Uid;
            CardCache.Set(card);
            CardCache.KeepAlive(card);
        }

        return card;
    }

    public override string ToString()
    {
        // never Find(): on a client it decodes and caches the card, a log line changing state (robustness audit 2026-11-01)
        return $"{Type}:{Uid}";
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

/// <summary>
///     A card with no parent, no data and a count of 1 (nearly every character, most items) is one integer,
///     uid * 2 + type, instead of an array of five fields: a quarter of every delta list and a fifth of every snapshot
///     (efficiency audit 2026-10-27). Anything else is the array as before, and the array is always read, so a
///     message written the old way (empload) still reads
/// </summary>
public sealed class RemoteCardFormatter : IMessagePackFormatter<RemoteCard?>
{
    public void Serialize(ref MessagePackWriter writer, RemoteCard? value, MessagePackSerializerOptions options)
    {
        if (value is null) {
            writer.WriteNil();
            return;
        }

        if (value is { Parent: null, Data: null, Num: 1 }) {
            writer.Write(((long)value.Uid << 1) | (byte)value.Type);
            return;
        }

        writer.WriteArrayHeader(5);
        writer.Write(value.Uid);
        writer.Write((byte)value.Type);
        Serialize(ref writer, value.Parent, options);
        options.Resolver.GetFormatterWithVerify<LZ4Bytes?>().Serialize(ref writer, value.Data, options);
        writer.Write(value.Num);
    }

    public RemoteCard? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) {
            return null;
        }

        if (reader.NextMessagePackType == MessagePackType.Integer) {
            var packed = reader.ReadInt64();
            return new() {
                Uid = checked((int)(packed >> 1)),
                Type = (RemoteCard.CardType)(packed & 1),
                Num = 1,
            };
        }

        options.Security.DepthStep(ref reader);
        try {
            var count = reader.ReadArrayHeader();
            int uid = 0, num = 0;
            var type = RemoteCard.CardType.Thing;
            RemoteCard? parent = null;
            LZ4Bytes? data = null;
            for (var i = 0; i < count; i++) {
                switch (i) {
                    case 0:
                        uid = reader.ReadInt32();
                        break;
                    case 1:
                        type = (RemoteCard.CardType)reader.ReadByte();
                        break;
                    case 2:
                        parent = Deserialize(ref reader, options);
                        break;
                    case 3:
                        data = options.Resolver.GetFormatterWithVerify<LZ4Bytes?>().Deserialize(ref reader, options);
                        break;
                    case 4:
                        num = reader.ReadInt32();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return new() {
                Uid = uid,
                Type = type,
                Parent = parent,
                Data = data,
                Num = num,
            };
        } finally {
            reader.Depth--;
        }
    }
}
