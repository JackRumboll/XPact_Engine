// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Orchestrates XIL2CPP Pass 2 (AST normalization) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: reflection-discover
/// every <see cref="INormalizer"/> in the <c>XIL2CPP.Normalization</c>
/// assembly, run them in a stable deterministic order against a shared
/// <see cref="NormalizedUnitBuilder"/>, then seal + return an immutable
/// <see cref="NormalizedUnit"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovery.</b> Mirrors <c>XIL2CPP.Entry.ToolModeRegistry</c>: scan the
/// production assembly's types for non-abstract classes implementing
/// <see cref="INormalizer"/> with a public parameterless constructor,
/// instantiate each via <see cref="Activator"/>, and sort the instances by
/// <see cref="INormalizer.Name"/> (ordinal) so the run order is identical
/// across machines (gates X-IL2CPP-MANGLE-DET / X-IL2CPP-CSPATH-DET).
/// Discovery is scoped to <em>this</em> assembly so a test-only fake
/// normalizer in <c>XIL2CPP.Tests</c> is never picked up as a production
/// normalizer.
/// </para>
/// <para>
/// <b>Zero-normalizer correctness.</b> The driver runs cleanly with ZERO
/// discovered normalizers, returning an empty-but-valid
/// <see cref="NormalizedUnit"/> that still exposes the Pass-1 result. This
/// lets the foundation build + pass tests before any concrete normalizer
/// lands.
/// </para>
/// </remarks>
public static class Pass2Driver
{
    /// <summary>
    /// Run Pass 2 over <paramref name="pass1"/>: discover + run every
    /// production normalizer, then seal the result.
    /// </summary>
    /// <param name="pass1">The Pass-1 result to normalize. Must not be null.</param>
    /// <returns>The sealed, immutable normalized unit.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="pass1"/> is null.</exception>
    public static NormalizedUnit Run(Pass1Result pass1)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        return Run(pass1, DiscoverNormalizers());
    }

    /// <summary>
    /// Run Pass 2 over <paramref name="pass1"/> with an explicit, already-
    /// ordered normalizer set. Used by <see cref="Run(Pass1Result)"/> and by
    /// tests that inject a deterministic set without reflection discovery.
    /// The supplied normalizers run in the order given.
    /// </summary>
    /// <param name="pass1">The Pass-1 result to normalize. Must not be null.</param>
    /// <param name="normalizers">The normalizers to run, in order. Must not be null.</param>
    /// <returns>The sealed, immutable normalized unit.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="pass1"/> or <paramref name="normalizers"/> is null.</exception>
    public static NormalizedUnit Run(Pass1Result pass1, IReadOnlyList<INormalizer> normalizers)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(normalizers);

        NormalizedUnitBuilder builder = new(pass1);
        foreach (INormalizer normalizer in normalizers)
        {
            normalizer.Normalize(pass1, builder);
        }
        return builder.Build(pass1);
    }

    /// <summary>
    /// Reflection-discover every production <see cref="INormalizer"/> in the
    /// <c>XIL2CPP.Normalization</c> assembly and return them sorted by
    /// <see cref="INormalizer.Name"/> (ordinal). Public so tests can assert
    /// the discovered set + ordering directly.
    /// </summary>
    /// <returns>The discovered normalizers, deterministically ordered by Name.</returns>
    public static IReadOnlyList<INormalizer> DiscoverNormalizers()
    {
        Assembly assembly = typeof(Pass2Driver).Assembly;

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

        List<INormalizer> instances = new();
        foreach (Type type in types)
        {
            if (type is null
                || !type.IsClass
                || type.IsAbstract
                || !typeof(INormalizer).IsAssignableFrom(type))
            {
                continue;
            }

            // Require a public parameterless constructor (mirrors the
            // ToolModeRegistry Activator contract).
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                Logger.Warning(
                    $"Type '{type.FullName}' implements INormalizer but has no public parameterless constructor; skipping.");
                continue;
            }

            INormalizer? instance;
            try
            {
                instance = Activator.CreateInstance(type) as INormalizer;
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    $"Failed to instantiate INormalizer '{type.FullName}': {ex.Message}; skipping.");
                continue;
            }

            if (instance is null)
            {
                Logger.Warning(
                    $"Activator.CreateInstance returned null for INormalizer '{type.FullName}'; skipping.");
                continue;
            }

            instances.Add(instance);
        }

        // Deterministic run order: sort by Name (ordinal). Two normalizers
        // sharing a Name is a programming error; OrderBy is stable so the
        // discovery order is the tiebreak, but the contract is unique Names.
        return instances
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
    }
}
