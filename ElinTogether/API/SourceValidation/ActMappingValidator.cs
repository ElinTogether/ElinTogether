using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;

namespace ElinTogether.API.SourceValidation;

public class ActMappingValidator : ISourceValidator
{
    public readonly Dictionary<Type, int> ActToIdMapping = [];

    public readonly Dictionary<int, Type> IdToActMapping = [];

    private ActMappingValidator()
    {
    }

    public static ActMappingValidator Default => field ??= new();

    public string Category => "act";

    public bool TryValidate(
        Dictionary<string, string> clientMapping,
        out Dictionary<string, SourceValidationMismatch> mismatches)
    {
        mismatches = [];
        var hostMapping = GetValidation();
        var hasMismatch = false;

        foreach (var (typeName, hostId) in hostMapping) {
            if (!clientMapping.TryGetValue(typeName, out var clientId)) {
                mismatches[typeName] = new() {
                    Entry = typeName,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.MissingOnClient,
                };
                hasMismatch = true;
            } else if (clientId != hostId) {
                mismatches[typeName] = new() {
                    Entry = typeName,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.HashChanged,
                };
                hasMismatch = true;
            }
        }

        foreach (var (typeName, _) in clientMapping) {
            if (!hostMapping.ContainsKey(typeName)) {
                mismatches[typeName] = new() {
                    Entry = typeName,
                    MismatchType = SourceValidationMismatch.SourceMismatchType.ExtraOnClient,
                };
                hasMismatch = true;
            }
        }

        return !hasMismatch;
    }

    public Dictionary<string, string> GetValidation()
    {
        return IdToActMapping.ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value.FullName!);
    }

    public void BuildActMapping()
    {
        ActToIdMapping.Clear();
        IdToActMapping.Clear();

        var actType = typeof(Act);
        var allActs = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try {
                    return a.GetTypes();
                } catch {
                    return [];
                }
            })
            .Where(actType.IsAssignableFrom)
            .OrderBy(GetInheritanceDepth)
            .ThenBy(t => t.Name);

        // keep NoGoal as 0 for bit checking
        IdToActMapping[0] = typeof(NoGoal);
        ActToIdMapping[typeof(NoGoal)] = 0;

        var actIndex = 1;
        foreach (var act in allActs) {
            if (!ActToIdMapping.TryAdd(act, actIndex)) {
                continue;
            }

            IdToActMapping[actIndex] = act;
            actIndex++;
        }

        return;

        static int GetInheritanceDepth(Type t)
        {
            var depth = 0;
            while (t.BaseType != null) {
                depth++;
                t = t.BaseType;
            }

            return depth;
        }
    }
}