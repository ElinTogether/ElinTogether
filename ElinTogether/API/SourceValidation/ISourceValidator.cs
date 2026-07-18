using System.Collections.Generic;
using ElinTogether.Models;

namespace ElinTogether.API.SourceValidation;

public interface ISourceValidator
{
    public string Category { get; }

    public bool TryValidate(Dictionary<string, string> validation, out Dictionary<string, SourceValidationMismatch> mismatches);

    public Dictionary<string, string> GetValidation();
}