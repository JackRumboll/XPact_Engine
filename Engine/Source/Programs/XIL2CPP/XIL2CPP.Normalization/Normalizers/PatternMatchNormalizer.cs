// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer that lowers C# pattern matching
/// (<c>is</c>-patterns, <c>switch</c> expressions, and <c>switch</c>
/// statements with patterns) into an explicit decision representation Pass 6
/// can emit as the nested <c>if</c>/<c>switch</c> chains described in
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotate, do not rewrite.</b> Per the Section 3.1/3.2 hybrid
/// annotation-layer design this normalizer never mutates the Pass-1 trees.
/// Each pattern-bearing node (<see cref="IsPatternExpressionSyntax"/>,
/// <see cref="SwitchExpressionSyntax"/>, <see cref="SwitchStatementSyntax"/>)
/// receives one <see cref="PatternMatchAnnotation"/> carrying the lowered
/// <see cref="PatternDecision"/> tree (plus, for the two switch forms, the
/// per-arm decision list). The original Roslyn binding info stays
/// authoritative for Pass 3.
/// </para>
/// <para>
/// <b>Decision tree shape (Section 5.6 per-pattern emit rules).</b> Each
/// Roslyn <see cref="PatternSyntax"/> maps to one concrete
/// <see cref="PatternDecision"/>:
/// <list type="bullet">
///   <item><description>type pattern -> <see cref="TypePatternDecision"/> (a runtime cast + optional binding local).</description></item>
///   <item><description>declaration / recursive pattern with a <c>{ ... }</c> property sub-clause -> <see cref="PropertyPatternDecision"/> (per-property getter + sub-decision).</description></item>
///   <item><description>recursive pattern with a positional <c>( ... )</c> sub-clause -> <see cref="PositionalPatternDecision"/> (per-element deconstruction + sub-decision).</description></item>
///   <item><description>list pattern -> <see cref="ListPatternDecision"/> (length check + per-element sub-decisions + optional slice).</description></item>
///   <item><description>relational pattern -> <see cref="RelationalPatternDecision"/> (a comparison operator + literal).</description></item>
///   <item><description><c>and</c>/<c>or</c>/<c>not</c> -> <see cref="LogicalPatternDecision"/> over sub-decisions.</description></item>
///   <item><description>constant pattern -> <see cref="ConstantPatternDecision"/> (an equality compare).</description></item>
///   <item><description><c>var</c> pattern -> <see cref="VarPatternDecision"/> (assign a local, always true).</description></item>
///   <item><description>discard <c>_</c> -> <see cref="DiscardPatternDecision"/> (always true, no emit).</description></item>
/// </list>
/// A <c>when</c> guard wraps its arm's decision in a
/// <see cref="WhenGuardDecision"/>.
/// </para>
/// <para>
/// <b>Determinism.</b> Trees are walked in Pass-1 canonical file order then
/// document order; switch arms and sub-patterns are recorded in source
/// order. No ambient state.
/// </para>
/// </remarks>
public sealed class PatternMatchNormalizer : INormalizer
{
    /// <inheritdoc />
    public string Name => "PatternMatchNormalizer";

    /// <inheritdoc />
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(builder);

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case IsPatternExpressionSyntax isPattern:
                        AnnotateIsPattern(isPattern, model, builder);
                        break;

                    case SwitchExpressionSyntax switchExpr:
                        AnnotateSwitchExpression(switchExpr, model, builder);
                        break;

