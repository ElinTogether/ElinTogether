using System.Collections.Generic;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SourceValidationRequest
{
    [Key(0)]
    public required List<string> SourceNames { get; init; }

    [Key(1)]
    public required List<string> FilePaths { get; init; }

    [Key(2)]
    public int ValidationFlags { get; init; }
}