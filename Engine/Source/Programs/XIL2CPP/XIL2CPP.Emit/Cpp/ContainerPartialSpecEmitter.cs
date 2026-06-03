// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Analysis;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// XIL2CPP Pass-6 (WU-6F) module-level emitter for the C++ container
/// partial specializations per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 5.7 (container emit) + Section 6.2 (container roots: XGCRootSpan
/// emit). For every UNIQUE XGC-aware container instantiation the Pass-3
/// <see cref="ContainerAnalyzer"/> recorded (a <see cref="ContainerSite"/>),
/// it emits exactly one explicit C++ partial specialization of the matching
/// container template (<c>TArray&lt;XPtr&lt;T'&gt;&gt;</c> /
/// <c>TMap&lt;K', XPtr&lt;V'&gt;&gt;</c> / <c>TSet&lt;XPtr&lt;T'&gt;&gt;</c>)
/// carrying the GC scaffolding: an <c>XGCRootSpan</c> member, a constructor
/// that registers the span (<c>XGC_RegisterRootSpan</c>), a resize hook that
/// updates it (<c>XGC_UpdateRootSpan</c>), a destructor that unregisters it
/// (<c>XGC_UnregisterRootSpan</c>), deleted copy operations + defaulted move
/// operations (move-only per Contract Section 3.4), and the marker comments the
/// downstream tooling keys on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dedup by emit signature (Section 5.7 "one partial spec per closed
/// instantiation").</b> Two declaration sites with the IDENTICAL closed
/// instantiation (e.g. two fields both typed <c>List&lt;XActor&gt;</c>) share
/// ONE C++ partial specialization &#8211; emitting it twice would be an ODR
/// violation. The sites are deduplicated by their
/// <see cref="ContainerSite.EmitSignature"/> (the authoritative C++ template
/// spelling the Pass-3 analyzer computed), and the unique signatures are
/// emitted in ordinal order via a <see cref="SortedSet{T}"/> keyed by the
/// signature string. The GC kind (Strong / Conservative) is a pure function
/// of the slot types and is therefore identical for every site sharing a
/// signature, so the first-seen site's <see cref="ContainerSite.GcKind"/> is
/// authoritative for the deduplicated spec.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The emitter carries NO
/// ambient state: the unique-signature ordering is ordinal
/// (<see cref="StringComparer.Ordinal"/>), the per-spec emit is a pure
/// function of the signature + GC kind, and every newline is the
/// <see cref="CppWriter"/>'s explicit <c>'\n'</c>. Two runs over the same
/// site set produce byte-identical output.
/// </para>
/// <para>
/// <b>GC ABI (LOCKED).</b> The emitted register / update / unregister calls
/// match the <c>::XCore::Reflect::XGC_*RootSpan</c> ABI surface exactly:
/// <c>XGC_RegisterRootSpan(&amp;_rootSpan, GetData(), Num(), sizeof(elem),
/// kind)</c> in the constructor, <c>XGC_UpdateRootSpan(&amp;_rootSpan, base,
/// n, sizeof(elem))</c> in the resize hook, and
/// <c>XGC_UnregisterRootSpan(&amp;_rootSpan)</c> in the destructor. A
/// conservative site emits <c>XGCRootKind::Conservative</c> (with an
/// <c>// XIL2CPP070 conservative</c> marker) instead of
/// <c>XGCRootKind::Strong</c>.
/// </para>
/// </remarks>
public sealed class ContainerPartialSpecEmitter
{
    /// <summary>The C++ namespace the GC reflection runtime types live in.</summary>
    private const string ReflectNamespace = "::XCore::Reflect::";

    /// <summary>
    /// The (fully-qualified, but UNqualified-at-the-specialization-name)
    /// container template namespace the explicit specialization is wrapped in.
    /// A full explicit specialization is written INSIDE the primary template's
    /// own namespace using the UNqualified template name
    /// (<c>namespace XCore { namespace Container { template &lt;&gt; struct
    /// TArray&lt;...&gt; { ... }; } }</c>); spelling the specialization name with
    /// a leading global qualifier (<c>struct ::XCore::Container::TArray&lt;...&gt;</c>)
    /// is rejected by g++ ("global qualification of class name is invalid before
    /// '{'"), so the spec is emitted in-namespace instead. See FIX 1
    /// (XIL2CPP.html Rev 4 Section 5.7).
    /// </summary>
    private const string ContainerNamespaceOuter = "XCore";

