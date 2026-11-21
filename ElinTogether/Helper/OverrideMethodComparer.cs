using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EModding.Helper;
using EModding.Helper.Runtime;
using HarmonyLib;

namespace ElinTogether.Helper;

public struct OverrideMethodComparer : IEqualityComparer<MethodBase>
{
    public static OverrideMethodComparer Default { get; } = new();

    public bool Equals(MethodBase? lhs, MethodBase? rhs)
    {
        return ReferenceEquals(lhs, rhs) || lhs?.MetadataToken == rhs?.MetadataToken;
    }

    public int GetHashCode(MethodBase mi)
    {
        return mi.MetadataToken;
    }

    public static IEnumerable<MethodBase> FindAllOverrides(Type type, string methodName, params Type[] parameterTypes)
    {
        return Derived(type)
            .Select(t => Safe(() => t.GetRuntimeMethod(methodName, parameterTypes), t))
            .Where(mi => mi is not null && !mi.ContainsGenericParameters && mi.IsDeclaredMember())
            .Distinct(Default)!;
    }

    public static IEnumerable<MethodBase> FindAllOverridesGetter(Type type, string propertyName)
    {
        return FindAllOverrides(type, $"get_{propertyName}");
    }

    public static IEnumerable<MethodBase> FindAllOverridesCtor(Type type)
    {
        return Derived(type)
            .SelectMany(t => Safe(() => AccessTools.GetDeclaredConstructors(t), t) ?? [])
            .Distinct(Default);
    }

    // (issue 76: another mod's type that cannot load, e.g. a missing ModOptions assembly, threw out of the whole search
    // and the patch built on it was skipped: 13 patches, task progress, item use and quest syncs; such a type is now left out)
    private static IEnumerable<Type> Derived(Type type)
    {
        return type.Assembly.GetTypes()
            .Concat(TypeQualifier.Declared)
            .Where(t => Safe(() => type.IsAssignableFrom(t), t));
    }

    private static T? Safe<T>(Func<T> get, Type t)
    {
        try {
            return get();
        } catch (Exception ex) {
            string name;
            try {
                name = t.FullName;
            } catch {
                name = "?";
            }

            EmpLog.Debug(ex, "Type {Type} left out of an override search: it does not load", name);
            return default;
        }
    }
}