                    case SwitchStatementSyntax switchStmt:
                        AnnotateSwitchStatement(switchStmt, model, builder);
                        break;
                }
            }
        }
    }

    private static void AnnotateIsPattern(
        IsPatternExpressionSyntax node,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        PatternDecision decision = LowerPattern(node.Pattern, model);
        builder.AnnotateNode(
            node,
            new PatternMatchAnnotation(PatternConstructKind.IsPattern, decision));
    }

    private static void AnnotateSwitchExpression(
        SwitchExpressionSyntax node,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        List<SwitchArmDecision> arms = new(node.Arms.Count);
        foreach (SwitchExpressionArmSyntax arm in node.Arms)
        {
            PatternDecision decision = LowerPattern(arm.Pattern, model);
            bool hasWhen = arm.WhenClause is not null;
            if (hasWhen)
            {
                decision = new WhenGuardDecision(decision);
            }
            arms.Add(new SwitchArmDecision(decision, hasWhen));
        }

        builder.AnnotateNode(
            node,
            new PatternMatchAnnotation(PatternConstructKind.SwitchExpression, arms));
    }

    private static void AnnotateSwitchStatement(
        SwitchStatementSyntax node,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        List<SwitchArmDecision> arms = new();
        foreach (SwitchSectionSyntax section in node.Sections)
        {
            foreach (SwitchLabelSyntax label in section.Labels)
            {
                switch (label)
                {
                    case CasePatternSwitchLabelSyntax patternLabel:
                        PatternDecision decision = LowerPattern(patternLabel.Pattern, model);
                        if (patternLabel.WhenClause is not null)
                        {
                            decision = new WhenGuardDecision(decision);
                        }
                        arms.Add(new SwitchArmDecision(
                            decision, patternLabel.WhenClause is not null));
                        break;

                    case CaseSwitchLabelSyntax constantLabel:
                        // A classic 'case <constant>:' is a constant pattern in
                        // the decision model (Section 5.6: 'obj is 5' -> equality).
                        arms.Add(new SwitchArmDecision(
                            new ConstantPatternDecision(constantLabel.Value.ToString()),
                            false));
                        break;

                    case DefaultSwitchLabelSyntax:
                        // 'default:' is the catch-all arm (discard semantics).
                        arms.Add(new SwitchArmDecision(
                            DiscardPatternDecision.Instance, false));
                        break;
                }
            }
        }

        builder.AnnotateNode(
            node,
            new PatternMatchAnnotation(PatternConstructKind.SwitchStatement, arms));
    }

    /// <summary>
    /// Lower a single Roslyn <see cref="PatternSyntax"/> into its
    /// <see cref="PatternDecision"/> per the Section 5.6 per-pattern emit
    /// rules. Never throws on well-formed input; an unrecognised pattern kind
    /// (forward-compat) maps to a discard so the cascade still terminates.
    /// </summary>
    private static PatternDecision LowerPattern(PatternSyntax pattern, SemanticModel model)
    {
        switch (pattern)
        {
            case ConstantPatternSyntax constant:
                return new ConstantPatternDecision(constant.Expression.ToString());

            case DeclarationPatternSyntax declaration:
                // 'obj is SomeType x' -> type pattern with a binding local.
                return new TypePatternDecision(
                    TypeDisplay(declaration.Type, model),
                    BindingName(declaration.Designation));

            case TypePatternSyntax typePattern:
                // 'obj is SomeType' (no binding) -> type pattern, no local.
                return new TypePatternDecision(
                    TypeDisplay(typePattern.Type, model),
                    bindingName: null);

            case RecursivePatternSyntax recursive:
                return LowerRecursivePattern(recursive, model);

            case ListPatternSyntax list:
                return LowerListPattern(list, model);

            case RelationalPatternSyntax relational:
                return new RelationalPatternDecision(
                    relational.OperatorToken.ValueText,
                    relational.Expression.ToString());

            case BinaryPatternSyntax binary:
                // 'a and b' / 'a or b'.
                return new LogicalPatternDecision(
                    binary.OperatorToken.IsKind(SyntaxKind.AndKeyword)
                        ? LogicalPatternOperator.And
                        : LogicalPatternOperator.Or,
                    new[]
                    {
                        LowerPattern(binary.Left, model),
                        LowerPattern(binary.Right, model),
                    });

            case UnaryPatternSyntax unary:
                // 'not p'.
                return new LogicalPatternDecision(
                    LogicalPatternOperator.Not,
                    new[] { LowerPattern(unary.Pattern, model) });

            case VarPatternSyntax varPattern:
                return new VarPatternDecision(BindingName(varPattern.Designation));

            case DiscardPatternSyntax:
                return DiscardPatternDecision.Instance;

            case ParenthesizedPatternSyntax parenthesized:
                // Parentheses only group; lower the inner pattern verbatim.
                return LowerPattern(parenthesized.Pattern, model);

            default:
                // Forward-compat: an unknown future pattern kind degrades to a
                // discard (always-true) rather than throwing on well-formed
                // input, keeping the cascade total.
                return DiscardPatternDecision.Instance;
        }
    }

    private static PatternDecision LowerRecursivePattern(
        RecursivePatternSyntax recursive,
        SemanticModel model)
    {
        // A recursive pattern may carry a type, a positional '( ... )' clause,
        // a property '{ ... }' clause, and a binding designation -- in any
        // combination. Compose the sub-decisions left-to-right (type first,
        // then positional, then property) under an 'and' when more than one is
        // present, matching the short-circuit cascade Section 5.6 emits.
        string? bindingName = BindingName(recursive.Designation);
        List<PatternDecision> parts = new();

        if (recursive.Type is { } type)
        {
            parts.Add(new TypePatternDecision(TypeDisplay(type, model), bindingName: null));
        }

        if (recursive.PositionalPatternClause is { } positional)
        {
            List<PatternDecision> elements = new(positional.Subpatterns.Count);
            foreach (SubpatternSyntax sub in positional.Subpatterns)
            {
                elements.Add(LowerPattern(sub.Pattern, model));
            }
            parts.Add(new PositionalPatternDecision(elements));
        }

        if (recursive.PropertyPatternClause is { } property)
        {
            List<PropertyPatternMember> members = new(property.Subpatterns.Count);
            foreach (SubpatternSyntax sub in property.Subpatterns)
            {
                string memberName = sub.ExpressionColon?.Expression.ToString()
                    ?? sub.NameColon?.Name.ToString()
                    ?? string.Empty;
                members.Add(new PropertyPatternMember(memberName, LowerPattern(sub.Pattern, model)));
            }
            parts.Add(new PropertyPatternDecision(members));
        }

        PatternDecision composed = parts.Count switch
        {
            0 => DiscardPatternDecision.Instance,
            1 => parts[0],
            _ => new LogicalPatternDecision(LogicalPatternOperator.And, parts),
        };

        // A binding designation on the recursive pattern itself ('o is C c')
        // captures the matched value; wrap so Pass 6 emits the local.
        if (bindingName is not null)
        {
            return new VarBindingPatternDecision(bindingName, composed);
        }
        return composed;
    }

    private static PatternDecision LowerListPattern(ListPatternSyntax list, SemanticModel model)
    {
        List<PatternDecision> elements = new(list.Patterns.Count);
        bool hasSlice = false;
        int slicePosition = -1;
        PatternDecision? sliceSubPattern = null;

        int index = 0;
        foreach (PatternSyntax element in list.Patterns)
        {
            if (element is SlicePatternSyntax slice)
            {
                hasSlice = true;
                slicePosition = index;
                sliceSubPattern = slice.Pattern is { } inner
                    ? LowerPattern(inner, model)
                    : null;
            }
            else
            {
                elements.Add(LowerPattern(element, model));
            }
            index++;
        }

        return new ListPatternDecision(elements, hasSlice, slicePosition, sliceSubPattern);
    }

    /// <summary>
    /// Render a pattern's target type with the fully-qualified display form
    /// the C++ emit cast (<c>::XCore::Reflect::Cast&lt;T&gt;</c>) keys on,
    /// falling back to the syntactic text when the type does not bind (an
    /// error type), so the lowering is total on well-formed input.
    /// </summary>
    private static string TypeDisplay(TypeSyntax type, SemanticModel model)
    {
        TypeInfo info = model.GetTypeInfo(type);
        ITypeSymbol? symbol = info.Type;
        if (symbol is not null && symbol.TypeKind != TypeKind.Error)
        {
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        return type.ToString();
    }

    /// <summary>
    /// Extract the binding local name from a pattern designation, or null for
    /// a discard / missing designation.
    /// </summary>
    private static string? BindingName(VariableDesignationSyntax? designation) => designation switch
    {
        SingleVariableDesignationSyntax single => single.Identifier.ValueText,
        _ => null,
    };
}

