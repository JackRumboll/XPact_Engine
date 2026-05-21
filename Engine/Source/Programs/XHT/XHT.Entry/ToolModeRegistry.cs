// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Entry;

/// <summary>
/// Registry of <see cref="XhtModeAttribute"/>-marked mode classes
/// discovered via reflection at startup. Cached after first build so
/// repeated CLI invocations in the same process do not re-scan. Mirrors
/// XBT's <c>ToolModeRegistry</c> pattern (XBT.html Section 1.1).
/// </summary>
public static class ToolModeRegistry
{
    private static IReadOnlyDictionary<string, IToolMode>? s_modes;
    private static readonly object s_gate = new();

    /// <summary>
    /// Discover modes once and return the cached set. Subsequent calls
    /// return the same dictionary.
    /// </summary>
    /// <returns>
    /// Read-only dictionary keyed on the case-insensitive mode name (the
    /// kebab-case form per <c>/Documents/XHT.html</c> Rev 8 Section 1).
    /// </returns>
    public static IReadOnlyDictionary<string, IToolMode> Build()
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

    /// <summary>
    /// Resolve a mode by case-insensitive name; returns null when no
    /// match is registered.
    /// </summary>
    /// <param name="name">Mode name as parsed from the CLI.</param>
    /// <returns>The mode instance, or null when no match exists.</returns>
    public static IToolMode? Resolve(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        IReadOnlyDictionary<string, IToolMode> modes = Build();
        return modes.TryGetValue(name, out IToolMode? mode) ? mode : null;
    }

    /// <summary>
    /// Reset the cached registry. Used by tests that want a fresh scan
    /// (e.g., when verifying registration semantics in isolation).
    /// </summary>
    internal static void __ResetForTesting()
    {
        lock (s_gate)
        {
            s_modes = null;
        }
    }

    /// <summary>
    /// Inject a synthetic mode into the registry for the duration of a
    /// test. Round 7 R6-XH1 added this so the XHT900 ICE-branch
    /// positive test can register a mode that throws the (internal)
    /// un-anchored <c>ManifestMalformedException</c>, run
    /// <c>Program.Main</c>, and assert the XHT900 stderr surface. Pair
    /// with <see cref="__ResetForTesting"/> in the test's teardown so
    /// the injection does not leak to other tests.
    /// </summary>
    /// <param name="name">Kebab-case mode name to register under.</param>
    /// <param name="mode">The mode instance.</param>
    internal static void __RegisterForTesting(string name, IToolMode mode)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new System.ArgumentException("Mode name cannot be empty.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(mode);

        lock (s_gate)
        {
            // Build with the existing reflection-discovered set, then
            // overlay the injected mode. The injected entry wins on a
            // name collision so tests can override built-in modes when
            // exercising the entry-point catch surface.
            Dictionary<string, IToolMode> map = new(
                s_modes ?? Discover(),
                StringComparer.OrdinalIgnoreCase);
            map[name] = mode;
            s_modes = map;
        }
    }

    private static IReadOnlyDictionary<string, IToolMode> Discover()
    {
        Dictionary<string, IToolMode> map = new(StringComparer.OrdinalIgnoreCase);

        // Scan every assembly currently loaded. XHT.Entry has the built-
        // in modes; the set is finite (Phase 1 mode count <= 10) so the
        // cost is negligible.
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

                XhtModeAttribute? attr = type.GetCustomAttribute<XhtModeAttribute>();
                if (attr is null)
                {
                    continue;
                }

                if (!typeof(IToolMode).IsAssignableFrom(type))
                {
                    Logger.Warning(
                        $"Type '{type.FullName}' carries [XhtMode(\"{attr.Name}\")] but does not implement IToolMode; skipping.");
                    continue;
                }

                IToolMode? instance;
                try
                {
                    instance = Activator.CreateInstance(type) as IToolMode;
                }
                catch (Exception ex)
                {
                    Logger.Warning(
                        $"Failed to instantiate XHT mode '{type.FullName}': {ex.Message}; skipping.");
                    continue;
                }

                if (instance is null)
                {
                    Logger.Warning(
                        $"Activator.CreateInstance returned null for XHT mode '{type.FullName}'; skipping.");
                    continue;
                }

                if (map.ContainsKey(attr.Name))
                {
                    Logger.Warning(
                        $"Duplicate XHT mode name '{attr.Name}' on {type.FullName}; "
                        + $"keeping the first-registered class '{map[attr.Name].GetType().FullName}'.");
                    continue;
                }
                map.Add(attr.Name, instance);
            }
        }

        return map;
    }
}
