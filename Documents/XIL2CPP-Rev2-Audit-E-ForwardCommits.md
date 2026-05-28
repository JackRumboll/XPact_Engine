# XIL2CPP Rev 2 Audit — Perspective E: Forward-commitments + cross-system contradictions

Auditor: Round 2 retry (third attempt; prior two hit socket errors mid-run)
Audit date: 2026-05-28
Audited revision: commit a6483f2 (Documents/XIL2CPP.html Rev 2)
Scope: NARROWED — Axis 1 (forward-commits in §14.0) + Axis 2 (5 cross-system contradictions in §14.0.1). Other axes deferred to other auditors / Rev 3.

## Summary

- Total findings: 22
- CRITICAL: 4
- HIGH: 9
- MEDIUM: 6
- LOW: 3

### Forward-commitment fingerprint scorecard

The user's prompt referred to "17 forward-commits"; the table actually contains **18 rows**. Discrepancy noted (see FIX-E-LOW-3).

| # | ID | Field/Type/Location fingerprint | Rationale stated | Fallback documented | Severity |
|---|----|--------------------------------|------------------|---------------------|----------|
| 1 | XCXO-Rev5-SAFEPOINT | OK (macro name + TLS field + handler + tag) | OK (FIX-A-CRIT-6) | NO | well-formed |
| 2 | XCXO-Rev5-GCSTORE-MEM | UNDER (no memory-order code shown, ARM64 only) | OK | NO | under-spec |
| 3 | XCXO-Rev5-NEWROOT | OK (signature + semantics) | OK | NO | well-formed |
| 4 | XCXO-Rev5-LISTENER | OK (interface + delegate + FHandle + Unsub) | OK | NO | well-formed |
| 5 | XCXO-Rev5-CONTAINER-PARENT | UNDER (no constructor signature; no card-table API; no Realloc snapshot protocol) | OK | NO | under-spec |
| 6 | XCXO-Rev5-LAZY-INIT | UNDER (which class? which init-state machine?) | partial | NO | under-spec |
| 7 | XHT-Rev7-SCHEMA-VECTOR | OK (cross-tool boundary §10.4 + Q14) | OK | NO | well-formed |
| 8 | XHT-Rev7-NOTHROW-CPP | OK (gen.manifest location) | OK | NO | well-formed |
| 9 | XHT-Rev7-BACKING-FIELD | OK (exact naming `__BackingField_<X>`) | OK | NO | well-formed |
| 10 | XBT-Rev11-REFCOMPILE | OK (slot 17, prereq chain, CommandVersion) | OK | NO | well-formed |
| 11 | XBT-Rev11-MANIFEST-FIELDS | OK (4 typed fields enumerated) | OK | NO | well-formed |
| 12 | Contract-Rev14-MANGLING | OK (`K`, `Q` discriminator letters) | OK | NO | well-formed |
| 13 | Contract-Rev14-SEPARATOR | UNDER (no replacement proposed; only "review") | partial | NO | under-spec |
| 14 | BCL-Rev2-BOXING | UNDER (only Int32Box / FloatBox; "other primitive-wrapper" hand-wave) | OK | NO | under-spec |
| 15 | BCL-Rev2-HANDLER | OK (3 typed RAII types listed) | OK | NO | well-formed |
| 16 | BCL-Rev2-CHAR | UNDER ("etc." hand-wave) | OK | NO | under-spec |
| 17 | XException-V1 | UNDER (no exception-class shape, no exception type list) | OK | partial | under-spec |
| 18 | XPact.Sim-PRNG | UNDER (interface name only; no method signatures, no semantics) | OK | NO | under-spec |

**Counts**: well-formed = 9; under-specified = 9.
**No commit documents a fallback** (what XIL2CPP does if the receiving spec misses the rev — degrade gracefully? hard-fail? warn?). This is a SYSTEMATIC GAP across all 18 commits.

### Cross-system contradiction resolution feasibility

| # | Contradiction | Status | Resolution path |
|---|---------------|--------|-----------------|
| 1 | XCXO lifecycle 7 vs 8 slots | **mis-stated; not a real contradiction** | Rev 3 self-contained — strike from §14.0.1 (XIL2CPP emits 8, XCXO defines 8) |
| 2 | Contract Rev 13.9 ABI tag counts (24/21) | **REAL cross-doc drift; worse than stated** | Forward-commit; Contract Rev 13.9 doc update required (currently Rev 13.8 in source) |
| 3 | XHT NoThrow dual surface | **REAL** | Forward-commit XHT Rev 7 (already filed; consistent) |
| 4 | Boxing wrappers in BCL | **REAL** | Forward-commit BCL Rev 2 (already filed; consistent) |
| 5 | Manifest schema_version vs contract_version | **REAL** | Forward-commit XBT Rev 11 (already filed; consistent) |

