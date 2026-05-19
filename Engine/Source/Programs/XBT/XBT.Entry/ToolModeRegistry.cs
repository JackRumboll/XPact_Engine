// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// Registry of <see cref="XBTModeAttribute"/>-marked mode classes
/// discovered via reflection at startup. Cached after first build so
/// repeated CLI invocations in the same process do not re-scan.
/// </summary>
internal static class ToolModeRegistry
{
    /// <summary>Information about one registered mode.</summary>
    public sealed record ModeInfo(
        string Name,
        string Description,
        Type ModeType,
        Func<string[], CancellationToken, Task<int>> Invoker);

    private static IReadOnlyDictionary<string, ModeInfo>? s_modes;
    private static readonly object s_gate = new();

    /// <summary>Force discovery and return the registered mode set.</summary>
    public static IReadOnlyDictionary<string, ModeInfo> Build()
    {
        if (s_modes is { } cached)
        {
            return cached;
        }
        lock (s_gate)
        {
            if (s_modes is { } cached2)
            {
                return cached2;
            }
            s_modes = Discover();
            return s_modes;
        }
    }

    /// <summary>Look up a mode by case-insensitive name.</summary>
    public static ModeInfo? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        IReadOnlyDictionary<string, ModeInfo> modes = Build();
        return modes.TryGetValue(name, out ModeInfo? info) ? info : null;
    }

    private static IReadOnlyDictionary<string, ModeInfo> Discover()
    {
        Dictionary<string, ModeInfo> map = new(StringComparer.OrdinalIgnoreCase);

        // Scan every assembly currently loaded. XBT.Entry has the
        // built-in modes; out-of-tree additions can be registered by
        // loading their assembly before invocation. The set is finite
        // (Phase 1 ships ~4 modes), so the cost is negligible.
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Resilient against partially-loaded assemblies; we
                // proceed with the types that did load.
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (Type type in types)
            {
                if (type is null || !type.IsClass || type.IsAbstract)
                {
                    continue;
                }

                XBTModeAttribute? attr = type.GetCustomAttribute<XBTModeAttribute>();
                if (attr is null)
                {
                    continue;
                }

                ModeInfo info = BuildModeInfo(type, attr);
                if (map.ContainsKey(info.Name))
                {
                    Logger.Warning(
                        $"Duplicate XBT mode name '{info.Name}' on {type.FullName}; " +
                        $"keeping the first-registered class '{map[info.Name].ModeType.FullName}'.");
                    continue;
                }
                map.Add(info.Name, info);
            }
        }

        return map;
    }

    private static ModeInfo BuildModeInfo(Type type, XBTModeAttribute attr)
    {
        // Read the static `Description` property if present; fall back
        // to the attribute name. We use reflection rather than the
        // static-abstract interface dispatch to keep the discovery
        // resilient against modes that don't implement the interface
        // yet (Phase 1.2 will tighten this).
        string description = ReadStaticString(type, nameof(IToolMode<HelpMode>.Description)) ?? attr.Name;
        Func<string[], CancellationToken, Task<int>> invoker = BuildInvoker(type);
        return new ModeInfo(attr.Name, description, type, invoker);
    }

    private static string? ReadStaticString(Type type, string propertyName)
    {
        PropertyInfo? prop = type.GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Static);
        if (prop is null || prop.PropertyType != typeof(string))
        {
            return null;
        }
        try
        {
            return prop.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static Func<string[], CancellationToken, Task<int>> BuildInvoker(Type type)
    {
        // Mode classes have a parameterless constructor; we invoke
        // ExecuteAsync via reflection. The interface uses static-abstract
        // members for metadata but the per-call dispatch must go through
        // an instance method because mode state (per-invocation flags,
        // accumulators) is instance-scoped. The cost of reflection here
        // is one Activator.CreateInstance + one MethodInfo.Invoke per
        // XBT process invocation -- negligible.
        MethodInfo? exec = type.GetMethod(
            nameof(IToolMode<HelpMode>.ExecuteAsync),
            BindingFlags.Public | BindingFlags.Instance,
            new[] { typeof(string[]), typeof(CancellationToken) });

        if (exec is null)
        {
            return (_, _) =>
            {
                Logger.Error(
                    $"Mode '{type.FullName}' is marked [XBTMode] but does not declare " +
                    $"ExecuteAsync(string[], CancellationToken). Likely an implementation bug.");
                return Task.FromResult(1);
            };
        }

        return async (args, ct) =>
        {
            object? instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to instantiate mode '{type.FullName}': {ex.Message}");
                return 1;
            }
            if (instance is null)
            {
                Logger.Error($"Activator.CreateInstance returned null for '{type.FullName}'.");
                return 1;
            }

            object? result = exec.Invoke(instance, new object[] { args, ct });
            if (result is Task<int> typedTask)
            {
                return await typedTask.ConfigureAwait(false);
            }
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                return 0;
            }
            return 1;
        };
    }
}
