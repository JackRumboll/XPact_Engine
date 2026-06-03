// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// The reflection-discovered registry of <see cref="IBodyLoweringRule"/>
/// implementations the <see cref="StatementEmitter"/> /
/// <see cref="ExpressionEmitter"/> dispatch to, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (Pass 6). Mirrors the
/// <see cref="Analysis.Pass3Driver"/> /
/// <see cref="Normalization.Pass2Driver"/> discovery pattern: scan the
/// <c>XIL2CPP.Emit</c> assembly for non-abstract
/// <see cref="IBodyLoweringRule"/> classes with a public parameterless
/// constructor, instantiate each, and sort by <see cref="IBodyLoweringRule.Name"/>
/// (ordinal) so dispatch order is identical across machines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero-rule correctness.</b> The registry works with ZERO discovered
/// rules: <see cref="FindRule"/> returns null for every node, and the
/// emitters fall back to a <c>// TODO(6.e): &lt;NodeKind&gt; not yet lowered</c>
/// comment. This lets the foundation build + pass tests BEFORE any concrete
/// rule lands.
/// </para>
/// <para>
/// <b>Test injection.</b> The reflection-discovery constructor is the
/// production path; the explicit-set constructor lets tests inject a
/// deterministic rule set (including a test-only fake rule) without scanning
/// the assembly.
/// </para>
/// </remarks>
public sealed class BodyLoweringRuleRegistry
{
    private readonly IReadOnlyList<IBodyLoweringRule> _rules;

    /// <summary>
    /// Construct a registry over an explicit, already-instantiated rule set.
    /// The rules are sorted by <see cref="IBodyLoweringRule.Name"/> (ordinal)
    /// so dispatch order is deterministic regardless of the caller's order.
    /// </summary>
    /// <param name="rules">The rules to register. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="rules"/> is null.</exception>
    public BodyLoweringRuleRegistry(IEnumerable<IBodyLoweringRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The registered rules, in deterministic <see cref="IBodyLoweringRule.Name"/>
    /// (ordinal) order. Never null; possibly empty.
    /// </summary>
    public IReadOnlyList<IBodyLoweringRule> Rules => _rules;

    /// <summary>
    /// Build a registry by reflection-discovering every production
    /// <see cref="IBodyLoweringRule"/> in the <c>XIL2CPP.Emit</c> assembly.
    /// </summary>
    /// <returns>The discovered registry (possibly empty).</returns>
    public static BodyLoweringRuleRegistry Discover()
        => new(DiscoverRules());

    /// <summary>
    /// Find the first rule (in <see cref="IBodyLoweringRule.Name"/> order)
    /// whose <see cref="IBodyLoweringRule.CanHandle"/> accepts
    /// <paramref name="node"/>, or null when no rule handles it.
    /// </summary>
    /// <param name="node">The candidate syntax node. Must not be null.</param>
    /// <returns>The matching rule, or null.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public IBodyLoweringRule? FindRule(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        foreach (IBodyLoweringRule rule in _rules)
        {
            if (rule.CanHandle(node))
            {
                return rule;
            }
        }
        return null;
    }

    /// <summary>
    /// Reflection-discover every production <see cref="IBodyLoweringRule"/> in
    /// the <c>XIL2CPP.Emit</c> assembly and return them sorted by
    /// <see cref="IBodyLoweringRule.Name"/> (ordinal). Public so tests can
    /// assert the discovered set + ordering directly. Discovery is scoped to
    /// THIS assembly so a test-only fake rule in <c>XIL2CPP.Tests</c> is never
    /// picked up as a production rule.
    /// </summary>
    /// <returns>The discovered rules, deterministically ordered by Name.</returns>
    public static IReadOnlyList<IBodyLoweringRule> DiscoverRules()
    {
        Assembly assembly = typeof(BodyLoweringRuleRegistry).Assembly;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Resilient against partially-loaded assemblies; proceed with the
            // types that did load (mirrors Pass3Driver / ToolModeRegistry).
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        List<IBodyLoweringRule> instances = new();
        foreach (Type type in types)
        {
            if (type is null
                || !type.IsClass
                || type.IsAbstract
                || !typeof(IBodyLoweringRule).IsAssignableFrom(type))
            {
                continue;
            }

            // Require a public parameterless constructor (mirrors the
            // Pass3Driver / ToolModeRegistry Activator contract).
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                Logger.Warning(
                    $"Type '{type.FullName}' implements IBodyLoweringRule but has no public parameterless constructor; skipping.");
                continue;
            }

            IBodyLoweringRule? instance;
            try
            {
                instance = Activator.CreateInstance(type) as IBodyLoweringRule;
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    $"Failed to instantiate IBodyLoweringRule '{type.FullName}': {ex.Message}; skipping.");
                continue;
            }

            if (instance is null)
            {
                Logger.Warning(
                    $"Activator.CreateInstance returned null for IBodyLoweringRule '{type.FullName}'; skipping.");
                continue;
            }

            instances.Add(instance);
        }

        // Deterministic dispatch order: sort by Name (ordinal). Two rules
        // sharing a Name is a programming error; OrderBy is stable so the
        // discovery order is the tiebreak, but the contract is unique Names.
        return instances
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }
}
