using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Client → Host: signal that client accepts validation mismatches and wants to proceed with joining.
/// </summary>
[MessagePackObject]
public class SourceValidationContinue
{
}