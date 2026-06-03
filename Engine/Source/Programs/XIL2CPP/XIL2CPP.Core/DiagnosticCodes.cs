// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Core;

/// <summary>
/// Centralised catalog of the <c>XIL2CPP&lt;NNN&gt;</c> diagnostic codes
/// XIL2CPP emits, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12.
/// Pulling code strings into named constants means a typo (e.g.,
/// <c>"XLI2CPP001"</c> transposing I and L) fails to compile rather than
/// manifesting at runtime, matching the XHT.Core.DiagnosticCodes
/// discipline.
/// </summary>
/// <remarks>
/// <para>
/// The full Section 12 catalog spans XIL2CPP001-XIL2CPP200. Phase 6.a
/// anchored the infrastructure + Pass-1 codes; Phase 6.b (Pass 2 AST
/// normalization + Pass 3 semantic analysis) adds the Locked-Commitment-3,
/// banned-feature, tier-classification, sim-path, reflection, boxing,
/// container, Span, lifecycle, LINQ, generic-instantiation, ABI / manifest,
/// cross-system, and forward-commitment bands the normalizers + analyzers
/// surface. Later sub-phases (6.c tier classification, 6.e+ emit) reuse
/// these constants; the constants must always match the numeric allocations
/// in Section 12.
/// </para>
/// <para>
/// Band layout (per XIL2CPP.html Section 12):
/// <list type="bullet">
///   <item><description><b>XIL2CPP000</b> -- logger sentinel (info / warning / error lines without a catalog anchor); the <c>000-009</c> band header is "Locked Commitment 3 violations" but the <c>000</c> slot itself is unallocated, so it serves as the un-anchored sentinel exactly as XHT000 does for XHT.</description></item>
///   <item><description><b>XIL2CPP001-009</b> -- Locked Commitment 3 (XObject factory) violations.</description></item>
///   <item><description><b>XIL2CPP010-019</b> -- unsupported / banned C# features.</description></item>
///   <item><description><b>XIL2CPP020-029</b> -- language version / target framework / type constraints.</description></item>
///   <item><description><b>XIL2CPP030-039</b> -- tier classification + hot-reload signature changes.</description></item>
///   <item><description><b>XIL2CPP040-049, 059</b> -- sim-path banned APIs.</description></item>
///   <item><description><b>XIL2CPP050-058</b> -- reflection runtime.</description></item>
///   <item><description><b>XIL2CPP060-064</b> -- pattern matching / boxing / foreach over IEnumerable.</description></item>
///   <item><description><b>XIL2CPP070-073</b> -- GC / container scaffolding.</description></item>
///   <item><description><b>XIL2CPP080-086</b> -- Span / unsafe / params allocation.</description></item>
///   <item><description><b>XIL2CPP090-097</b> -- object lifecycle + struct layout.</description></item>
///   <item><description><b>XIL2CPP100</b> -- LINQ / collection processing.</description></item>
///   <item><description><b>XIL2CPP123-129</b> -- generic instantiation.</description></item>
///   <item><description><b>XIL2CPP140-151</b> -- ABI / manifest mismatch + cross-system contract.</description></item>
///   <item><description><b>XIL2CPP170-187</b> -- forward-commitment fallbacks.</description></item>
///   <item><description><b>XIL2CPP200</b> -- hot-reload + module unload (runtime band).</description></item>
///   <item><description><b>XIL2CPP900</b> -- ICE band: internal compiler error surfaced through an un-anchored exception path (above the Section 12 catalog's 200 ceiling; matches the XHT900 convention).</description></item>
/// </list>
/// </para>
/// </remarks>
public static class DiagnosticCodes
{
    // -----------------------------------------------------------------
    // Logger / infrastructure.
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP000 -- logger sentinel for info / warning / error lines without a catalog anchor.</summary>
    public const string LoggerSentinel = "XIL2CPP000";

    // -----------------------------------------------------------------
    // Locked Commitment 3 violations (XObject factory) (XIL2CPP001-009).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP001 -- new-expression on XObject-derived type; use XObject.New&lt;T&gt;(outer, name, flags). Section 2.3, 5.3.</summary>
    public const string NewExpressionOnXObjectDerived = "XIL2CPP001";