## Findings

### FIX-E-CRIT-1 [severity: CRITICAL]

**Location**: XIL2CPP.html §9.7 (line 3163-3243) and §14.0.1 (line 3599)

**Issue**: XIL2CPP repeatedly cites "Contract Rev 13.9 §14.1" and "Contract Rev 13.9 §14.2" with **24 layout tags + 21 sizeof pins**, but `XToolchainContract.html` (the actual contract doc) is still at **Rev 13.8** with §14.1 declaring **15 layout-tag macros** and §14.2 declaring **14 sizeof pins**. The contract doc was NEVER rolled to Rev 13.9, even though commit 72be4f8 ("Contract Rev 13.8 → 13.9 prerequisite") updated the C++ headers (FStruct +RefSchema, FClass +LifecycleTable). The contract HTML still ends "Rev 13.8" in §14.5 and the §14.3 hot-reload-safety mechanism still cites "15 layout-tag content pins + 14 sizeof pins". This is the Rev 13.9 the entire XIL2CPP doc is contractually pinned against. Without it, every `XPACT_*_LAYOUT_TAG` macro that XIL2CPP claims is "verbatim Contract Rev 13.9 §14.1 content" has NO canonical source to verify against. The contract surface hash (per §14.5) is unchanged from 13.8 in source.

**Fix**: This is a XToolchainContract Rev 13.9 carry-forward gap, not an XIL2CPP defect per se. The right action is a separate `XToolchainContract.html Rev 13.9` PR that:
- Updates §14.1 to enumerate 24 macros (15 Stage-B reflection + 9 new XObject-side: XPACT_XOBJECT_LAYOUT_TAG, XPACT_XGC_CARDTABLE_LAYOUT_TAG, XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG, XPACT_XOBJECTKEY_LAYOUT_TAG, XPACT_XWEAKPTR_LAYOUT_TAG, XPACT_XPTR_LAYOUT_TAG, XPACT_XOBJECT_LIFECYCLE_TABLE_TAG, XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG, XPACT_GC_STORE_BARRIER_TAG or similar). 
- Updates §14.2 to enumerate 21 sizeof pins (14 existing + 7 new: XObject, FXObjectArrayEntry, XObjectKey, XWeakPtr, XPtr<XObject>, FXObjectLifecycleTable, FXObjectRefSchema — plus the XGCRootSpan==32 / FStackMapRecord%8==0 pins that XIL2CPP emits).
- Updates §14.5 to bump SemanticVersionTag and ContractVersion.Current hash to 13.9+<sha>.
- Updates the layout-tag-strings to FProperty FFakeVTable etc. variants that XIL2CPP §9.7 emits (e.g., FStruct-v5 with appended RefSchema@112 — currently FStruct-v4 in Contract).

Until that Rev 13.9 lands, XIL2CPP is asserting against a contract that doesn't exist. XIL2CPP Rev 2 should either (a) add a §0 forward-commit note that says "Contract Rev 13.9 is a co-required PR", or (b) regress its `XPACT_*` tag string content to Rev 13.8 actuals until the contract lands.

---

### FIX-E-CRIT-2 [severity: CRITICAL]

**Location**: XIL2CPP.html §9.7 line 3220 ("21 entries") vs lines 3221-3243 (the actual list)

**Issue**: The comment header says "21 entries", but the listed `static_assert(sizeof(...))` lines are **23 entries** (counted at lines 3221-3243): FName, FField, FFieldClass, FFieldVariant, FProperty, FFakeVTable, FStruct, FScriptStruct, FCppStructOpsFakeVTable, FClass, FRepRecord, FEnum, FInterface, FCustomVersion, XObject, FXObjectArrayEntry, XObjectKey, XWeakPtr, XPtr<XObject>, FXObjectLifecycleTable, FXObjectRefSchema, XGCRootSpan, **plus** `static_assert(sizeof(FStackMapRecord) % 8 == 0)` (which is a constraint not a sizeof pin). The comment "21 entries" is wrong against the emit; even excluding the modulo-check entry it is 22.

