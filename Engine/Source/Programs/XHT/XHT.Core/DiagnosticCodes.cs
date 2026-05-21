// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Centralised catalog of every <c>XHT&lt;NNN&gt;</c> diagnostic code
/// XHT can emit, per <c>/Documents/XHT.html</c> Rev 8 Section 12.3
/// (band allocations) + Section 23.2 (catalog entries). Round 7 R6-XH2
/// pulled all in-line string literals into this class so a typo
/// (e.g., <c>"XTH001"</c> swapping H and T) fails to compile rather
/// than manifesting at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Band layout</b> (per XHT.html Section 12.3, Rev 4 X-Round2-M2
/// decimal-only convention):
/// <list type="bullet">
///   <item><description><b>XHT000-XHT009</b> -- Logger / entry-point infrastructure (Logger info/warning/error sentinel, manifest-not-found, CV mismatch, verifier rejection, module-not-in-manifest, .gen.manifest CV mismatch, .gen.manifest structural).</description></item>
///   <item><description><b>XHT040-XHT049</b> -- C# marker walker (duplicate-type, unsupported attribute shapes).</description></item>
///   <item><description><b>XHT060-XHT069</b> -- C++ tokenizer / specifier-parser / marker-scanner (lex + AST-shape diagnostics; XHT066/067/068 relocated here from the validator band per C8 audit, XHT.html Rev 8 Section 12.3).</description></item>
///   <item><description><b>XHT070-XHT099</b> -- Emit-band: source-file IO, missing files, gen.* write failures.</description></item>
///   <item><description><b>XHT100-XHT124</b> -- Validator-band: parser + resolver semantic diagnostics.</description></item>
///   <item><description><b>XHT130</b> -- Cancellation (operator Ctrl-C).</description></item>
///   <item><description><b>XHT143</b> -- Informational (partial-class merged).</description></item>
///   <item><description><b>XHT900</b> -- ICE-band: internal compiler error (un-anchored throw site).</description></item>
/// </list>
/// </para>
/// <para>
/// The parser-local constants (<c>CppTokenizer.DiagUnterminatedComment</c>
/// etc.) shadow the lexer-band entries here for callsite locality;
/// any future renumbering must update both. The resolver-band entries
/// were lifted from <c>XHT.Resolver.DiagnosticCodes</c> (deleted in
/// the R6-XH2 consolidation) so cross-band callers have a single
/// import.
/// </para>
/// </remarks>
public static class DiagnosticCodes
{
    // -----------------------------------------------------------------
    // Logger / infrastructure band (XHT000-XHT009).
    // -----------------------------------------------------------------

    /// <summary>XHT000 -- Logger sentinel for info / warning / error lines without a catalog anchor.</summary>
    public const string LoggerSentinel = "XHT000";

    /// <summary>XHT001 -- Manifest not found at the path the operator supplied (or that an .gen.manifest reader expected).</summary>
    public const string ManifestNotFound = "XHT001";

    /// <summary>XHT002 -- Manifest <c>ContractVersion</c> does not match XHT's compile-time pin.</summary>
    public const string ContractVersionMismatch = "XHT002";

    /// <summary>XHT003 -- Manifest payload rejected by the hardened JSON / FlatBuffers verifier (depth, size, schema).</summary>
    public const string ManifestVerifierRejection = "XHT003";

    /// <summary>XHT004 -- Module name supplied to <c>parse-module</c> / <c>emit-module</c> / <c>validate-only</c> is not present in the manifest. Remapped from 30 to 50 in the Rev 3 X-CR1 audit.</summary>
    public const string ModuleNotInManifest = "XHT004";

    /// <summary>XHT005 -- <c>.gen.manifest</c> Inputs / Generated section path contains a comma; commas are reserved as field separators in the line-oriented format.</summary>
    public const string GenManifestCommaInPath = "XHT005";

    /// <summary>XHT006 -- <c>.gen.manifest</c> ContractVersion does not match XHT's compile-time pin. The symmetric counterpart of XHT002 on the XHT-output side (R5-XHT-MA5).</summary>
    public const string GenManifestContractVersionMismatch = "XHT006";

    /// <summary>XHT007 -- <c>.gen.manifest</c> structural error (missing key, mis-ordered section, malformed hash, missing [End] marker, etc.).</summary>
    public const string GenManifestStructural = "XHT007";