/// <summary>
/// The category of pattern-bearing construct a
/// <see cref="PatternMatchAnnotation"/> describes.
/// </summary>
public enum PatternConstructKind
{
    /// <summary>An <c>is</c>-pattern expression (<c>obj is Pattern</c>).</summary>
    IsPattern,

    /// <summary>A <c>switch</c> expression (<c>e switch { ... }</c>).</summary>
    SwitchExpression,

    /// <summary>A <c>switch</c> statement (<c>switch (e) { case ...: }</c>).</summary>
    SwitchStatement,
}

/// <summary>
/// The lowering decision a <see cref="PatternMatchNormalizer"/> records on a
/// pattern-bearing node. For an <c>is</c>-pattern it carries a single
/// <see cref="Decision"/>; for a switch form it carries the ordered
/// per-arm <see cref="Arms"/> (and <see cref="Decision"/> is the first arm's
/// decision, or null when the switch is empty).
/// </summary>
public sealed class PatternMatchAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct an annotation for a single-pattern construct (an
    /// <c>is</c>-pattern).
    /// </summary>
    /// <param name="construct">The construct category.</param>
    /// <param name="decision">The lowered decision. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="decision"/> is null.</exception>
    public PatternMatchAnnotation(PatternConstructKind construct, PatternDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Construct = construct;
        Decision = decision;
        Arms = Array.Empty<SwitchArmDecision>();
    }

    /// <summary>
    /// Construct an annotation for a multi-arm construct (a <c>switch</c>
    /// expression or statement).
    /// </summary>
    /// <param name="construct">The construct category.</param>
    /// <param name="arms">The ordered per-arm decisions. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="arms"/> is null.</exception>
    public PatternMatchAnnotation(PatternConstructKind construct, IReadOnlyList<SwitchArmDecision> arms)
    {
        ArgumentNullException.ThrowIfNull(arms);
        Construct = construct;
        Arms = arms;
        Decision = arms.Count > 0 ? arms[0].Decision : DiscardPatternDecision.Instance;
    }

    /// <inheritdoc />
    public override string Kind => "pattern-match";

    /// <summary>The category of pattern-bearing construct this describes.</summary>
    public PatternConstructKind Construct { get; }

    /// <summary>
    /// The root decision. For an <c>is</c>-pattern this is the pattern's
    /// decision; for a switch form it is the first arm's decision (a
    /// convenience; the full set is in <see cref="Arms"/>).
    /// </summary>
    public PatternDecision Decision { get; }

    /// <summary>
    /// The ordered per-arm decisions for a switch form, in source order.
    /// Empty for an <c>is</c>-pattern.
    /// </summary>
    public IReadOnlyList<SwitchArmDecision> Arms { get; }
}