    /// <summary>XIL2CPP002 -- XObject.New&lt;T&gt; called with null Outer. Section 6.4.</summary>
    public const string XObjectNewNullOuter = "XIL2CPP002";

    /// <summary>XIL2CPP003 -- XObject.New&lt;T&gt; T must be a non-abstract XObject-derived type. Section 2.3, 5.3.</summary>
    public const string XObjectNewTypeMustBeConcrete = "XIL2CPP003";

    /// <summary>XIL2CPP005 -- ref/out parameter of XObject-reference type is not supported in MVP. Section 5.2, 6.3.</summary>
    public const string RefOutXObjectParameterUnsupported = "XIL2CPP005";

    // -----------------------------------------------------------------
    // Unsupported / banned C# features (XIL2CPP010-019).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP010 -- BCL type not in mapped subset. Section 1.2.</summary>
    public const string BclTypeNotInMappedSubset = "XIL2CPP010";

    /// <summary>XIL2CPP011 -- 'dynamic' is not supported; XIL2CPP is a static transpiler. Section 5.20.</summary>
    public const string DynamicNotSupported = "XIL2CPP011";

    /// <summary>XIL2CPP012 -- P/Invoke (DllImport) is not supported. Section 5.20.</summary>
    public const string PInvokeNotSupported = "XIL2CPP012";

    /// <summary>XIL2CPP013 -- direct thread creation is banned. Section 5.20.</summary>
    public const string DirectThreadCreationBanned = "XIL2CPP013";

    /// <summary>XIL2CPP014 -- direct I/O is not in the BCL surface. Section 5.20.</summary>
    public const string DirectIoNotInBclSurface = "XIL2CPP014";

    /// <summary>XIL2CPP015 -- params ReadOnlySpan&lt;T&gt; is post-MVP. Section 5.2.</summary>
    public const string ParamsReadOnlySpanPostMvp = "XIL2CPP015";

    /// <summary>XIL2CPP016 -- static abstract interface members are post-MVP. Section 5.1.</summary>
    public const string StaticAbstractInterfaceMembersPostMvp = "XIL2CPP016";

    /// <summary>XIL2CPP017 -- class primary-constructor parameter captured into method body; XClass-reflectable types should use explicit fields. Section 5.4.</summary>
    public const string PrimaryConstructorParameterCaptured = "XIL2CPP017";

    /// <summary>XIL2CPP018 -- call site to unbodied partial method has side-effecting arguments. Section 5.1.</summary>
    public const string UnbodiedPartialMethodSideEffectingArgs = "XIL2CPP018";

    /// <summary>XIL2CPP019 -- volatile field type has incompatible alignment for std::atomic on target platform. Section 5.4.</summary>
    public const string VolatileFieldAlignmentIncompatible = "XIL2CPP019";

    // -----------------------------------------------------------------
    // Language version / target framework / type constraints (XIL2CPP020-029).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP020 -- Module declares LangVersion higher than MVP-supported C# 12. Section 2.2.</summary>
    public const string LangVersionTooHigh = "XIL2CPP020";

    /// <summary>XIL2CPP021 -- Module targets a .NET version higher than MVP-supported .NET 8. Section 2.2.</summary>
    public const string TargetFrameworkTooHigh = "XIL2CPP021";

    /// <summary>XIL2CPP026 -- variance-based assignment not supported in this context; use explicit .Cast&lt;Base&gt;(). Section 5.8.</summary>
    public const string VarianceAssignmentUnsupported = "XIL2CPP026";

    /// <summary>XIL2CPP028 -- type contains XObject reference; cannot satisfy 'where U : unmanaged' constraint. Section 5.8.</summary>
    public const string XObjectReferenceViolatesUnmanagedConstraint = "XIL2CPP028";

    // -----------------------------------------------------------------
    // Tier classification + hot-reload signature changes (XIL2CPP030-039).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP030 -- [XFunction(NoThrow = true)] proof failed; includes the failing callee chain. Section 3.3.</summary>
    public const string NoThrowProofFailed = "XIL2CPP030";

