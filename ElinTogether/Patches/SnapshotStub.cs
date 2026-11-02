using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A Snapshot reverse patch (the Stub_* copies a delta replays the game's code with) holds only the transpilers
///     already on the original when its class is patched, and the classes are patched in assembly order: a transpiler
///     of a class that comes later is missing from the copy (the planter of a guest's finishing blow, CharaDieEvent).
///     A stub applies those itself through <see cref="With" />, and <see cref="ReportMissing" /> names, once the mod is
///     patched, every transpiler of an original that its copy still runs without
/// </summary>
internal static class SnapshotStub
{
    // by original: the transpilers its copy holds (those on it when the copy was made, those the stub applies itself,
    // and those left out on purpose)
    private static readonly Dictionary<(Module, int), (MethodBase Original, HashSet<(Module, int)> Transpilers)> Held = [];

    private static (Module, int) Key(MethodBase method)
    {
        return (method.Module, method.MetadataToken);
    }

    private static HashSet<(Module, int)> Hold(MethodBase original)
    {
        if (!Held.TryGetValue(Key(original), out var held)) {
            held = (original, ((IEnumerable<Patch>?)Harmony.GetPatchInfo(original)?.Transpilers ?? []).Select(t => Key(t.PatchMethod)).ToHashSet());
            Held[Key(original)] = held;
        }

        return held.Transpilers;
    }

    /// <summary>
    ///     Before the classes are patched (again): the copies are made anew
    /// </summary>
    internal static void Reset()
    {
        Held.Clear();
    }

    /// <summary>
    ///     The transpilers the copy of the original lacks, applied to it (one the copy already holds is not applied twice)
    /// </summary>
    internal static IEnumerable<CodeInstruction> With(IEnumerable<CodeInstruction> instructions,
                                                      MethodBase? original,
                                                      params Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>[] transpilers)
    {
        var held = original is null ? null : Hold(original);
        foreach (var transpiler in transpilers) {
            if (held?.Contains(Key(transpiler.Method)) is true) {
                continue;
            }

            // one that fails leaves the copy as it was: thrown here, it would cost the whole class its patches
            var before = instructions.ToList();
            try {
                instructions = transpiler(before).ToList();
                held?.Add(Key(transpiler.Method));
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Snapshot stub: {Transpiler} not applied", $"{transpiler.Method.DeclaringType?.Name}.{transpiler.Method.Name}");
                instructions = before;
            }
        }

        return instructions;
    }

    /// <summary>
    ///     A transpiler the copy must run without (said where the stub is), not reported as missing
    /// </summary>
    internal static void Without(MethodBase? original, Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transpiler)
    {
        if (original is not null) {
            Hold(original).Add(Key(transpiler.Method));
        }
    }

    /// <summary>
    ///     Right after a class is patched: what the copies it made hold, when the stub did not apply anything itself
    /// </summary>
    internal static void Note(Type type)
    {
        try {
            var stubs = AccessTools.GetDeclaredMethods(type).Where(m =>
                m.GetCustomAttributes(typeof(HarmonyReversePatch), false).OfType<HarmonyReversePatch>()
                    .Any(r => r.info.reversePatchType == HarmonyReversePatchType.Snapshot));
            if (!stubs.Any()) {
                return;
            }

            var target = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(type));
            var original = target.methodType == MethodType.Getter
                ? AccessTools.PropertyGetter(target.declaringType, target.methodName)
                : AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes);
            if (original is not null) {
                Hold(original);
            }
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Snapshot stub of {PatchClass} not checked", type.FullName);
        }
    }

    /// <summary>
    ///     Once every class is patched: a transpiler on an original that its copy runs without
    /// </summary>
    internal static void ReportMissing()
    {
        try {
            foreach (var (original, held) in Held.Values) {
                foreach (var transpiler in (IEnumerable<Patch>?)Harmony.GetPatchInfo(original)?.Transpilers ?? []) {
                    if (!held.Contains(Key(transpiler.PatchMethod))) {
                        EmpLog.Warning("Transpiler {Transpiler} ({Owner}) of {Original} is missing from its snapshot stub, which " +
                                       "replays the game's code without it: apply it in the stub (SnapshotStub.With)",
                            $"{transpiler.PatchMethod.DeclaringType?.FullName}.{transpiler.PatchMethod.Name}", transpiler.owner,
                            $"{original.DeclaringType?.Name}.{original.Name}");
                    }
                }
            }
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Snapshot stubs not checked");
        }
    }
}
