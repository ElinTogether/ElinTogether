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

    // the original of the snapshot stub of the class being patched, found from the class's attributes. Not a
    // MethodBase parameter of the stub's transpiler: HarmonyX 2.10 does not hand it the original there (null or the
    // stub; 2026-11-02, every transpiler was reported missing, Without's included)
    private static MethodBase? _current;

    /// <summary>
    ///     What each stub applied while the mod was patched, for a check in the game: by original, the transpilers
    ///     and the methods the copy calls that it did not call before (proof the IL came out changed)
    /// </summary>
    internal static readonly Dictionary<string, List<string>> Applied = [];

    /// <summary>
    ///     Before the classes are patched (again): the copies are made anew
    /// </summary>
    internal static void Reset()
    {
        Held.Clear();
        Applied.Clear();
        _current = null;
    }

    /// <summary>
    ///     Right before a class is patched: the original its snapshot stub copies, if it has one (the stub's own
    ///     transpiler runs while the class is patched, With and Without apply to it)
    /// </summary>
    internal static void Begin(Type type)
    {
        _current = null;
        try {
            var stubs = AccessTools.GetDeclaredMethods(type).Where(m =>
                m.GetCustomAttributes(typeof(HarmonyReversePatch), false).OfType<HarmonyReversePatch>()
                    .Any(r => r.info.reversePatchType == HarmonyReversePatchType.Snapshot));
            if (!stubs.Any()) {
                return;
            }

            var target = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(type));
            _current = target.methodType == MethodType.Getter
                ? AccessTools.PropertyGetter(target.declaringType, target.methodName)
                : AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes);
        } catch (Exception ex) {
            EmpLog.Information(ex, "Snapshot stub of {PatchClass} not checked", type.FullName);
        }
    }

    /// <summary>
    ///     The transpilers the copy of the original lacks, applied to it (one the copy already holds is not applied twice)
    /// </summary>
    internal static IEnumerable<CodeInstruction> With(IEnumerable<CodeInstruction> instructions,
                                                      params Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>[] transpilers)
    {
        var held = _current is null ? null : Hold(_current);
        foreach (var transpiler in transpilers) {
            if (held?.Contains(Key(transpiler.Method)) is true) {
                continue;
            }

            // one that fails leaves the copy as it was: thrown here, it would cost the whole class its patches
            var before = instructions.ToList();
            try {
                instructions = transpiler(before).ToList();
                held?.Add(Key(transpiler.Method));
                if (_current is not null) {
                    var name = $"{transpiler.Method.DeclaringType?.Name}.{transpiler.Method.Name}";
                    var calls = Called(instructions).Except(Called(before)).ToList();
                    var where = $"{_current.DeclaringType?.Name}.{_current.Name}";
                    if (!Applied.TryGetValue(where, out var list)) {
                        Applied[where] = list = [];
                    }

                    list.Add($"{name} (+{string.Join(", ", calls)})");
                    EmpLog.Debug("Snapshot stub of {Original}: {Transpiler} applied, new calls {Calls}", where, name, calls);
                }
            } catch (Exception ex) {
                EmpLog.Warning(ex, "Snapshot stub: {Transpiler} not applied", $"{transpiler.Method.DeclaringType?.Name}.{transpiler.Method.Name}");
                instructions = before;
            }
        }

        return instructions;
    }

    private static IEnumerable<string> Called(IEnumerable<CodeInstruction> instructions)
    {
        return instructions.Select(c => c.operand).OfType<MethodBase>().Select(m => $"{m.DeclaringType?.Name}.{m.Name}").Distinct();
    }

    /// <summary>
    ///     A transpiler the copy must run without (said where the stub is), not reported as missing
    /// </summary>
    internal static void Without(Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transpiler)
    {
        if (_current is not null) {
            Hold(_current).Add(Key(transpiler.Method));
        }
    }

    /// <summary>
    ///     Right after the class is patched: what its copy holds, when the stub did not apply anything itself
    /// </summary>
    internal static void Note()
    {
        if (_current is not null) {
            Hold(_current);
        }

        _current = null;
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
            EmpLog.Information(ex, "Snapshot stubs not checked");
        }
    }
}