    /// <summary>XIL2CPP031 -- function conservatively Tier 1 due to unresolved cross-module callee. Section 3.3.</summary>
    public const string ConservativeTier1UnresolvedCallee = "XIL2CPP031";

    /// <summary>XIL2CPP032 -- Tier 2 function attempting to throw. Section 5.12.</summary>
    public const string Tier2FunctionThrows = "XIL2CPP032";

    /// <summary>XIL2CPP033 -- exception filter relies on non-unwinding semantics that differ from C++ catch+rethrow. Section 5.12.</summary>
    public const string ExceptionFilterNonUnwinding = "XIL2CPP033";

    /// <summary>XIL2CPP034 -- generic constraint change on existing method; signature breakage forbidden by hot-reload commitment. Section 8.2.</summary>
    public const string GenericConstraintChangeForbidden = "XIL2CPP034";

    /// <summary>XIL2CPP035 -- Tier 2 to Tier 1 demotion across hot-reload baseline. Section 3.3, 8.</summary>
    public const string Tier2ToTier1DemotionAcrossHotReload = "XIL2CPP035";

    /// <summary>XIL2CPP036 -- NoThrow lookup failed; function has no XHT-emitted reflection entry. Section 3.3, 10.2.</summary>
    public const string NoThrowLookupFailed = "XIL2CPP036";

    /// <summary>XIL2CPP039 -- [XOnClassReplaced] method has wrong signature. Section 8.4.</summary>
    public const string OnClassReplacedWrongSignature = "XIL2CPP039";

    // -----------------------------------------------------------------
    // Sim-path banned APIs (XIL2CPP040-049, 059).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP040 -- Sim-path TU calls banned API &lt;FullName&gt;. Section 7.4.</summary>
    public const string SimPathBannedApiCall = "XIL2CPP040";

    /// <summary>XIL2CPP041 -- foreach over HashSet&lt;T&gt; or Dictionary&lt;K,V&gt; has non-deterministic iteration order (error on sim-path, warning otherwise). Section 5.3, 7.4.</summary>
    public const string NonDeterministicIterationOrder = "XIL2CPP041";

    /// <summary>XIL2CPP042 -- Sim-path string index s[i] is non-deterministic at the UTF-8 boundary. Section 5.5.</summary>
    public const string SimPathStringIndexNonDeterministic = "XIL2CPP042";

    /// <summary>XIL2CPP043 -- lock(obj) is banned on sim-path TUs. Section 5.10.</summary>
    public const string SimPathLockBanned = "XIL2CPP043";

    /// <summary>XIL2CPP044 -- async/await is banned on sim-path TUs. Section 5.9, 7.5.</summary>
    public const string SimPathAsyncAwaitBanned = "XIL2CPP044";

    /// <summary>XIL2CPP045 -- Sim-path reads XObject.SerialNumber. Section 7.3.</summary>
    public const string SimPathReadsSerialNumber = "XIL2CPP045";

    /// <summary>XIL2CPP046 -- Sim-path hashes XObjectKey. Section 7.3.</summary>
    public const string SimPathHashesXObjectKey = "XIL2CPP046";

    /// <summary>XIL2CPP047 -- Sim-path iterates FXObjectArray ordered. Section 7.3.</summary>
    public const string SimPathIteratesFXObjectArrayOrdered = "XIL2CPP047";

    /// <summary>XIL2CPP048 -- Task&lt;T&gt; / ValueTask&lt;T&gt; / IAsyncEnumerable&lt;T&gt; / await foreach / Task.Result / Task.Wait is banned on sim-path TUs. Section 5.9, 7.5.</summary>
    public const string SimPathTaskBanned = "XIL2CPP048";

    /// <summary>XIL2CPP049 -- Random.* banned on sim-path TUs. Section 7.4, 7.8.</summary>
    public const string SimPathRandomBanned = "XIL2CPP049";

