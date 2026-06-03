// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// Orchestrates XIL2CPP Pass 3 (semantic analysis + validation) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: reflection-discover
/// every <see cref="ISemanticAnalyzer"/> in the <c>XIL2CPP.Analysis</c>
/// assembly, run them sequentially in a stable deterministic order against a
/// shared <see cref="Pass3ResultBuilder"/>, then seal + return an immutable
/// <see cref="Pass3Result"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovery.</b> Mirrors <c>XIL2CPP.Entry.ToolModeRegistry</c> /
/// <see cref="Normalization.Pass2Driver"/>: scan the production assembly's
/// types for non-abstract classes implementing
/// <see cref="ISemanticAnalyzer"/> with a public parameterless constructor,
/// instantiate each via <see cref="Activator"/>, and sort the instances by
/// <see cref="ISemanticAnalyzer.Name"/> (ordinal) so the run order is
/// identical across machines (gates X-IL2CPP-MANGLE-DET /
/// X-IL2CPP-CSPATH-DET). Discovery is scoped to <em>this</em> assembly so a
/// test-only fake analyzer in <c>XIL2CPP.Tests</c> is never picked up as a
/// production analyzer.
/// </para>
/// <para>
/// <b>Zero-analyzer correctness.</b> The driver runs cleanly with ZERO
/// discovered analyzers, returning an empty-but-valid
/// <see cref="Pass3Result"/>. This lets the foundation build + pass tests
/// before any concrete analyzer lands.
/// </para>
/// </remarks>
public static class Pass3Driver
{
    /// <summary>
    /// Run Pass 3 over <paramref name="unit"/>: discover + run every
    /// production analyzer, then seal the result.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit to analyze. Must not be null.</param>
    /// <returns>The sealed, immutable Pass-3 result.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> is null.</exception>
    public static Pass3Result Run(NormalizedUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return Run(unit, DiscoverAnalyzers());
    }

    /// <summary>
    /// Run Pass 3 over <paramref name="unit"/> with an explicit, already-
    /// ordered analyzer set. Used by <see cref="Run(NormalizedUnit)"/> and by
    /// tests that inject a deterministic set without reflection discovery.
    /// The supplied analyzers run in the order given.
    /// </summary>
    /// <param name="unit">The Pass-2 normalized unit to analyze. Must not be null.</param>
    /// <param name="analyzers">The analyzers to run, in order. Must not be null.</param>
    /// <returns>The sealed, immutable Pass-3 result.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="unit"/> or <paramref name="analyzers"/> is null.</exception>
    public static Pass3Result Run(NormalizedUnit unit, IReadOnlyList<ISemanticAnalyzer> analyzers)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(analyzers);

        Pass3ResultBuilder builder = new(unit);
        foreach (ISemanticAnalyzer analyzer in analyzers)
        {
            analyzer.Analyze(unit, builder);
        }
        return builder.Build(unit);
    }

    /// <summary>
    /// Reflection-discover every production <see cref="ISemanticAnalyzer"/>
    /// in the <c>XIL2CPP.Analysis</c> assembly and return them sorted by
    /// <see cref="ISemanticAnalyzer.Name"/> (ordinal). Public so tests can
    /// assert the discovered set + ordering directly.
    /// </summary>
    /// <returns>The discovered analyzers, deterministically ordered by Name.</returns>
    public static IReadOnlyList<ISemanticAnalyzer> DiscoverAnalyzers()
    {
        Assembly assembly = typeof(Pass3Driver).Assembly;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Resilient against partially-loaded assemblies; proceed with
            // the types that did load (mirrors ToolModeRegistry).
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        List<ISemanticAnalyzer> instances = new();
        foreach (Type type in types)
        {
            if (type is null
                || !type.IsClass
                || type.IsAbstract
                || !typeof(ISemanticAnalyzer).IsAssignableFrom(type))
            {
                continue;
            }

            // Require a public parameterless constructor (mirrors the
            // ToolModeRegistry Activator contract).
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                Logger.Warning(
                    $"Type '{type.FullName}' implements ISemanticAnalyzer but has no public parameterless constructor; skipping.");
                continue;
            }

            ISemanticAnalyzer? instance;
            try
            {
                instance = Activator.CreateInstance(type) as ISemanticAnalyzer;
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    $"Failed to instantiate ISemanticAnalyzer '{type.FullName}': {ex.Message}; skipping.");
                continue;
            }

            if (instance is null)
            {
                Logger.Warning(
                    $"Activator.CreateInstance returned null for ISemanticAnalyzer '{type.FullName}'; skipping.");
                continue;
            }

            instances.Add(instance);
        }

        // Deterministic run order: sort by Name (ordinal). Two analyzers
        // sharing a Name is a programming error; OrderBy is stable so the
        // discovery order is the tiebreak, but the contract is unique Names.
        return instances
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }
}
