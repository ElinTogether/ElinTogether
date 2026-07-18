using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SourceValidationMismatch
{
    public enum SourceMismatchType
    {
        MissingOnClient,
        ExtraOnClient,
        HashChanged,
    }

    [Key(0)]
    public required string Entry { get; init; }

    [Key(1)]
    public required SourceMismatchType MismatchType { get; init; }
}