/// <summary>
/// One arm of a lowered <c>switch</c> expression / statement: its (possibly
/// <c>when</c>-guarded) decision in source order.
/// </summary>
/// <param name="Decision">The arm's lowered decision (a <see cref="WhenGuardDecision"/> when guarded).</param>
/// <param name="HasWhenGuard">True iff the arm carried a <c>when</c> clause.</param>
public sealed record SwitchArmDecision(PatternDecision Decision, bool HasWhenGuard);

/// <summary>
/// The discriminated-union base for a lowered pattern. Each concrete subclass
/// is one node in the decision tree Pass 6 emits as a nested
/// <c>if</c>/<c>switch</c> chain per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 5.6.
/// </summary>
public abstract class PatternDecision
{
    /// <summary>
    /// A short, stable discriminator for this decision kind (used in
    /// diagnostics + tests; not a dispatch key). Implementations return a
    /// constant string.
    /// </summary>
    public abstract string DecisionKind { get; }
}

/// <summary>
/// A type pattern (<c>obj is SomeType x</c>): a runtime cast against
/// <see cref="TypeName"/> with an optional binding local. Section 5.6:
/// <c>(::XCore::Reflect::Cast&lt;SomeType&gt;(obj) != nullptr)</c> + assign.
/// </summary>
public sealed class TypePatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a type-pattern decision.
    /// </summary>
    /// <param name="typeName">The fully-qualified target type display name. Must not be null.</param>
    /// <param name="bindingName">The binding local name, or null when the pattern binds nothing.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="typeName"/> is null.</exception>
    public TypePatternDecision(string typeName, string? bindingName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        TypeName = typeName;
        BindingName = bindingName;
    }

    /// <summary>The fully-qualified target type display name.</summary>
    public string TypeName { get; }

    /// <summary>The binding local name, or null.</summary>
    public string? BindingName { get; }

    /// <inheritdoc />
    public override string DecisionKind => "type";
}