    // -----------------------------------------------------------------
    // C# marker walker band (XHT040-XHT049).
    // -----------------------------------------------------------------

    /// <summary>XHT040 -- Duplicate type definition (same engine name encountered twice during the parser pass).</summary>
    public const string DuplicateType = "XHT040";

    /// <summary>XHT044 -- Generic attribute shape is not supported (Phase 1 walker limitation).</summary>
    public const string GenericAttributeUnsupported = "XHT044";

    /// <summary>XHT045 -- Duplicate marker attribute on a single declaration (e.g., two XCLASS attributes).</summary>
    public const string DuplicateMarkerAttribute = "XHT045";

    // -----------------------------------------------------------------
    // C++ tokenizer / specifier-parser band (XHT060-XHT069).
    // -----------------------------------------------------------------

    /// <summary>XHT060 -- Unterminated block comment (<c>/* ... EOF</c>).</summary>
    public const string UnterminatedComment = "XHT060";

    /// <summary>XHT061 -- Unterminated string / character / raw-string literal.</summary>
    public const string UnterminatedString = "XHT061";

    /// <summary>XHT062 -- Malformed numeric literal (e.g., <c>0x</c> with no hex digits). Re-used as the lexer-side numeric-literal diagnostic; do NOT confuse with <see cref="Simgenics.XPact.XHT.Core.ExitCodes.XhtInternalFailure"/> (also numeric 62) -- these are unrelated value spaces.</summary>
    public const string InvalidNumericLiteral = "XHT062";

    /// <summary>XHT063 -- Unsupported digraph / unknown punctuator at lex time.</summary>
    public const string UnsupportedPunctuator = "XHT063";

    /// <summary>XHT064 -- Deprecated specifier syntax using the pipe (<c>|</c>) form instead of the contract's comma list.</summary>
    public const string DeprecatedPipeSyntax = "XHT064";

    /// <summary>XHT065 -- Specifier-list syntax error (malformed key-value pair, unbalanced brackets, etc.).</summary>
    public const string SpecifierSyntaxError = "XHT065";

    /// <summary>
    /// XHT066 -- Specifier registered but not legal in the active
    /// <c>SpecifierContext</c> (e.g., <c>EditAnywhere</c> on a class).
    /// Per C8 audit renumbering (XHT.html Rev 8 Section 12.3):
    /// relocated from XHT111 to XHT066 to resolve a numeric-slot
    /// collision with the validator-band
    /// <see cref="FunctionSpecifierConflict"/> (Server+Client+NetMulticast
    /// mutex). Emitted by <c>CppSpecifierParser</c> +
    /// <c>CSharpSpecifierExtractor</c> during the registry-lookup-with-context
    /// step.
    /// </summary>
    public const string SpecifierIllegalInContext = "XHT066";

    /// <summary>
    /// XHT067 -- Reflection marker (XCLASS / XSTRUCT / XENUM / XFUNCTION
    /// / XPROPERTY / XDELEGATE) is followed by a declaration shape the
    /// scanner does not recognise (missing identifier, malformed enum
    /// header, empty XPROPERTY declaration, etc.). Per C8 audit
    /// renumbering (XHT.html Rev 8 Section 12.3): relocated from XHT115
    /// to XHT067 to resolve a numeric-slot collision with the
    /// validator-band <see cref="IntrinsicWithGeneratedBody"/>. Emitted
    /// by <c>CppMarkerScanner</c> + <c>CSharpMarkerWalker</c>.
    /// </summary>
    public const string MalformedMarkerDeclaration = "XHT067";

    /// <summary>
    /// XHT068 -- Reflection marker appeared in the wrong syntactic
    /// context (e.g., <c>XFUNCTION</c> outside a reflected class /
    /// struct / interface body). Per C8 audit renumbering (XHT.html Rev
    /// 8 Section 12.3): relocated from XHT116 to XHT068 to resolve a
    /// numeric-slot collision with the validator-band
    /// <see cref="MinimalApiWithRequiredApi"/>. Emitted by
    /// <c>CppMarkerScanner</c>.
    /// </summary>
    public const string MarkerContextError = "XHT068";

    // -----------------------------------------------------------------
    // Emit band (XHT070-XHT099).
    // -----------------------------------------------------------------

    /// <summary>XHT070 -- Emit warning: a source file referenced in the manifest could not be read in lenient mode (-Strict=false) and the emit continued with empty / sentinel content.</summary>
    public const string EmitSourceWarning = "XHT070";