    /// <summary>XIL2CPP059 -- Sim-path code constructs FName from non-literal string. Section 5.5.</summary>
    public const string SimPathFNameFromNonLiteral = "XIL2CPP059";

    // -----------------------------------------------------------------
    // Reflection runtime (XIL2CPP050-058).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP050 -- dynamic reflection invocation (Type.GetMethods / MethodInfo.Invoke) is post-MVP. Section 5.11.</summary>
    public const string DynamicReflectionInvocationPostMvp = "XIL2CPP050";

    /// <summary>XIL2CPP051 -- Activator.CreateInstance banned on all TUs in MVP. Section 5.11.</summary>
    public const string ActivatorCreateInstanceBanned = "XIL2CPP051";

    /// <summary>XIL2CPP052 -- System.Reflection.Emit is not supported (no managed runtime). Section 5.20.</summary>
    public const string ReflectionEmitNotSupported = "XIL2CPP052";

    /// <summary>XIL2CPP053 -- Type.MakeGenericType / Type.MakeGenericMethod not supported. Section 5.11.</summary>
    public const string MakeGenericTypeNotSupported = "XIL2CPP053";

    /// <summary>XIL2CPP054 -- typeof(T) at open-generic site cannot be resolved at compile time. Section 5.4, 5.11.</summary>
    public const string TypeofOpenGenericUnresolvable = "XIL2CPP054";

    /// <summary>XIL2CPP055 -- Interlocked.* banned on sim-path TUs. Section 5.10.5, 7.4.</summary>
    public const string SimPathInterlockedBanned = "XIL2CPP055";

    /// <summary>XIL2CPP056 -- field used with Interlocked but not declared volatile; promoting to std::atomic in C++ emit. Section 5.10.5.</summary>
    public const string InterlockedFieldNotVolatile = "XIL2CPP056";

    /// <summary>XIL2CPP057 -- [ThreadStatic] banned on sim-path. Section 5.4.</summary>
    public const string SimPathThreadStaticBanned = "XIL2CPP057";

    /// <summary>XIL2CPP058 -- locale-dependent ToString/Parse on sim-path TUs. Section 7.8.</summary>
    public const string SimPathLocaleDependentToStringParse = "XIL2CPP058";

    // -----------------------------------------------------------------
    // Pattern matching / boxing / foreach over IEnumerable (XIL2CPP060-064).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP060 -- pattern match on object with value-type pattern requires boxing. Section 5.6.</summary>
    public const string PatternMatchValueTypeRequiresBoxing = "XIL2CPP060";

    /// <summary>XIL2CPP061 -- anonymous types banned on sim-path; boxing of value type to object. Section 5.6, 5.7.</summary>
    public const string SimPathAnonymousTypesBanned = "XIL2CPP061";

    /// <summary>XIL2CPP062 -- implicit boxing of value type to object is not supported in MVP. Section 5.6.</summary>
    public const string ImplicitBoxingNotSupported = "XIL2CPP062";

    /// <summary>XIL2CPP063 -- chained write through XPtr&lt;T&gt; intermediate is undefined; assign to a stable local first. Section 5.3.</summary>
    public const string ChainedWriteThroughXPtrUndefined = "XIL2CPP063";

    /// <summary>XIL2CPP064 -- foreach over IEnumerable&lt;T&gt; interface is banned on sim-path (allocation pressure). Section 5.3.</summary>
    public const string SimPathForeachOverIEnumerableBanned = "XIL2CPP064";

    // -----------------------------------------------------------------
    // GC / container scaffolding (XIL2CPP070-073).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP070 -- conservative XGCRootSpan; cross-arch determinism may be affected (error on sim-path with SimPathConservativeRootsAllowed=false, warning otherwise). Section 6.6.</summary>
    public const string ConservativeXGCRootSpan = "XIL2CPP070";

    /// <summary>XIL2CPP071 -- container declared with copy semantics; XGC-aware containers are move-only. Section 6.2.</summary>
    public const string ContainerCopySemanticsForbidden = "XIL2CPP071";