/// <summary>
/// A property pattern (<c>obj is { X: &gt; 0, Y: 5 }</c>): a per-property
/// getter call + per-property sub-decision per Section 5.6.
/// </summary>
public sealed class PropertyPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a property-pattern decision over its member sub-decisions.
    /// </summary>
    /// <param name="members">The per-property members, in source order. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="members"/> is null.</exception>
    public PropertyPatternDecision(IReadOnlyList<PropertyPatternMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        Members = members;
    }

    /// <summary>The per-property members, in source order.</summary>
    public IReadOnlyList<PropertyPatternMember> Members { get; }

    /// <inheritdoc />
    public override string DecisionKind => "property";
}

/// <summary>
/// One member of a <see cref="PropertyPatternDecision"/>: the property /
/// field (or extended <c>a.b</c> chain) name and the sub-decision its value
/// must satisfy.
/// </summary>
/// <param name="MemberName">The matched member name (or extended expression text).</param>
/// <param name="Decision">The sub-decision the member's value must satisfy.</param>
public sealed record PropertyPatternMember(string MemberName, PatternDecision Decision);

/// <summary>
/// A positional / deconstruction pattern (<c>p is (0, 0)</c>): per-element
/// deconstruction + per-element sub-decision. Modeled alongside the property
/// pattern as a recursive-pattern sub-form per Section 5.6.
/// </summary>
public sealed class PositionalPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a positional-pattern decision over its element sub-decisions.
    /// </summary>
    /// <param name="elements">The per-position element sub-decisions, in source order. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="elements"/> is null.</exception>
    public PositionalPatternDecision(IReadOnlyList<PatternDecision> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        Elements = elements;
    }

    /// <summary>The per-position element sub-decisions, in source order.</summary>
    public IReadOnlyList<PatternDecision> Elements { get; }

    /// <inheritdoc />
    public override string DecisionKind => "positional";
}