    /// <summary>XHT072 -- Emit error: a source file referenced in the manifest could not be read in strict mode (-Strict=true). Surfaces under exit code <see cref="ExitCodes.ManifestMalformed"/>.</summary>
    public const string EmitSourceMissing = "XHT072";

    // -----------------------------------------------------------------
    // Validator band (XHT100-XHT124, XHT143). Lifted from
    // XHT.Resolver.DiagnosticCodes in the R6-XH2 consolidation so
    // every band lives in a single import.
    // -----------------------------------------------------------------

    /// <summary>XHT100 -- Interface declared without a paired class (orphan native interface).</summary>
    public const string InterfaceWithoutPairedClass = "XHT100";

    /// <summary>XHT101 -- Function declared outside a class / struct / interface (top-level XFUNCTION forbidden).</summary>
    public const string TopLevelFunctionForbidden = "XHT101";

    /// <summary>XHT102 -- Property declared outside a class / struct / interface (orphan reflected property).</summary>
    public const string OrphanProperty = "XHT102";

    /// <summary>XHT103 -- Super / base / interface / outer-type identifier could not be resolved via the symbol table.</summary>
    public const string SymbolNotFound = "XHT103";

    /// <summary>XHT104 -- Resolved super / base / outer entity is the wrong kind (e.g., a class extending a struct).</summary>
    public const string SuperWrongKind = "XHT104";

    /// <summary>XHT105 -- <c>TopologicalStructVisit</c> detected a cycle through the inheritance chain.</summary>
    public const string RecursiveStructCycle = "XHT105";

    /// <summary>XHT111 -- Mutually-exclusive function-side specifier combination (e.g., Server+Client+NetMulticast).</summary>
    public const string FunctionSpecifierConflict = "XHT111";

    /// <summary>XHT112 -- Mutually-exclusive property-side specifier combination (e.g., EditAnywhere+Transient).</summary>
    public const string PropertySpecifierConflict = "XHT112";

    /// <summary>XHT113 -- <c>ReplicatedUsing</c> callback signature mismatch or unresolved callback name.</summary>
    public const string RepNotifyInvalidSignature = "XHT113";

    /// <summary>XHT114 -- Class-level <c>NoExport + Config=</c> conflict per Section 7.4.</summary>
    public const string ConfigConflictsWithNoExport = "XHT114";

    /// <summary>XHT115 -- Class-level <c>Intrinsic + XGENERATED_BODY()</c> conflict per Section 7.4.</summary>
    public const string IntrinsicWithGeneratedBody = "XHT115";

    /// <summary>XHT116 -- Class-level <c>MinimalAPI + RequiredAPI</c> conflict per Section 7.4.</summary>
    public const string MinimalApiWithRequiredApi = "XHT116";

    /// <summary>XHT117 -- Class-level <c>NoExport + Blueprintable</c> conflict per Section 7.4.</summary>
    public const string NoExportWithBlueprintable = "XHT117";

    /// <summary>XHT119 -- Class <c>Within=</c> incompatible with super's <c>Within</c>; mirrors UHT <c>SetAndValidateWithinClass</c>.</summary>
    public const string ClassWithinIncompatibleWithSuper = "XHT119";

    /// <summary>XHT120 -- Cross-language pairing kind mismatch (e.g., C++ struct paired with C# class on the same engine name).</summary>
    public const string CrossLanguagePairingMismatch = "XHT120";

    /// <summary>XHT121 -- Type reference resolves only through a dynamic-only / interface-only module dependency; not link-visible.</summary>
    public const string DynamicOnlyModuleReference = "XHT121";

    /// <summary>XHT124 -- Manifest declares a mangling scheme this XHT build does not support (Round-2 audit C1).</summary>
    public const string UnsupportedManglingScheme = "XHT124";

    /// <summary>XHT143 -- Partial-class duplicate merged into the canonical entry (informational; no exit).</summary>
    public const string PartialClassMerged = "XHT143";

    // -----------------------------------------------------------------
    // ICE band (XHT900).
    // -----------------------------------------------------------------

    /// <summary>XHT900 -- Internal compiler error: an XHT-side bug surfaced through an un-anchored exception path. See <c>XHT.Entry.Program</c>'s catch surface.</summary>
    public const string InternalCompilerError = "XHT900";
}