    /// <summary>XIL2CPP072 -- custom container implementation missing XGCRootSpan registration in constructor. Section 6.2.</summary>
    public const string CustomContainerMissingRootSpanRegistration = "XIL2CPP072";

    /// <summary>XIL2CPP073 -- value type stored in object-typed container requires boxing, which is not supported. Section 5.7.</summary>
    public const string ValueTypeInObjectContainerRequiresBoxing = "XIL2CPP073";

    // -----------------------------------------------------------------
    // Span / unsafe / params allocation (XIL2CPP080-086).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP080 -- Span&lt;T&gt; / ReadOnlySpan&lt;T&gt; where T is or contains XObject reference is not supported in MVP. Section 5.13.</summary>
    public const string SpanOverXObjectUnsupported = "XIL2CPP080";

    /// <summary>XIL2CPP081 -- stackalloc T[n] where T contains XObject* is not supported. Section 5.13.</summary>
    public const string StackallocOverXObjectUnsupported = "XIL2CPP081";

    /// <summary>XIL2CPP082 -- Span&lt;T&gt; escapes its stack frame; ref struct rules violated. Section 5.13.</summary>
    public const string SpanEscapesStackFrame = "XIL2CPP082";

    /// <summary>XIL2CPP083 -- params T[] allocates per-call; consider params ReadOnlySpan&lt;T&gt; (post-MVP) or pass explicit array. Section 5.2.</summary>
    public const string ParamsArrayAllocatesPerCall = "XIL2CPP083";

    /// <summary>XIL2CPP084 -- Sim-path interpolation overflows stack buffer. Section 5.5.</summary>
    public const string SimPathInterpolationOverflowsStackBuffer = "XIL2CPP084";

    /// <summary>XIL2CPP085 -- Sim-path iterator pool exhausted. Section 5.9.</summary>
    public const string SimPathIteratorPoolExhausted = "XIL2CPP085";

    /// <summary>XIL2CPP086 -- stackalloc T[N] exceeds 64KB stack-allocation limit at static size. Section 5.13.</summary>
    public const string StackallocExceedsLimit = "XIL2CPP086";

    // -----------------------------------------------------------------
    // Object lifecycle + struct layout (XIL2CPP090-097).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP090 -- explicit destructor call on XObject-derived type; use MarkForKill(). Section 5.15.</summary>
    public const string ExplicitDestructorOnXObject = "XIL2CPP090";

    /// <summary>XIL2CPP091 -- fixed statement on XObject reference is meaningless (non-moving GC). Section 5.17.</summary>
    public const string FixedStatementOnXObjectMeaningless = "XIL2CPP091";

    /// <summary>XIL2CPP092 -- unsafe blocks are banned on sim-path TUs. Section 5.18.</summary>
    public const string SimPathUnsafeBlockBanned = "XIL2CPP092";

    /// <summary>XIL2CPP093 -- packed struct on ARM64 requires unaligned access; [StructLayout(Pack=N)] with N below natural alignment BANNED. Section 5.4.</summary>
    public const string PackedStructArm64Banned = "XIL2CPP093";

    /// <summary>XIL2CPP096 -- [XValueClass]-tagged type observed with reference identity. Section 5.1.</summary>
    public const string XValueClassObservedWithReferenceIdentity = "XIL2CPP096";

    /// <summary>XIL2CPP097 -- XObject-derived constructor with explicit base(args) is not supported. Section 5.2.</summary>
    public const string XObjectConstructorExplicitBaseArgs = "XIL2CPP097";

    // -----------------------------------------------------------------
    // LINQ / collection processing (XIL2CPP100).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP100 -- LINQ is deferred to post-MVP on non-sim-path; permanently banned on sim-path. Section 5.19.</summary>
    public const string LinqBannedOrDeferred = "XIL2CPP100";

    // -----------------------------------------------------------------
    // Generic instantiation (XIL2CPP123-129).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP123 -- generic instantiation closure exceeds depth 16. Section 5.8.</summary>
    public const string GenericInstantiationDepthExceeded = "XIL2CPP123";