/// <summary>
/// A list pattern (<c>arr is [first, .., last]</c>): an element-count check +
/// per-element indexer sub-decisions, with an optional slice (<c>..</c>)
/// carrying its own sub-pattern (<c>.. var rest</c>), per Section 5.6.
/// </summary>
public sealed class ListPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a list-pattern decision.
    /// </summary>
    /// <param name="elements">The non-slice element sub-decisions, in source order. Must not be null.</param>
    /// <param name="hasSlice">True iff the list pattern contains a slice (<c>..</c>).</param>
    /// <param name="slicePosition">The index of the slice among the original elements, or -1 when none.</param>
    /// <param name="sliceSubPattern">The slice's sub-pattern (<c>.. var rest</c>), or null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="elements"/> is null.</exception>
    public ListPatternDecision(
        IReadOnlyList<PatternDecision> elements,
        bool hasSlice,
        int slicePosition,
        PatternDecision? sliceSubPattern)
    {
        ArgumentNullException.ThrowIfNull(elements);
        Elements = elements;
        HasSlice = hasSlice;
        SlicePosition = slicePosition;
        SliceSubPattern = sliceSubPattern;
    }

    /// <summary>The non-slice element sub-decisions, in source order.</summary>
    public IReadOnlyList<PatternDecision> Elements { get; }

    /// <summary>True iff the list pattern contains a slice (<c>..</c>).</summary>
    public bool HasSlice { get; }

    /// <summary>
    /// The index of the slice among the original elements, or -1 when there
    /// is no slice. Determines whether fixed elements anchor to the front
    /// (slice last) or the back (slice first).
    /// </summary>
    public int SlicePosition { get; }

    /// <summary>The slice's sub-pattern (<c>.. var rest</c>), or null.</summary>
    public PatternDecision? SliceSubPattern { get; }

    /// <inheritdoc />
    public override string DecisionKind => "list";
}

/// <summary>
/// A relational pattern (<c>obj is &gt; 0</c>): a comparison operator + a
/// literal operand. Section 5.6: <c>obj &gt; 0</c>.
/// </summary>
public sealed class RelationalPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a relational-pattern decision.
    /// </summary>
    /// <param name="operator">The comparison operator text (e.g. <c>&gt;</c>, <c>&lt;=</c>). Must not be null.</param>
    /// <param name="operand">The literal operand text. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="operator"/> or <paramref name="operand"/> is null.</exception>
    public RelationalPatternDecision(string @operator, string operand)
    {
        ArgumentNullException.ThrowIfNull(@operator);
        ArgumentNullException.ThrowIfNull(operand);
        Operator = @operator;
        Operand = operand;
    }

    /// <summary>The comparison operator text.</summary>
    public string Operator { get; }

    /// <summary>The literal operand text.</summary>
    public string Operand { get; }

    /// <inheritdoc />
    public override string DecisionKind => "relational";
}

/// <summary>
/// The logical combinator a <see cref="LogicalPatternDecision"/> applies.
/// </summary>
public enum LogicalPatternOperator
{
    /// <summary><c>and</c> -> short-circuit <c>&amp;&amp;</c>.</summary>
    And,

    /// <summary><c>or</c> -> short-circuit <c>||</c>.</summary>
    Or,

    /// <summary><c>not</c> -> <c>!</c>.</summary>
    Not,
}

/// <summary>
/// A logical pattern (<c>and</c>/<c>or</c>/<c>not</c>): a short-circuiting
/// combination of sub-decisions per Section 5.6. <c>not</c> has exactly one
/// operand; <c>and</c>/<c>or</c> have two or more.
/// </summary>
public sealed class LogicalPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a logical-pattern decision.
    /// </summary>
    /// <param name="operator">The combinator.</param>
    /// <param name="operands">The sub-decisions, in source order. Must not be null / empty.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="operands"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="operands"/> is empty.</exception>
    public LogicalPatternDecision(LogicalPatternOperator @operator, IReadOnlyList<PatternDecision> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        if (operands.Count == 0)
        {
            throw new ArgumentException("A logical pattern requires at least one operand.", nameof(operands));
        }
        Operator = @operator;
        Operands = operands;
    }

    /// <summary>The combinator.</summary>
    public LogicalPatternOperator Operator { get; }

    /// <summary>The sub-decisions, in source order.</summary>
    public IReadOnlyList<PatternDecision> Operands { get; }

    /// <inheritdoc />
    public override string DecisionKind => "logical";
}

