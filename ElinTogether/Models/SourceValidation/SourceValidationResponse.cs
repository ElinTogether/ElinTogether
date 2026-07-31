using System.Collections.Generic;
using ElinTogether.API.SourceValidation;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SourceValidationResponse
{
    [Key(0)]
    public required Dictionary<string, string> SourceHashes { get; init; }

    [Key(1)]
    public required Dictionary<string, string> PluginHashes { get; init; }

    [Key(2)]
    public required Dictionary<string, string> FileHashes { get; init; }

    [Key(3)]
    public required Dictionary<string, string> ActMapping { get; init; }

    public static SourceValidationResponse Create(SourceValidationRequest request)
    {
        var flags = (ValidationFlags)request.ValidationFlags;

        return new() {
            SourceHashes = SourceDataValidator.Default.GetValidation(request.SourceNames),
            PluginHashes = flags.HasFlag(ValidationFlags.Plugins)
                ? PluginDataValidator.Default.GetValidation()
                : [],
            FileHashes = new FileDataValidator(request.FilePaths).GetValidation(),
            ActMapping = ActMappingValidator.Default.GetValidation(),
        };
    }
}