    /// <summary>The inner container template namespace (see <see cref="ContainerNamespaceOuter"/>).</summary>
    private const string ContainerNamespaceInner = "Container";

    /// <summary>
    /// Emit the module's container partial specializations into
    /// <paramref name="writer"/>. Reads every <see cref="ContainerSite"/> the
    /// Pass-3 <see cref="ContainerAnalyzer"/> recorded from
    /// <paramref name="context"/>, deduplicates by
    /// <see cref="ContainerSite.EmitSignature"/>, and emits one partial
    /// specialization per unique signature (in ordinal order). When the module
    /// has no XGC-aware container sites, nothing is written.
    /// </summary>
    /// <param name="context">The per-module emit context (its Pass-3 result carries the container sites). Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="context"/> or <paramref name="writer"/> is null.</exception>
    public void Emit(EmitContext context, CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        IReadOnlyList<ContainerSite> sites = context.Pass3.GetAll<ContainerSite>();
        if (sites.Count == 0)
        {
            return;
        }

        // Dedup by emit signature (one C++ partial spec per closed
        // instantiation). The GC kind is a pure function of the signature, so
        // the first-seen site for a signature is authoritative; a SortedSet of
        // signatures gives the deterministic ordinal emit order.
        SortedSet<string> uniqueSignatures = new(StringComparer.Ordinal);
        Dictionary<string, ContainerSite> bySignature = new(StringComparer.Ordinal);
        foreach (ContainerSite site in sites)
        {
            if (uniqueSignatures.Add(site.EmitSignature))
            {
                bySignature.Add(site.EmitSignature, site);
            }
        }

        writer.AppendComment(
            "===== Container partial specializations (Section 5.7 / 6.2) =====");

        bool first = true;
        foreach (string signature in uniqueSignatures)
        {
            if (!first)
            {
                writer.AppendLine();
            }
            first = false;
            EmitSpec(writer, bySignature[signature]);
        }
    }

    /// <summary>
    /// Emit one container partial specialization for <paramref name="site"/>.
    /// </summary>
    private static void EmitSpec(CppWriter writer, ContainerSite site)
    {
        ParsedSignature parsed = ParseSignature(site.EmitSignature);

        // The closed template-id the specialization is OF, spelled with the
        // UNqualified template name (the spec is wrapped in the container
        // namespace below). A leading global qualifier on a specialization name
        // (`struct ::XCore::Container::TArray<...>`) is rejected by g++; emitting
        // the explicit specialization in-namespace with the unqualified name is
        // the well-formed form. See FIX 1.
        string specName = parsed.TemplateName + "<" + parsed.ArgumentList + ">";

        // The container's element-slot type: the (single) element for
        // TArray / TSet, the VALUE column for TMap (the GC-rooted column).
        string elementType = parsed.ElementSlot;

        bool conservative = site.GcKind == ContainerGcKind.Conservative;
        string rootKind = conservative
            ? ReflectNamespace + "XGCRootKind::Conservative"
            : ReflectNamespace + "XGCRootKind::Strong";

        writer.AppendComment(
            "Closed instantiation " + parsed.TemplateName + "<" + parsed.ArgumentList
            + "> for C# " + site.CSharpType + ".");
        writer.AppendComment("container = true");

        // Wrap the explicit specialization in the primary template's own
        // namespace so the specialization name stays UNqualified (no leading
        // global-scope `::`), which g++ accepts.
        writer.BeginBlock("namespace " + ContainerNamespaceOuter);
        writer.BeginBlock("namespace " + ContainerNamespaceInner);

        // The keyword matches the primary template (struct), so the explicit
        // specialization's class-key agrees with the primary declaration.
        writer.AppendLine("template <>");
        writer.BeginBlock("struct " + specName);

        writer.Unindent();
        writer.AppendLine("public:");
        writer.Indent();

        // GC root-span member (Section 6.2).
        writer.AppendComment("XGC root-span member (Section 6.2).");
        writer.AppendLine(ReflectNamespace + "XGCRootSpan _rootSpan;");
        writer.AppendLine();

        // Constructor: register the root span.
        if (conservative)
        {
            writer.AppendComment("XIL2CPP070 conservative");
        }
        writer.BeginBlock("explicit " + parsed.TemplateName + "(" + ReflectNamespace + "XObject* parent)");
        writer.AppendLine("(void)parent;");
        writer.AppendLine(
            ReflectNamespace + "XGC_RegisterRootSpan(&_rootSpan, GetData(), Num(), sizeof("
            + elementType + "), " + rootKind + ");");
        writer.EndBlock();
        writer.AppendLine();

        // Destructor: unregister the root span.
        writer.BeginBlock("~" + parsed.TemplateName + "()");
        writer.AppendLine(ReflectNamespace + "XGC_UnregisterRootSpan(&_rootSpan);");
        writer.EndBlock();
        writer.AppendLine();

        // Resize hook: update the root span when the backing store moves.
        writer.BeginBlock("void _OnRootSpanResize(void* base, size_t n)");
        writer.AppendLine(
            ReflectNamespace + "XGC_UpdateRootSpan(&_rootSpan, base, n, sizeof(" + elementType + "));");
        writer.EndBlock();
        writer.AppendLine();

        // Move-only per Contract Section 3.4: deleted copy, defaulted move.
        writer.AppendComment("Move-only per Contract Section 3.4: deleted copy, defaulted move.");
        writer.AppendLine(parsed.TemplateName + "(const " + parsed.TemplateName + "&) = delete;");
        writer.AppendLine(
            parsed.TemplateName + "& operator=(const " + parsed.TemplateName + "&) = delete;");
        writer.AppendLine(parsed.TemplateName + "(" + parsed.TemplateName + "&&) = default;");
        writer.AppendLine(
            parsed.TemplateName + "& operator=(" + parsed.TemplateName + "&&) = default;");

        writer.EndBlock(";");

        // Close `namespace Container` / `namespace XCore`.
        writer.EndBlock(" // namespace " + ContainerNamespaceInner);
        writer.EndBlock(" // namespace " + ContainerNamespaceOuter);
    }

