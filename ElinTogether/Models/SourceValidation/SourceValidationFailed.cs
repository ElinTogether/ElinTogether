using System.Collections.Generic;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SourceValidationFailed
{
    [Key(0)]
    public List<SourceValidationMismatch> SourceMismatches { get; set; } = [];

    [Key(1)]
    public List<SourceValidationMismatch> PluginMismatches { get; set; } = [];

    [Key(2)]
    public List<SourceValidationMismatch> FileMismatches { get; set; } = [];

    [Key(3)]
    public List<SourceValidationMismatch> ActMismatches { get; set; } = [];
}