    /// <summary>XIL2CPP124 -- cyclic generic type closure detected. Section 5.8.</summary>
    public const string CyclicGenericTypeClosure = "XIL2CPP124";

    /// <summary>XIL2CPP125 -- generic constraint 'T : unmanaged' violated by XObject-derived T. Section 5.8.</summary>
    public const string UnmanagedConstraintViolatedByXObject = "XIL2CPP125";

    /// <summary>XIL2CPP129 -- cross-module duplicate FClass for closed instantiation. Section 5.8.</summary>
    public const string CrossModuleDuplicateFClass = "XIL2CPP129";

    // -----------------------------------------------------------------
    // ABI / manifest mismatch (XIL2CPP140-149).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP140 -- Manifest declares an unknown ABI envelope tag value. Section 9.7.</summary>
    public const string ManifestUnknownAbiEnvelopeTag = "XIL2CPP140";

    /// <summary>XIL2CPP141 -- Manifest schema_version below minimum or mismatches XIL2CPP-supported set. Section 9.7.</summary>
    public const string ManifestUnsupportedSchemaVersion = "XIL2CPP141";

    /// <summary>XIL2CPP142 -- cross-tool symbol-space mismatch: XHT-declared singleton-getter not matched by XIL2CPP emit. Section 10.</summary>
    public const string CrossToolSymbolSpaceMismatch = "XIL2CPP142";

    /// <summary>XIL2CPP143 -- ABI envelope tag content mismatch between manifest and runtime header. Section 5.1, 9.7.</summary>
    public const string AbiEnvelopeTagContentMismatch = "XIL2CPP143";

    /// <summary>XIL2CPP144 -- Roslyn version mismatch between manifest and XIL2CPP binary. Section 8.1.</summary>
    public const string RoslynVersionMismatch = "XIL2CPP144";

    /// <summary>XIL2CPP145 -- vtable slot count or order changed; class layout drift forbidden mid-session. Section 5.2, 8.2.</summary>
    public const string VtableLayoutDrift = "XIL2CPP145";

    /// <summary>XIL2CPP146 -- XHT-emitted FProperty descriptor schema version mismatches XIL2CPP's supported value. Section 10.</summary>
    public const string XhtFPropertySchemaMismatch = "XIL2CPP146";

    /// <summary>XIL2CPP149 -- backing-field naming mismatch between XHT and XIL2CPP for property. Section 10.5.</summary>
    public const string BackingFieldNamingMismatch = "XIL2CPP149";

    // -----------------------------------------------------------------
    // Cross-system contract (XIL2CPP150-151).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP150 -- cross-language type-pair manifest mismatch (C# type / C++ type pair declared but not aligned). Section 10.3.</summary>
    public const string CrossLanguageTypePairMismatch = "XIL2CPP150";

    /// <summary>XIL2CPP151 -- FProperty descriptor missing from XHT-emitted reflection metadata for cross-module field. Section 10.2.</summary>
    public const string FPropertyDescriptorMissing = "XIL2CPP151";

    // -----------------------------------------------------------------
    // Forward-commitment fallbacks (XIL2CPP170-187).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP170 -- ReferenceCompileCSharpAction not registered in XBT action graph; XBT Rev 11 amendment missing. Section 9.8 (hard-fail, exit 63).</summary>
    public const string ReferenceCompileActionMissing = "XIL2CPP170";

    /// <summary>XIL2CPP171 -- TArray write barrier emitting in degraded mode (XCoreXObject Rev 5 m_parent field not yet shipped). Section 5.7, 6.3.</summary>
    public const string TArrayWriteBarrierDegradedMode = "XIL2CPP171";

    /// <summary>XIL2CPP172 -- XPACT_SAFEPOINT_CHECK macro not published by XCoreXObject Rev 5. Section 6.5, 14.0.</summary>
    public const string SafepointCheckMacroMissing = "XIL2CPP172";

    /// <summary>XIL2CPP173 -- GC store memory-order forward-commit not yet honored; emitting conservative cross-arch release. Section 6.3, 14.0.</summary>
    public const string GcStoreMemoryOrderConservative = "XIL2CPP173";