    /// <summary>
    /// Parse a Pass-3 container emit signature (e.g.
    /// <c>TArray&lt;XPtr&lt;XActor&gt;&gt;</c>,
    /// <c>TMap&lt;FName, XPtr&lt;XActor&gt;&gt;</c>,
    /// <c>TSet&lt;void*&gt;</c>) into the template name, the full (verbatim)
    /// argument list, and the GC-rooted element slot (the single element for
    /// TArray / TSet; the value column for TMap).
    /// </summary>
    private static ParsedSignature ParseSignature(string emitSignature)
    {
        int open = emitSignature.IndexOf('<');
        if (open < 0 || !emitSignature.EndsWith(">", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Malformed container emit signature (no top-level '<...>'): '" + emitSignature + "'.");
        }

        string templateName = emitSignature[..open];
        string argumentList = emitSignature[(open + 1)..^1];

        // The element slot is the LAST top-level comma-separated argument: the
        // single element for TArray / TSet, the value column for TMap (the
        // GC-rooted column). Splitting respects nested angle brackets so a
        // nested XPtr<...> is not split on its own internal punctuation.
        string elementSlot = LastTopLevelArgument(argumentList);

        return new ParsedSignature(templateName, argumentList, elementSlot);
    }

    /// <summary>
    /// Return the last top-level comma-separated argument of
    /// <paramref name="argumentList"/>, tracking angle-bracket nesting so a
    /// comma inside a nested template-id (none arise today, but the parser
    /// stays robust) does not split the argument. The result is trimmed of
    /// surrounding whitespace.
    /// </summary>
    private static string LastTopLevelArgument(string argumentList)
    {
        int depth = 0;
        int lastSplit = -1;
        for (int i = 0; i < argumentList.Length; i++)
        {
            char c = argumentList[i];
            switch (c)
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    lastSplit = i;
                    break;
            }
        }

        string last = argumentList[(lastSplit + 1)..];
        return last.Trim();
    }

    /// <summary>
    /// The parsed parts of a container emit signature: the template name
    /// (<c>TArray</c> / <c>TMap</c> / <c>TSet</c>), the verbatim
    /// angle-bracketed argument list, and the GC-rooted element slot.
    /// </summary>
    private readonly record struct ParsedSignature(
        string TemplateName,
        string ArgumentList,
        string ElementSlot);
}