Also XIL2CPP §14.0 mentions "(full 24-tag + 21-sizeof list)" in the Rev 2 changelog cell at line 3660. This is doubly wrong: 
- The §9.7 layout-tag block actually counts 24 (15 + 9), so the 24 is consistent with XIL2CPP's own emit, but inconsistent with Contract Rev 13.8's 15. 
- The 21 sizeof claim is inconsistent with both Contract Rev 13.8's 14 and XIL2CPP's own 22-or-23 emit.

**Fix**: Either (a) reconcile to "23 sizeof pins (14 Stage-B + 8 XObject + 1 stack-map alignment)" and update both the changelog cell AND the §9.7 comment, OR (b) drop the XGCRootSpan and FStackMapRecord lines from the pin block (these are XIL2CPP-domain not Contract-domain — they belong in a Contract Rev 14 addendum, not §14.2) and report 21. The "9 entries" header for XObject-side tags (line 3202) also undercounts: only 8 static_asserts follow, missing one (or the comment is wrong). **Pick one count and apply it consistently in the §14.0 changelog + §9.7 header + §9.7 emit + future Contract Rev 13.9 §14.2 table.**

---

### FIX-E-CRIT-3 [severity: CRITICAL]

**Location**: XIL2CPP.html §14.0 line 3590 (XException-V1 row)

**Issue**: The `XException-V1` forward-commit cites "New XException.html (post-MVP)" as the receiving spec, but XIL2CPP MVP itself depends on the XCSharpException type today (per §6.2 line 1197 and §7.5 line 2134; emit example uses `catch (const ::XCore::Exception::XCSharpException& ex)`). MVP exception emit cannot wait for post-MVP XException.html. The forward-commit is misclassified as post-MVP when an MVP subset is required.

Cross-check: §5.12 (line 2229) declares "XCSharpException surface" for MVP, with Message, StackTrace, InnerException — but then §5.12 line 2232 forward-references "the detailed exception-class spec lives in a separate XException.html doc to be authored post-MVP". The MVP-subset vs post-MVP boundary is not drawn anywhere. Without a documented MVP exception surface, XIL2CPP Phase 6.j (exception emit) has no contract to compile against.

**Fix**: Split the forward-commit into two:
- `XException-MVP` (CRITICAL, blocking): defines the minimum exception-class surface XIL2CPP MVP requires: XCSharpException with Message field, StackTrace as opaque XStackTraceHandle, InnerException pointer chain. Should be carved into XCoreXObject Rev 5 OR a new XException.html Rev 1 BEFORE Phase 6.j ships.
- `XException-V1` (post-MVP, current row): full reflective Exception surface including TargetSite, Source, custom exception subclasses, stack-walking semantics.

Without this split, the MVP plan ships against an undefined exception contract.

---

### FIX-E-CRIT-4 [severity: CRITICAL]

**Location**: XIL2CPP.html §14.0 all 18 rows; §14.0.1 contradiction #1

**Issue**: NO forward-commit documents what XIL2CPP does if the receiving spec MISSES the requested feature in its scheduled revision. This is the "fallback" axis the prompt asked about. Examples:
- If XCoreXObject Rev 5 ships without XPACT_SAFEPOINT_CHECK (XCXO-Rev5-SAFEPOINT), does XIL2CPP Phase 6.g hard-fail, emit a no-op, or emit its own duplicate macro?
- If XHT Rev 7 ships without C++ NoThrow surface (XHT-Rev7-NOTHROW-CPP), does XIL2CPP Phase 2 Tier 1 conservatively assume all C++ callees can throw (correct but slow), hard-fail (wrong), or warn-and-assume-nothrow (unsafe)?
- If Contract Rev 14 ships without `K`/`Q` mangling discriminators (Contract-Rev14-MANGLING), how does XIL2CPP mangle `ref readonly` and nullable T?? Use an XIL2CPP-private extension? Emit XIL2CPP-error and refuse to compile?

The contradiction #1 ("lifecycle slot count 7 vs 8") is itself a sign of this gap: XIL2CPP §14.0.1 says it "enumerates 7 explicit slots" but the actual XIL2CPP §5.1 emit (lines 906-922) enumerates ALL 8 slot bodies (PostInitProperties, BeginDestroy, IsReadyForFinishDestroy, FinishDestroy, Serialize, AddReferencedObjects, PostLoad, PreSave). The contradiction-self-report is WRONG — it's not a contradiction, it's a stale doc-prose claim that contradicts the emit example two pages later in the same doc. This signals that Rev 2's contradiction list itself was written without re-checking against the same Rev 2's emit.

