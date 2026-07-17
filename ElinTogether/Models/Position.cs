using System;
using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class Position : IEquatable<Position>
{
    [Key(0)]
    public required int X { get; init; }

    [Key(1)]
    public required int Z { get; init; }

    [return: NotNullIfNotNull("point")]
    public static implicit operator Position?(Point? point)
    {
        return point is null ? null : new() { X = point.x, Z = point.z };
    }

    [return: NotNullIfNotNull("position")]
    public static implicit operator Point?(Position? position)
    {
        return position is null ? null : new(position.X, position.Z);
    }

    public static bool operator ==(Position? lhs, Position? rhs)
    {
        if (lhs is null) {
            return rhs is null;
        }

        return lhs.Equals(rhs);
    }

    public static bool operator !=(Position? lhs, Position? rhs)
    {
        return !(lhs == rhs);
    }

    public static bool operator ==(Position? lhs, Point? rhs)
    {
        if (lhs is null || rhs is null) {
            return lhs is null && rhs is null;
        }

        return lhs.X == rhs.x && lhs.Z == rhs.z;
    }

    public static bool operator !=(Position? lhs, Point? rhs)
    {
        return !(lhs == rhs);
    }

    public static bool operator ==(Point? lhs, Position? rhs)
    {
        return rhs == lhs;
    }

    public static bool operator !=(Point? lhs, Position? rhs)
    {
        return !(lhs == rhs);
    }

    public bool Equals(Position? other)
    {
        if (other is null) {
            return false;
        }

        if (ReferenceEquals(this, other)) {
            return true;
        }

        return X == other.X && Z == other.Z;
    }

    public override bool Equals(object? other)
    {
        return other switch {
            Position pos => Equals(pos),
            Point point => X == point.x && Z == point.z,
            _ => false,
        };
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Z);
    }

    public override string ToString()
    {
        return $"({X}/{Z})";
    }
}