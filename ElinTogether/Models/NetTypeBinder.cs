using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ElinTogether.Models;

internal sealed class NetTypeBinder : DefaultSerializationBinder
{
    internal static readonly NetTypeBinder Instance = new();

    private static readonly HashSet<Type> _plainTypes = [
        typeof(object), typeof(string), typeof(decimal), typeof(DateTime), typeof(TimeSpan), typeof(Guid),
        typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(Queue<>), typeof(Stack<>),
        typeof(LinkedList<>), typeof(SortedList<,>), typeof(SortedDictionary<,>), typeof(SortedSet<>),
        typeof(KeyValuePair<,>), typeof(Nullable<>),
        typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Vector2Int), typeof(Vector3Int),
        typeof(Color), typeof(Color32), typeof(Quaternion), typeof(Rect),
    ];

    private readonly Dictionary<(string?, string), Type> _resolved = [];

    public override Type BindToType(string? assemblyName, string typeName)
    {
        if (_resolved.TryGetValue((assemblyName, typeName), out var type)) {
            return type;
        }

        var resolved = assemblyName is null || IsLoaded(assemblyName.Split(',')[0].Trim())
            ? base.BindToType(assemblyName!, typeName)
            : null;
        if (resolved is null || !IsAllowed(resolved)) {
            EmpLog.Warning("Rejected remote type {TypeName} from assembly {AssemblyName}",
                typeName, assemblyName);
            throw new JsonSerializationException($"Type {typeName} is not allowed in net payloads");
        }

        return _resolved[(assemblyName, typeName)] = resolved;
    }

    private static bool IsLoaded(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name);
    }

    private static bool IsAllowed(Type type)
    {
        while (true) {
            if (type.IsArray) {
                type = type.GetElementType()!;
                continue;
            }

            if (type is { IsGenericType: true, IsGenericTypeDefinition: false }) {
                return IsAllowed(type.GetGenericTypeDefinition()) && type.GetGenericArguments().All(IsAllowed);
            }

            if (type.IsPrimitive || type.IsEnum || _plainTypes.Contains(type)) {
                return true;
            }

            if (typeof(Object).IsAssignableFrom(type)) {
                return false;
            }

            var assembly = type.Assembly;
            var name = assembly.GetName().Name;
            return name == "Elin" || name.StartsWith("Plugins") || ModManager.ListPluginObject.Any(p => p.GetType().Assembly == assembly);
        }
    }
}