/// <summary>
/// A constant pattern (<c>obj is 5</c> / <c>case "label":</c>): an equality
/// compare against <see cref="ConstantText"/>. Section 5.6: <c>obj == 5</c>.
/// </summary>
public sealed class ConstantPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a constant-pattern decision.
    /// </summary>
    /// <param name="constantText">The constant expression text (e.g. <c>5</c>, <c>"label"</c>, <c>null</c>). Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="constantText"/> is null.</exception>
    public ConstantPatternDecision(string constantText)
    {
        ArgumentNullException.ThrowIfNull(constantText);
        ConstantText = constantText;
    }

    /// <summary>The constant expression text.</summary>
    public string ConstantText { get; }

    /// <inheritdoc />
    public override string DecisionKind => "constant";
}

/// <summary>
/// A <c>var</c> pattern (<c>obj is var x</c>): assign the value to a new
/// local and always match. Section 5.6: assign + always-true.
/// </summary>
public sealed class VarPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a <c>var</c>-pattern decision.
    /// </summary>
    /// <param name="bindingName">The binding local name, or null for <c>var _</c>.</param>
    public VarPatternDecision(string? bindingName) => BindingName = bindingName;

    /// <summary>The binding local name, or null for a discard designation.</summary>
    public string? BindingName { get; }

    /// <inheritdoc />
    public override string DecisionKind => "var";
}

/// <summary>
/// A binding wrapper for a recursive pattern whose own designation captures
/// the matched value (<c>o is Shape s</c> / <c>o is { } s</c>): bind
/// <see cref="BindingName"/> to the scrutinee then evaluate
/// <see cref="Inner"/>.
/// </summary>
public sealed class VarBindingPatternDecision : PatternDecision
{
    /// <summary>
    /// Construct a binding wrapper.
    /// </summary>
    /// <param name="bindingName">The binding local name. Must not be null.</param>
    /// <param name="inner">The inner decision to evaluate after binding. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="bindingName"/> or <paramref name="inner"/> is null.</exception>
    public VarBindingPatternDecision(string bindingName, PatternDecision inner)
    {
        ArgumentNullException.ThrowIfNull(bindingName);
        ArgumentNullException.ThrowIfNull(inner);
        BindingName = bindingName;
        Inner = inner;
    }

    /// <summary>The binding local name.</summary>
    public string BindingName { get; }

    /// <summary>The inner decision evaluated after the binding.</summary>
    public PatternDecision Inner { get; }

    /// <inheritdoc />
    public override string DecisionKind => "var-binding";
}

/// <summary>
/// A discard pattern (<c>_</c>) or a catch-all <c>default:</c> label: no emit,
/// always true. Section 5.6: discard -> just <c>true</c>. A flyweight
/// singleton since it carries no state.
/// </summary>
public sealed class DiscardPatternDecision : PatternDecision
{
    /// <summary>The shared flyweight instance (the decision is stateless).</summary>
    public static DiscardPatternDecision Instance { get; } = new();

    private DiscardPatternDecision()
    {
    }

    /// <inheritdoc />
    public override string DecisionKind => "discard";
}

/// <summary>
/// A wrapper that augments an arm's pattern decision with the arm's
/// <c>when</c> guard. Section 5.6: each <c>when</c> clause becomes an
/// additional <c>if</c> guard after the pattern test. The guard expression
/// itself is bound by the still-authoritative Pass-1 semantic model, so this
/// wrapper only records that the additional guard exists around
/// <see cref="Inner"/>.
/// </summary>
public sealed class WhenGuardDecision : PatternDecision
{
    /// <summary>
    /// Construct a when-guard wrapper around an arm's pattern decision.
    /// </summary>
    /// <param name="inner">The arm's pattern decision the guard augments. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="inner"/> is null.</exception>
    public WhenGuardDecision(PatternDecision inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>The arm's pattern decision the <c>when</c> guard augments.</summary>
    public PatternDecision Inner { get; }

    /// <inheritdoc />
    public override string DecisionKind => "when-guard";
}
