// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// Body-lowering rule (WU-D1) for a C# local-variable declaration statement,
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3:
/// <c>var x = expr;</c> lowers to <c>auto x = &lt;expr&gt;;</c> and
/// <c>int x = 5;</c> lowers to <c>int32_t x = 5;</c> (the explicit-type form
/// emits the resolved C++ type spelling). One C# declarator per emitted C++
/// line; the initializer (when present) is lowered by recursing into the parent
/// expression emitter so this rule never re-implements expression lowering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Type spelling.</b> A <c>var</c> declaration emits <c>auto</c> (Roslyn /
/// the doc Section 5.3 example). An explicit-type declaration emits the C++
/// spelling resolved from the declarator's bound type: the C# primitive types
/// map to their fixed-width <c>&lt;cstdint&gt;</c> forms
/// (<c>int</c> to <c>int32_t</c>, <c>char</c> to <c>char16_t</c>, ... per the
/// doc's cross-arch-bit-exact mapping), and any other resolved type falls back
/// to its bound name. The richer reference / container / struct type spellings
/// (<c>XObject*</c>, <c>XPtr&lt;T&gt;</c>, value structs) are the province of the
/// later class / member emitters; this rule emits the primitive mapping
/// precisely and the resolved name otherwise.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Declarators are emitted in
/// the source's syntactic order; type spelling is a pure switch over the bound
/// <see cref="SpecialType"/>; no ambient state, <see cref="DateTime"/>,
/// <see cref="Guid"/>, or culture-sensitive formatting.
/// </para>
/// </remarks>
public sealed class LocalDeclarationLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "LocalDeclaration";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
        => node is LocalDeclarationStatementSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var declarationStatement = (LocalDeclarationStatementSyntax)node;
        VariableDeclarationSyntax declaration = declarationStatement.Declaration;

        SemanticModel model = context.GetSemanticModel(node.SyntaxTree);
        string typeSpelling = ResolveTypeSpelling(declaration, model);

        foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
        {
            // Lead-in: "<type> <name>" with no trailing newline, so the
            // initializer (lowered through the parent expression emitter) can be
            // appended mid-line and the whole declaration ends with ";\n".
            writer.Append(typeSpelling);
            writer.Append(" ");
            writer.Append(declarator.Identifier.ValueText);

            if (declarator.Initializer is { } initializer)
            {
                writer.Append(" = ");
                parent.Expressions.EmitExpression(initializer.Value);
            }

            writer.Append(";");
            writer.AppendLine();
        }
    }

    /// <summary>
    /// Resolve the C++ type spelling for a declaration: <c>auto</c> for a
    /// <c>var</c> declaration, else the C++ spelling of the declarator's bound
    /// type (the fixed-width <c>&lt;cstdint&gt;</c> primitive form where one
    /// exists, otherwise the resolved type's name).
    /// </summary>
    private static string ResolveTypeSpelling(VariableDeclarationSyntax declaration, SemanticModel model)
    {
        if (declaration.Type.IsVar)
        {
            return "auto";
        }

        ITypeSymbol? type = model.GetTypeInfo(declaration.Type).Type;
        return SpellCppType(type) ?? declaration.Type.ToString();
    }

    /// <summary>
    /// Map a bound C# type to its emitted C++ spelling, or null when the type
    /// is unresolved (the caller falls back to the syntactic type text). The
    /// C# primitive types map to their fixed-width <c>&lt;cstdint&gt;</c> forms
    /// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 + the <c>char</c>
    /// mapping note (Section 5.3, FIX-B-HIGH-23): <c>char</c> maps to
    /// <c>char16_t</c> (a 16-bit UTF-16 code unit, NOT the platform-variable
    /// <c>wchar_t</c>).
    /// </summary>
    /// <param name="type">The bound type symbol (may be null).</param>
    /// <returns>The C++ type spelling, or null when unresolved.</returns>
    internal static string? SpellCppType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        return type.SpecialType switch
        {
            SpecialType.System_Void => "void",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Char => "char16_t",
            SpecialType.System_SByte => "int8_t",
            SpecialType.System_Byte => "uint8_t",
            SpecialType.System_Int16 => "int16_t",
            SpecialType.System_UInt16 => "uint16_t",
            SpecialType.System_Int32 => "int32_t",
            SpecialType.System_UInt32 => "uint32_t",
            SpecialType.System_Int64 => "int64_t",
            SpecialType.System_UInt64 => "uint64_t",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            _ => type.Name,
        };
    }
}