    /// <summary>XIL2CPP174 -- XObject.NewRoot factory not yet shipped; XCoreXObject Rev 5 missing. Section 6.4, 14.0.</summary>
    public const string NewRootFactoryMissing = "XIL2CPP174";

    /// <summary>XIL2CPP175 -- IXObjectClassReplacedListener not published; XCoreXObject Rev 5 missing; hot-reload cascade hot-patch unavailable. Section 8.4, 14.0.</summary>
    public const string ClassReplacedListenerMissing = "XIL2CPP175";

    /// <summary>XIL2CPP176 -- XStackMapTable lazy-init not shipped; using eager-init fallback. Section 6.1, 14.0.</summary>
    public const string StackMapTableLazyInitMissing = "XIL2CPP176";

    /// <summary>XIL2CPP177 -- schema vector emit not supported by XHT; conservative scan fallback active. Section 10.4, 14.0.</summary>
    public const string SchemaVectorEmitUnsupported = "XIL2CPP177";

    /// <summary>XIL2CPP178 -- cpp_nothrow_symbols not published by XHT; conservative T1 shim active for all C++ callees. Section 3.3, 14.0.</summary>
    public const string CppNoThrowSymbolsMissing = "XIL2CPP178";

    /// <summary>XIL2CPP179 -- K/Q mangling discriminators not yet in Contract Rev 14; using XIL2CPP-private extension. Section 5.8, 14.0.</summary>
    public const string ManglingDiscriminatorsMissing = "XIL2CPP179";

    /// <summary>XIL2CPP180 -- using __ separator pending Contract Rev 14 _X_ replacement; non-conformance risk accepted. Section 8.1, 14.0.</summary>
    public const string SeparatorPendingContractRev14 = "XIL2CPP180";

    /// <summary>XIL2CPP181 -- BCL-Rev2-HANDLER not yet shipped; emitting inline RAII types. Section 5.12, 5.15, 14.0.</summary>
    public const string BclHandlerTypesMissing = "XIL2CPP181";

    /// <summary>XIL2CPP182 -- XCharOps surface not yet shipped; non-sim-path callers may use FChar; sim-path callers must wait. Section 14.0.</summary>
    public const string XCharOpsSurfaceMissing = "XIL2CPP182";

    /// <summary>XIL2CPP183 -- XCSharpStringBuilder naming not yet finalized; using FStringBuilder directly. Section 5.5, 14.0.</summary>
    public const string StringBuilderNamingNotFinalized = "XIL2CPP183";

    /// <summary>XIL2CPP184 -- XCSharpException MVP surface not yet defined; Phase 6.j blocked. Section 5.12, 14.0.</summary>
    public const string ExceptionSurfaceUndefined = "XIL2CPP184";

    /// <summary>XIL2CPP185 -- full XException surface deferred to post-MVP. Section 5.12, 14.0.</summary>
    public const string FullExceptionSurfaceDeferred = "XIL2CPP185";

    /// <summary>XIL2CPP186 -- IDeterministicRng surface not yet published; sim-path PRNG blocked. Section 7.4, 14.0.</summary>
    public const string DeterministicRngSurfaceMissing = "XIL2CPP186";

    /// <summary>XIL2CPP187 -- Int32Box/FloatBox not yet shipped in BCL Rev 2; use typed record discriminant instead. Section 5.6, 14.0.</summary>
    public const string BoxingWrappersMissing = "XIL2CPP187";

    // -----------------------------------------------------------------
    // Hot-reload + module unload (XIL2CPP200).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP200 -- module unload while OnClassReplaced listener still registered; potential dangling-pointer hazard. Section 8.4.</summary>
    public const string ModuleUnloadWithRegisteredListener = "XIL2CPP200";

    // -----------------------------------------------------------------
    // ICE band (XIL2CPP900).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP900 -- internal compiler error: an XIL2CPP-side bug surfaced through an un-anchored exception path. See <c>XIL2CPP.Entry.Program</c>'s catch surface.</summary>
    public const string InternalCompilerError = "XIL2CPP900";
}