**Fix**: For each forward-commit, add a fallback column to the §14.0 table: "If missing in target rev, XIL2CPP behavior is: <action>". Possible actions:
- "Hard-fail at build with XIL2CPP-<code>"
- "Emit conservative default (T1 shim for all C++ callees, no mangling discriminator suffix, no compile-time pin)"
- "Defer to Rev 3 — bumps MVP critical path"
- "XIL2CPP-private extension (with handoff plan when receiving spec catches up)"

Also strike the lifecycle-slot-count contradiction from §14.0.1 — it's not real; the doc internally agrees on 8 slots.

---

### FIX-E-HIGH-1 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3580 (XHT-Rev7-SCHEMA-VECTOR)

**Issue**: The forward-commit is described as "Schema vector emit for C#-declared XClasses" but does NOT specify which XHT pass takes ownership, what input data XHT consumes (FProperty descriptors from XHT's pre-existing pass, or XIL2CPP-emitted intermediate), or whether the FXObjectRefSchema opcode encoding is XHT-internal or shared with XIL2CPP. The Q14 §10.4 resolution says XHT emits for "both C++- and C#-declared types", but it does not say WHEN XHT learns about C#-declared types (XHT reads .cs files? XHT reads XIL2CPP-emitted intermediate? XHT receives a .gen.manifest from XIL2CPP?).

This is a real boundary question because XHT is a separate tool with its own action graph slot.

**Fix**: Extend the forward-commit description:
- Input: "XHT consumes the per-module `.cs.h` declarations emitted by XIL2CPP Phase 6.e to discover C#-declared XClasses and their FProperty fields."
- Output: "XHT emits the FXObjectRefSchema opcode vector + extern declaration in the per-class `.gen.h`."
- Ordering: "XHT's action runs AFTER XIL2CPP's emit action in the action graph; XBT slot 17 (ReferenceCompileCSharpAction) is a XIL2CPP prereq, but XHT's reflect action is XIL2CPP's downstream consumer."
- Failure mode: "If XIL2CPP emits a C#-declared XClass with an FProperty XHT cannot understand (e.g., an unknown generic instantiation), XHT emits a typed diagnostic and the build fails."

Without this fingerprint, XHT Rev 7 implementers have no contract to wire against.

---

### FIX-E-HIGH-2 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3581 (XHT-Rev7-NOTHROW-CPP)

**Issue**: The forward-commit says "NoThrow annotation discovery for C++-declared functions in per-module `.gen.manifest`", but does NOT specify the manifest schema additions, the discriminator mechanism (per-symbol vs per-class-method-set), or how XIL2CPP discovers NoThrow on a transitively-called C++ function that lives in a third-party SDK (e.g., glm, SDL3) that does NOT pass through XHT. The XHT cross-doc §5.2 N2 mentions "NoThrow lookup via XHT's emitted reflection metadata" but XHT.html itself has no C++ NoThrow handling (Grep confirms only the C# NoThrow surface is in XHT.html, lines 172 and 1576).

**Fix**: Extend the forward-commit:
- Schema delta: "XHT's `.gen.manifest` adds a new section `cpp_nothrow_symbols: List<MangledSymbol>` enumerating every C++-declared function with `noexcept` or `[[nodiscard]]`-equivalent attribution."
- Source: "XHT reads the per-module `.h` headers it already scans for FProperty/XCLASS macros + adds a `noexcept` token scanner."
- Third-party scope: "C++ symbols in modules NOT processed by XHT (third-party SDKs) are conservatively classified as throwing by XIL2CPP Phase 2 — XIL2CPP01x diagnostic if a sim-path function transitively calls a throwing C++ callee."
- Fingerprint: "MangledSymbol uses Contract Rev 13.9 mangling scheme; the C++ symbol's NoThrow attribution is derived from its declaration site, not its definition site (so the `.h` is canonical, the `.cpp` is informational)."

This explicit boundary is required for XIL2CPP Phase 2 + Phase 6.c to ship.

---

### FIX-E-HIGH-3 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3587 (BCL-Rev2-BOXING)

**Issue**: The forward-commit says "Add Int32Box, FloatBox, and other primitive-wrapper XObject types". The "other primitive-wrapper" hand-wave is unsafe: C# has 13 distinct value-type primitives (bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, decimal, char) plus 2 common runtime-boxed types (DateTime, Guid). XIL2CPP's XIL2CPP062 "boxing banned" diagnostic enforces an MVP ban (FIX-B-CRIT-02), but the lower BCL layer needs to know which wrappers to materialize.

Cross-check: Grep confirms no Int32Box/FloatBox exists in the codebase or docs today. The forward-commit is the ONLY mention of this surface in any doc. The forward-commit also doesn't document:
- Whether the wrappers are MVP requirements (XIL2CPP062 ban makes them post-ban material? Or are they for `object o = (object)5` non-sim-path)?
- Whether they are XObject-derived (which means each one is a registered XClass, +72 bytes lifecycle table, +slot in FClass) or non-XObject value types
- Whether `(object)x` boxes (and unboxes back) are emit-time XIL2CPP-error or runtime XHT-aware box materialization

**Fix**: Extend the forward-commit:
- Enumerate explicit set: "Int32Box, UInt32Box, Int64Box, UInt64Box, FloatBox, DoubleBox, BoolBox, ByteBox, SByteBox, ShortBox, UShortBox, CharBox, DecimalBox, DateTimeBox, GuidBox (15 explicit; no `other primitive-wrapper`)."
- Class shape: "Each XxxBox is an [XValueClass] (no XObject lifecycle, no FClass) wrapping a single field of the primitive type. Hash + Equals defer to the primitive's value semantics."
- Usage gate: "MVP: XIL2CPP062 banning still applies on sim-path; the boxes are emitted ONLY on non-sim-path TUs where C# `object o = 5` occurs. The box class XObject-derivation is decided at BCL Rev 2 time but defaults to non-XObject reference-typed (struct-with-XObject-field semantics)."

Without this enumeration, BCL Rev 2 implementers ship the wrong subset and XIL2CPP cannot lower `object` casts of value types.

---

### FIX-E-HIGH-4 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3589 (BCL-Rev2-CHAR)

**Issue**: "Add XCharOps::IsDigit, IsLetter, etc. on char16_t" — the "etc." is unsafe. C#'s System.Char surface has ~30 static methods: IsDigit, IsLetter, IsLetterOrDigit, IsWhiteSpace, IsUpper, IsLower, IsControl, IsNumber, IsPunctuation, IsSurrogate, IsHighSurrogate, IsLowSurrogate, IsSymbol, IsSeparator, IsSurrogatePair, ToUpper, ToLower, ToUpperInvariant, ToLowerInvariant, GetNumericValue, GetUnicodeCategory, ConvertFromUtf32, ConvertToUtf32, Parse, TryParse, plus IsAscii*, IsBetween, IsAsciiHexDigit. Determinism is a sim-path concern: locale-dependent ToUpper/ToLower must NOT exist on sim-path (only InvariantCulture variants).

**Fix**: Extend the forward-commit to enumerate the supported methods explicitly; mark Invariant-only variants as sim-path-allowed; mark locale-dependent variants as XIL2CPP053 banned (already in Rev 2 §7.5 diagnostic table). Without this, BCL implementers ship some subset, sim-path determinism cannot be enforced.

---

### FIX-E-HIGH-5 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3591 (XPact.Sim-PRNG / IDeterministicRng)

**Issue**: The forward-commit publishes "IDeterministicRng with seed-locked construction" but does NOT enumerate the interface methods. XPact.Sim consumers (game-side determinism, replay-sim correctness gates per §11) need to know the surface contract. C# RNG patterns include: `Next()`, `NextInt32(min, max)`, `NextSingle()`, `NextDouble()`, `NextBytes(Span<byte>)`, `NextBoolean()`, plus state-export methods for replay (`GetState()` / `RestoreState()`). Without enumeration, replay-sim CrossArchReplayTest (Phase 6.l) cannot be implemented because the deterministic surface is undefined.

**Fix**: Add to the forward-commit description:
- "IDeterministicRng surface: NextInt32(), NextInt32(min, max), NextSingle() in [0, 1), NextDouble() in [0, 1), NextBoolean(), NextBytes(Span<byte>). State export: ExportState() returns 64-byte FRngState; RestoreState(FRngState) reseeds. Seed type: ulong (64-bit). Construction: `IDeterministicRng XPactRng.CreateWithSeed(ulong seed)` factory. Implementation: XPact-canonical SplitMix64 or PCG64; cross-arch byte-for-byte deterministic."
- Without this surface, sim-path PRNG cannot be tested against criterion (c) cross-arch convergence.

---

### FIX-E-HIGH-6 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3586 (Contract-Rev14-SEPARATOR)

**Issue**: The forward-commit says "Review linker-symbol separator (`__` reserved by C/C++ standards); propose alternative". This is a NON-COMMIT — XIL2CPP punts on the decision without proposing an alternative. C++ ABI separators commonly chosen: `_X_` (XPact-tagged), `$` (linker-legal in many ELF/COFF), `_Z`/`_N` (Itanium prefix prefixes), `_e_` (private-extension marker). The current `__` choice IS undefined behavior per C++ standard (§17.4.3.2.1).

This affects XIL2CPP Phase 6.e (mangling assignment) — XIL2CPP cannot ship a mangling scheme that may need to change later.

**Fix**: Either (a) propose the alternative in this forward-commit row (e.g., "Replace `__` with `_X_`; symbol-stability test in Phase 6.k re-verifies stability"), or (b) downgrade the forward-commit to "Defer to Rev 3 — XIL2CPP MVP uses `__` and accepts the non-conformance risk; symbol stability test does not fire on UB unless toolchain rejects". Option (a) is preferred per Prime Directive.

---

### FIX-E-HIGH-7 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3578 (XCXO-Rev5-CONTAINER-PARENT)

**Issue**: "TArray/TMap/TSet constructor accepts parent XObject*; card-table-mark uses parent (not `this`); Realloc mark-phase snapshot mechanism" — three things rolled into one row, each load-bearing but none fingerprinted:
- TArray<T>(XObject* parent) constructor signature: where does it go? Is the existing parameterless TArray ctor still allowed for non-XObject element types? What about `TArray<int>` (no XObject elements) — does it still need a parent?
- Card-table-mark API: which XCoreXObject Rev 5 function is called? `MarkCard(XObject* parent)` or `MarkCardForAddress(void* addr)`? At what offset granularity (per-byte, per-cache-line, per-page)?
- "Realloc mark-phase snapshot": what is the snapshot mechanism? Two-phase commit? Hazard pointer? The Round 1 finding FIX-A-MED-17 must have proposed something; the forward-commit doesn't carry it forward.

**Fix**: Split into three sub-commits or add a 1-paragraph fingerprint per item. The XCoreXObject Rev 5 implementer cannot wire any of the three without each one having a definite signature.

---

### FIX-E-HIGH-8 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3575 (XCXO-Rev5-GCSTORE-MEM)

**Issue**: "ARM64 acquire/release memory-order for SATB + slot store; add XPACT_GC_STORE_MEMORY_ORDER_TAG". Three issues:
- Only ARM64 is mentioned. What about x86-64 (the other MVP arch)? x86-64's strong memory model means most loads/stores are de facto release/acquire — but the forward-commit doesn't say that explicitly. Does XIL2CPP emit identical code on both archs and rely on the C++ memory model to be a no-op on x86? Or does it emit `std::memory_order_relaxed` on x86 and `std::memory_order_release` on ARM?
- "SATB + slot store" — two distinct stores. SATB is the snapshot-at-the-beginning write barrier; "slot store" is the actual reference write. Which one needs which memory order? Standard SATB implementations use `relaxed` on the barrier and `release` on the store (or vice-versa depending on collector). The row doesn't specify.
- "XPACT_GC_STORE_MEMORY_ORDER_TAG" — what's the tag content? Is it "relaxed-x86/release-arm64" string, or "C++20 memory_order_release" semantic name? This needs to land in Contract Rev 13.9 §14.1 too.

**Fix**: Fingerprint the row: 
- Per-arch memory order: x86-64 uses `memory_order_relaxed` for both SATB enqueue and slot store (TSO arch); ARM64 uses `memory_order_release` for both.
- Tag content: `"XGCStore-v1: SATB enqueue (relaxed on x86, release on ARM64); slot store (relaxed on x86, release on ARM64); read-side acquires inferred from architecture"`.

Without per-arch specificity, the implementer's choice may not converge across the team.

---

### FIX-E-HIGH-9 [severity: HIGH]

**Location**: XIL2CPP.html §14.0 line 3580 (XHT-Rev7-SCHEMA-VECTOR) cross-ref with XCoreXObject.html line 1739 (FXObjectRefSchemaOp)

**Issue**: The schema-vector opcode set is defined in XCoreXObject.html §7.4 line 1657-1739+ (the opcode-bit mask, op kinds, etc.). XIL2CPP Rev 2 wants XHT to emit the schema vector for C#-declared XClasses. But the opcode set Q14-resolved location (XCoreXObject.html §7.4) does NOT enumerate which opcodes are MVP vs post-MVP, which are sim-path-safe, or whether the bit-mask capability field allows for forward-evolution.

If XHT Rev 7 supports opcodes 1-10 of the bit-mask but XIL2CPP wants to emit opcode 11 in its first C# transpile, the forward-commit doesn't say what XIL2CPP does. Forward-commit fingerprint missing the **scope of the bit-mask capability field at XHT Rev 7 time**.

**Fix**: Cross-reference XCoreXObject §7.4 explicitly with an opcode-by-opcode MVP/post-MVP table in either the forward-commit description or in XHT Rev 7's design doc.

---

### FIX-E-MED-1 [severity: MEDIUM]

**Location**: XIL2CPP.html §14.0 line 3579 (XCXO-Rev5-LAZY-INIT)

**Issue**: "Lazy init for XStackMapTable so registration ordering hazards are eliminated" — non-specific. Which XStackMapTable? The per-module one or the engine-wide one? Lazy-init via what mechanism: function-local static (Magic Statics), `std::call_once`, `std::atomic<XStackMapTable*>` with double-checked locking, or constinit + lazy population? The Round 1 origin FIX-A-MED-30 likely had context; the forward-commit doesn't carry it.

**Fix**: Specify mechanism (e.g., "Per-module XStackMapTable wrapped in `LazyInit<XStackMapTable>`; population is single-threaded inside FClass-module-load, which is already serialized through XReflectionRuntime::RegisterClass") + cite FIX-A-MED-30 explicitly.

---

### FIX-E-MED-2 [severity: MEDIUM]

**Location**: XIL2CPP.html §14.0 line 3577 (XCXO-Rev5-LISTENER)

**Issue**: The IXObjectClassReplacedListener interface is well-fingerprinted (Subscribe returns FHandle; FHandle.Unsubscribe()). But it does NOT say whether the listener fires per-XClass-replacement or per-module-replacement, whether re-entrancy is allowed (a listener that registers another listener), or thread-safety (which thread fires the callback). All three matter for XIL2CPP Phase 6.k hot-reload bind.

**Fix**: Extend: "Listener fires once per-XClass replacement (not per-module); fired on the XBT-self thread after rebind but before user code resumes; re-entrant Subscribe inside the listener body is allowed but the new subscription is deferred to the next replacement event (avoiding infinite loop)."

---

### FIX-E-MED-3 [severity: MEDIUM]

**Location**: XIL2CPP.html §14.0 line 3585 (Contract-Rev14-MANGLING)

**Issue**: "Add param mangling discriminators: K for ref readonly; Q for nullability" — the letters are fingerprinted. But it doesn't say where in the mangled symbol they appear (prefix vs suffix vs interspersed with the parameter type), whether they compose (e.g., `K Q` for `ref readonly` of a nullable type), or how they interact with the existing reference-type discriminator (e.g., R for ref, P for pointer). Also nullability has finer-grain than `T?` (e.g., `T??` is invalid but `Nullable<T>` is distinct from `T?` in some compiler interpretations).

**Fix**: Specify position (suffix on parameter type, before the next parameter separator) + composition rules (K precedes Q if both apply; ordering matters: `KQ` is "ref readonly nullable T"; `QK` is illegal) + cross-link to Contract §1.x mangling rules.

---

### FIX-E-MED-4 [severity: MEDIUM]

**Location**: XIL2CPP.html §14.0.1 line 3598 (lifecycle slot count contradiction)

**Issue**: As detailed in FIX-E-CRIT-4, this "contradiction" is mis-stated. XIL2CPP Rev 2 §5.1 (line 906-922) actually emits 8 lifecycle slot bodies in its example, matching XCoreXObject §1.3's 8. There is NO 7-vs-8 contradiction. The §14.0.1 entry is stale — likely written against an earlier Rev 2 draft and not refreshed before commit.

**Fix**: STRIKE the lifecycle slot count item from §14.0.1. Add a doc-prose audit hook to Rev 3: re-verify §14.0.1 contradictions against the same Rev's emit examples to catch stale contradictions.

---

### FIX-E-MED-5 [severity: MEDIUM]

**Location**: XIL2CPP.html §14.0.1 line 3599 (ABI tag count contradiction)

**Issue**: "Contract Rev 13.9 ABI tag count: 24 layout tags + 21 sizeof pins per §14.1. Rev 2 emits the FULL list; verify per-tool sanity (XHT emits the same superset)." This is the right contradiction surface, but the prompt-stated "21 sizeof pins" is inconsistent with XIL2CPP's own §9.7 emit (which actually shows ~22-23 entries depending on how you count). See FIX-E-CRIT-2.

**Fix**: Sync the contradiction-statement's count with the §9.7 emit count once §9.7 itself is reconciled to a single canonical number.

---

### FIX-E-MED-6 [severity: MEDIUM]

**Location**: XIL2CPP.html §14.0 line 3584 (XBT-Rev11-MANIFEST-FIELDS)

**Issue**: Four fields enumerated (schema_version, roslyn_version, dotnet_sdk_version, conditional_symbols). But does NOT specify:
- FBS ordinals for the new fields (FBS additions must be append-only; the ordinal of each new field matters for forward-compat per Contract §10.2)
- The semantic-version-comparison rule for roslyn_version + dotnet_sdk_version (semver match? exact match? major-only match?)
- Whether conditional_symbols is the FULL set XIL2CPP must respect or only a subset, and how it composes with XIL2CPP's built-in symbol set (e.g., `XPACT_SIM_PATH`)

**Fix**: Fingerprint fields with FBS ordinal hints (e.g., `schema_version: uint32 = 100; // ordinal N+1, append-only`) and define semver-rule for tooling version comparison.

---

### FIX-E-LOW-1 [severity: LOW]

**Location**: XIL2CPP.html §14.0.1 (all five items)

**Issue**: The five contradictions are listed as a bulleted prose paragraph, NOT as a table. Compared to §14.0's structured table with ID/required-by/description/origin columns, the contradiction list is harder to track in audits. Each contradiction would benefit from an explicit ID prefix (e.g., `CONTRA-1` through `CONTRA-5`) and a "resolution path" column (Rev 3 self-contained vs forward-commit vs deferred).

**Fix**: Format §14.0.1 as a table with columns: ContradictionID, Statement, Real?, Resolution path (Rev3 self / forward-commit / defer), Receiving rev.

---

### FIX-E-LOW-2 [severity: LOW]

**Location**: XIL2CPP.html §14.0 (all rows)

**Issue**: Forward-commit IDs use mixed naming conventions: XCXO-Rev5-FOO (5 dashes), XHT-Rev7-FOO, XBT-Rev11-FOO, Contract-Rev14-FOO, BCL-Rev2-FOO, XException-V1 (one V1 not Rev1), XPact.Sim-PRNG (no rev qualifier). Inconsistent.

**Fix**: Normalize to `<TargetSpec>-Rev<N>-<FeatureName>`: e.g., `XException-Rev1-V1`, `XPactSim-Rev1-PRNG`. Trivial cosmetic; helps grep.

---

### FIX-E-LOW-3 [severity: LOW]

**Location**: XIL2CPP.html §14.0 (table row count)

**Issue**: The audit prompt said "17 forward-commitments". The §14.0 table contains 18 rows. Auditor presumes the prompt was reading an earlier draft or miscounted. No defect — just a discrepancy with the prompt.

**Fix**: None for XIL2CPP.html. Note for the audit-coordination: the count is 18.

---

## End of Findings

Total: 22 findings (CRITICAL 4, HIGH 9, MEDIUM 6, LOW 3).

**Bottom-line forward-commit assessment**: Of 18 forward-commitments, 9 are well-formed (clear signature, location, rationale) and 9 are under-specified (vague enumeration, "etc." hand-waves, missing fallback discipline). ALL 18 lack a documented fallback if the receiving spec misses the rev — this is the single most important systematic gap (FIX-E-CRIT-4).

**Bottom-line contradiction assessment**: Of 5 contradictions Rev 2 flagged:
- #1 (lifecycle 7v8) is **stale/mis-stated**, not a real contradiction — strike it.
- #2 (Contract Rev 13.9 ABI tag count) is **REAL and worse than Rev 2 admits** — Contract.html is still at Rev 13.8 and was NEVER bumped, even though commit 72be4f8 implied a "Rev 13.9 prerequisite" applied. See FIX-E-CRIT-1 + FIX-E-CRIT-2.
- #3 (XHT NoThrow dual) is **REAL and consistent with the XHT-Rev7-NOTHROW-CPP forward-commit**.
- #4 (boxing wrappers) is **REAL and consistent with the BCL-Rev2-BOXING forward-commit, but under-specified there**. See FIX-E-HIGH-3.
- #5 (manifest schema_version vs contract_version) is **REAL and consistent with XBT-Rev11-MANIFEST-FIELDS**.

The single most load-bearing follow-up from this audit is FIX-E-CRIT-1: XToolchainContract.html needs an actual Rev 13.9 PR (not just the C++ headers that were already bumped in commit 72be4f8). Without it, XIL2CPP Rev 2's ABI envelope pins assert against a contract surface that does not exist in canonical form.
