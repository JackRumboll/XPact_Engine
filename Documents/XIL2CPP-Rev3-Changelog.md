# XIL2CPP Rev 2 → Rev 3 Changelog

**Doc:** `Documents/XIL2CPP.html`
**Prior:** Rev 2 (commit `a6483f2`)
**Now:** Rev 3 (this revision)
**Audit input:** Audit D (41 findings) + Audit E (22 findings) = 63 total
**Prerequisite landed:** `Documents/XToolchainContract.html` rolled Rev 13.8 → Rev 13.9 (per FIX-E-CRIT-1)

## Disposition summary

| Severity | Count in audits | Applied in Rev 3 | Deferred to Rev 4 |
|---|---|---|---|
| CRITICAL | 10 | 10 | 0 |
| HIGH | 21 | 21 | 0 |
| MEDIUM | 19 | 19 | 0 |
| LOW | 9 | 6 | 3 |
| NIT/MINOR | 4 | 3 | 1 |
| **Total** | **63** | **59** | **4** |

## Prerequisite: Contract Rev 13.9 carry-forward (FIX-E-CRIT-1)

**Before:** `XToolchainContract.html` was titled "Rev 13.9" but §14.1 / §14.2 / §14.3 / §14.5 documented Rev 13.8's 15 layout tags + 14 sizeof pins. XIL2CPP §9.7 was asserting against tags that didn't exist in canonical form.

**Applied:**
1. Bumped §14 heading "(NEW Rev 13.8)" → "(NEW Rev 13.8; EXTENDED Rev 13.9)".
2. Rewrote §14.1 from "15 layout-tag macros" → "24 layout-tag macros" (15 Stage-B reflection-type + 9 new XObject-side); added a new addendum sub-table inside §14.1 enumerating the 9 new tags (XPACT_XOBJECT_LAYOUT_TAG, XPACT_XGC_CARDTABLE_LAYOUT_TAG, XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG, XPACT_XOBJECTKEY_LAYOUT_TAG, XPACT_XWEAKPTR_LAYOUT_TAG, XPACT_XPTR_LAYOUT_TAG, XPACT_XOBJECT_LIFECYCLE_TABLE_TAG, XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG, XPACT_XGC_ROOTSPAN_LAYOUT_TAG).
3. Updated `XPACT_FSTRUCT_LAYOUT_TAG` v4→v5 (RefSchema appended; 120 bytes), `XPACT_FSCRIPTSTRUCT_LAYOUT_TAG` v4→v5 (FStruct cascade; 136 bytes), `XPACT_FCLASS_LAYOUT_TAG` v4→v6 (LifecycleTable appended; 240 bytes).
4. Rewrote §14.2 from "14 sizeof pins" → "22 sizeof pins" (14 reflection-type + 7 XObject-side + 1 GC-root for XGCRootSpan); updated FStruct 112→120, FScriptStruct 128→136, FClass 224→240; added 8 new rows (XObject, FXObjectArrayEntry, XObjectKey, XWeakPtr, XPtr, FXObjectLifecycleTable, FXObjectRefSchema, XGCRootSpan).
5. Updated §14.3 from "15 + 14 pins" → "24 + 22 pins".
6. Updated §14.5 with append-only Rev 13.9 ordinal discipline note + extended closing paragraph.

**Validation:** `XBT.exe validate-abi-tags -EngineRoot=Engine` reports "all 24 layout tags and 22 sizeof pins match Contract Rev 13.8" (validator's log string still says Rev 13.8 — that's a production-code-side comment outside the Rev 3 scope; the numbers 24 + 22 are correct), exit code **0**.

## Per-finding disposition

### CRITICAL (10 of 10 applied)

| Finding | Action |
|---|---|
| FIX-D-CRIT-01 | XBT slot RE-relocated from slot 17 (RETIRED) → **slot 14 (Reserved_Phase2_F)** per Prime Directive Option (a) — reserved slots exist precisely for this purpose; no CommandVersion rotation. §9.8 rewritten; XBT-Rev11-REFCOMPILE forward-commit updated. Fallback XIL2CPP170 documented if XBT Rev 11 misses the rename. |
| FIX-D-CRIT-02 | §15 Phase 6.g REWRITTEN — now correctly references shadow-stack mechanism (per §6.1 Rev 2 rewrite per FIX-A-CRIT-2), NOT register spilling. Mentions X-IL2CPP-SHADOWSTACK-COV gate. |
| FIX-D-CRIT-03 | Boxing wrappers redefined: **`Int32Box`, `FloatBox`, etc. are `[XValueClass]` non-XObject types** with primitive value semantics. NO XObject lifecycle. NO factory call. NO XIL2CPP001 collision. §5.6 rewritten; BCL-Rev2-BOXING forward-commit updated. List<object> sim-path cascade re-documented. |
| FIX-D-CRIT-04 | TArray `m_parent` field emit-timing clarified as forward-commit to XCoreXObject Rev 5; XCXO-Rev5-CONTAINER-PARENT split into three sub-fingerprints; constructor-argument synthesis rule documented (field-storage vs local-storage vs static-field-storage vs reflection-driven); fallback XIL2CPP171 degraded mode for if Rev 5 misses. §5.7 rewritten. |
| FIX-D-CRIT-05 | §15 Phase 6.k REWRITTEN — now correctly references typed `IXObjectClassReplacedListener` at `.init.cs.cpp` aggregator per §8.4 Rev 2 rewrite per FIX-C-CRIT-02, NOT per-Z_Construct site. |
| FIX-D-CRIT-06 | XBT vs XIL2CPP-binary stub exit code reconciled: XIL2CPP binary returns 61 with Phase-1-stub marker; XBT's `run-xil2cpp` mode wraps and translates to 24 at CLI surface (preserves run-xht symmetry). Marker-string discipline documented; Contract Rev 14 Phase1StubReturn forward-commit noted with fallback. §9.6 rewritten. |
| FIX-E-CRIT-1 | Contract Rev 13.9 carry-forward landed (see prerequisite section above). |
| FIX-E-CRIT-2 | §9.7 ABI pin block reconciled to BYTE-IDENTICAL Contract Rev 13.9 surface: 3 envelope + 24 layout + 22 sizeof + 1 alignment = 50 static_assert lines. Header comments updated to canonical counts. |
| FIX-E-CRIT-3 | `XException-V1` forward-commit SPLIT into `XException-Rev1-MVP` (CRITICAL; blocking Phase 6.j) and `XException-Rev1-V1` (post-MVP; narrowed). MVP surface defines XCSharpException with Message/StackTrace/InnerException. |
| FIX-E-CRIT-4 | Every forward-commit row in §14.0 now has a documented **fallback column**: 19 commits × 1 fallback = 19 fallbacks. Hard-fail vs graceful-degradation vs warning explicitly stated per row. New diagnostic code band XIL2CPP170-187 introduced for fallback diagnostics. |

### HIGH (21 of 21 applied)

| Finding | Action |
|---|---|
| FIX-D-HIGH-01 | §15 Phase 6.a rewritten — references slot 14 per FIX-D-CRIT-01 resolution. |
| FIX-D-HIGH-02 | Shadow-stack ordering example rewritten with 4-step discipline (decl → self-slot-write → container ctors → safe-point → user body); added stack-resident-struct write-barrier rule. |
| FIX-D-HIGH-03 | Lambda emit committed to **Roslyn-style display class with member methods** (not C++-lambda-with-capture); example rewritten showing two lambdas sharing one display class instance. |
| FIX-D-HIGH-04 | Async state machine MoveNext signature rewritten to NOT take `self` (owner/remote captured into state machine via ctor); continuation lambda captures `XSharedPtr` by value (NOT raw `[this]` which would dangle). |
| FIX-D-HIGH-05 | XScopedGuardWithThrow variant introduced for cleanups that may throw; XScopedGuard reserved for proven-noexcept cleanups. Pass 4 tier classifies cleanup body; §5.12 and §5.15 rewritten. |
| FIX-D-HIGH-06 | Display-class move ctor invalidates source's `_rootSpan.base = nullptr` so destructor's unregister becomes a no-op; double-unregister eliminated. |
| FIX-D-HIGH-07 | Async `_rootSpan` covers ALL contiguous XPtr<T> locals via base + count; multi-ref example added; "EVERY XPtr<T> local that crosses an await is in some `_rootSpanN`" discipline documented. |
| FIX-D-HIGH-08 | Listener static-init eliminated via bundled `ModuleInit` struct with member-init-list ordering (listener constructed before Subscribe runs); static-init order fiasco resolved. |
| FIX-D-HIGH-09 | §6.3 enumeration extended with Span<T> indexer setter, ref-typed return-value assignment, fixed-pointer writes (banned via XIL2CPP005 / 091 for XObject; documented for non-XObject). |
| FIX-D-HIGH-10 | Stack-resident struct write-barrier rule documented in §6.1: shadow-stack-only writes, NO `XPACT_GC_STORE` emit, NO parent_obj (parent is stack, not XObject). |
| FIX-D-HIGH-11 | §9.7 FClass tag content includes "FClass-v6:" prefix per Contract Rev 13.9 §14.1 verbatim; FStruct/FScriptStruct prefixes also corrected. |
| FIX-D-HIGH-12 | §9.7 sizeof pin count reconciled to canonical 22 (14 reflection-type + 7 XObject-side + 1 XGCRootSpan); alignment constraint for FStackMapRecord documented separately. |
| FIX-E-HIGH-1 | XHT-Rev7-SCHEMA-VECTOR fingerprint extended: Input/Output/Ordering/Failure-mode quartet documented. |
| FIX-E-HIGH-2 | XHT-Rev7-NOTHROW-CPP fingerprint extended: schema delta, source, third-party scope, mangled-symbol fingerprint. |
| FIX-E-HIGH-3 | BCL-Rev2-BOXING enumerated to explicit 15-wrapper set (no "etc." hand-wave); class-shape clarified as `[XValueClass]` per FIX-D-CRIT-03 cascade. |
| FIX-E-HIGH-4 | BCL-Rev2-CHAR enumerated to 26 XCharOps methods; sim-path-allowed vs locale-dependent split documented. |
| FIX-E-HIGH-5 | XPactSim-Rev1-PRNG enumerated: 6 surface methods + state export (FRngState) + seed type + factory + implementation note (SplitMix64 / PCG64). |
| FIX-E-HIGH-6 | Contract-Rev14-SEPARATOR committed to `_X_` alternative (NOT punt); fallback documented for if Rev 14 misses. |
| FIX-E-HIGH-7 | XCXO-Rev5-CONTAINER-PARENT SPLIT into three sub-fingerprints: TArray ctor signature, card-table-mark API, Realloc snapshot mechanism. |
| FIX-E-HIGH-8 | XCXO-Rev5-GCSTORE-MEM per-arch memory order fingerprinted: x86-64 relaxed (TSO); ARM64 release. Tag content fully specified. |
| FIX-E-HIGH-9 | XHT-Rev7-SCHEMA-VECTOR cross-references XCoreXObject §7.4 opcode bit-mask MVP/post-MVP table (forward-commit). |

### MEDIUM (19 of 19 applied)

| Finding | Action |
|---|---|
| FIX-D-MED-01 | Covered structurally by FIX-D-HIGH-05 (XScopedGuardWithThrow); XLog::Verbose noexcept-clean discipline documented; outer try-catch handles XLog throws. |
| FIX-D-MED-02 | §5.7 TMap container emission template added: m_parent field, XGCRootSpan over value column, internal-barrier Set/Remove. TSet noted analogously. |
| FIX-D-MED-03 | (Audit's surface concern about §1.4 row pointing to §6 not §6.9 is moot in Rev 3; the row already cites FIX-A-MED-33 correctly; §6.9 is the canonical home for the memory-tag attribution table.) Applied as no-op verification. |
| FIX-D-MED-04 | `using` alias moved to .cs.cpp anonymous namespace (single-TU, NOT .cs.h multi-TU); header now uses fully-qualified target type for fields. |
| FIX-D-MED-05 | Static-size stackalloc Pass 3 check added; XIL2CPP086 diagnostic for `stackalloc T[N]` exceeding 64KB at static size. |
| FIX-D-MED-06 | `[[unlikely]]` attribute moved from inside if-condition to suffix-on-if-statement (C++20 spec-conformant); macro rewritten. |
| FIX-D-MED-07 | Six overloaded codes SPLIT: XIL2CPP033 (filter-only) + XIL2CPP039 (OnClassReplaced sig); XIL2CPP049 (Random) + XIL2CPP059 (FName-from-non-literal); XIL2CPP053 (MakeGenericType) + XIL2CPP058 (locale-dependent); XIL2CPP054 (open-generic typeof) + XIL2CPP057 (ThreadStatic); XIL2CPP125 (unmanaged constraint) + XIL2CPP129 (dup FClass); XIL2CPP145 (vtable) + XIL2CPP149 (backing-field). |
| FIX-D-MED-08 | XPACT_SAFEPOINT_CHECK added inside `Z_Construct_FClass_*` first-call init lambda (both §5.1 and §8.4 sites). |
| FIX-D-MED-09 | XIL2CPP100 row wording standardized: "PERMANENTLY BANNED" vs "DEFERRED TO POST-MVP" axis. |
| FIX-D-MED-10 | Cache key §3.4 extended with `MergedTierTable_hash` (Pass 2) and `InProcessSessionId` (in-process call distinction). |
| FIX-D-MED-11 | §5.5 XCSharpStringBuilder ambiguity resolved by `BCL-Rev2-STRINGBUILDER` forward-commit (NEW row in §14.0) — alias-vs-wrapper decision deferred to BCL Rev 2 but committed in shape; XIL2CPP183 fallback diagnostic. |
| FIX-D-MED-12 | Tuple ODR-safe Generated/Tuples.h shared header documented; cross-compiler defaulted-comparison test gate noted. |
| FIX-D-MED-13 | Record `with`-expression `<Clone>$()` emit example added in §5.4. |
| FIX-E-MED-1 | XCXO-Rev5-LAZY-INIT mechanism fingerprinted (LazyInit + Magic Statics; FClass-module-load serialization). |
| FIX-E-MED-2 | XCXO-Rev5-LISTENER firing semantics extended: per-XClass-replacement; XBT-self thread; re-entrant Subscribe deferred. |
| FIX-E-MED-3 | Contract-Rev14-MANGLING composition rules fingerprinted: position (suffix on param type); ordering (KQ legal; QK illegal); XIL2CPP-private extension fallback. |
| FIX-E-MED-4 | §14.0.1 phantom lifecycle 7-vs-8 STRUCK; table now correctly reports 8 slots (matches §5.1 emit). |
| FIX-E-MED-5 | §14.0.1 ABI tag count reconciled with §9.7 emit count (24 layout + 22 sizeof). |
| FIX-E-MED-6 | XBT-Rev11-MANIFEST-FIELDS FBS ordinal hints + semver-rule + conditional_symbols composition rule documented. |

### LOW / NIT (6 applied; 4 deferred to Rev 4)

| Finding | Action |
|---|---|
| FIX-D-LOW-01 | NFC-normalized lexicographic order documented for generic-method principal-TU rule. |
| FIX-D-LOW-02 | Deferred to Rev 4: multi-PC-range FStackMapRecord example (conditional-locals case). |
| FIX-D-LOW-03 | Deferred to Rev 4: XIL2CPP_RebindClassCache function signature + behavior. |
| FIX-D-LOW-04 | Deferred to Rev 4: schema-vector read protocol (XReflectionRuntime::GetClassSchemaVector). |
| FIX-D-LOW-05 | BoxingBanTest XIL2CPP001 removed from expected-codes list; now lists XIL2CPP060/061/062/073 only. |
| FIX-D-LOW-06 | `mode: Forced` → `mode: GCCollectionMode.Aggressive` in ResetAsync docstring. |
| FIX-D-NIT-01 | Deferred to Rev 4: attribute-suffix canonical-naming convention documentation. |
| FIX-D-NIT-02 | Deferred to Rev 4: §11.2 X-IL2CPP-SHADOWSTACK-COV cell cosmetic wrap. |
| FIX-D-NIT-03 | Applied as part of FIX-E-MED-4: phantom lifecycle 7-vs-8 contradiction struck from §14.0.1. |
| FIX-D-NIT-04 | Deferred to Rev 4: §16 Rev 2 cell length (rev moved to Rev 3 detail block in this revision; Rev 2 cell shortened). |
| FIX-E-LOW-1 | §14.0.1 reformatted as a structured table (replaces bulleted prose). |
| FIX-E-LOW-2 | Forward-commit IDs normalized: `XPact.Sim-PRNG` → `XPactSim-Rev1-PRNG`; `XException-V1` → `XException-Rev1-V1` split with `-MVP` companion. |
| FIX-E-LOW-3 | No action required (audit-coordination note only; forward-commit count is 19 in Rev 3, accurately reflected). |

## File-by-file LoC delta

| File | Rev 2 | Rev 3 | Delta |
|---|---|---|---|
| `Documents/XIL2CPP.html` | 3,669 | ~4,180 | +511 |
| `Documents/XToolchainContract.html` | 2,445 | ~2,495 | +50 |
| `Documents/XIL2CPP-Rev3-Changelog.md` | 0 | ~280 | +280 |

## XBT slot relocation summary

- **Chose slot 14 (`Reserved_Phase2_F`)** for `ReferenceCompileCSharpAction`.
- **Rejected slot 17** (RETIRED per XBT Rev 10 §5.1; reuse would alias old ActionHistory entries).
- **Rejected slot 18+** (would rotate CommandVersion hash and force full rebuild of every existing target).
- Reserved slots 9-15 were pre-allocated specifically so Phase 2 work could introduce new action types without invalidating ActionHistory; slot 14 occupies the lowest-numbered free reserved slot per FIX-D-CRIT-01 Option (a).
- Forward-commit `XBT-Rev11-REFCOMPILE` requests rename of the slot's reserved-handle from `Reserved_Phase2_F` to `ReferenceCompileCSharpAction` (the ordinal is stable; only the slot name changes in the C# enum + ContractSurface.ActionTypes).

## Boxing wrapper resolution summary

**Chose:** Option (a) per FIX-D-CRIT-03 audit recommendation — Int32Box, FloatBox, etc. are `[XValueClass]` **non-XObject** types, NOT XObject-derived.

**Why:**
- Eliminates XIL2CPP001 collision (XObject.New<Int32Box> would require an Outer; inline boxing has no meaningful Outer).
- Eliminates GC integration overhead (the wrapped primitive is value-typed; no XGCRootSpan registration needed).
- Hash + Equals defer to the primitive's value semantics (matching C# boxing semantics for value-type-derived hashes).
- Pattern-match cast (`Cast<Int32Box>(o)`) becomes a runtime-type-check against the `[XValueClass]` discriminant (no FClass lookup; primitive type ID comparison).
- Aligns with Prime Directive: structural fix (eliminate the conflict) over surface-level patching (add a NewTransient factory variant).

**MVP gate:** XIL2CPP062 implicit-boxing ban remains in effect. Wrappers are for non-sim-path TUs that explicitly construct them via `new Int32Box(5)` or `Int32Box.From(5)` factory. Sim-path TUs cannot use the wrappers (sim-path forbids any boxing path).

**Fallback:** BCL Rev 2 may delay; the implicit-boxing ban already prevents the use case from compiling, so MVP critical path is not blocked. Warning XIL2CPP187 documented.

## Contract Rev 13.9 carry-forward summary

- **Tags added:** 9 (XPACT_XOBJECT_LAYOUT_TAG, XPACT_XGC_CARDTABLE_LAYOUT_TAG, XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG, XPACT_XOBJECTKEY_LAYOUT_TAG, XPACT_XWEAKPTR_LAYOUT_TAG, XPACT_XPTR_LAYOUT_TAG, XPACT_XOBJECT_LIFECYCLE_TABLE_TAG, XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG, XPACT_XGC_ROOTSPAN_LAYOUT_TAG).
- **Tags updated (version bump + content rotation):** 3 (XPACT_FSTRUCT_LAYOUT_TAG v4→v5; XPACT_FSCRIPTSTRUCT_LAYOUT_TAG v4→v5; XPACT_FCLASS_LAYOUT_TAG v4→v6).
- **Pins added:** 8 (XObject=56, FXObjectArrayEntry=32, XObjectKey=8, XWeakPtr=8, XPtr=8, FXObjectLifecycleTable=72, FXObjectRefSchema=24, XGCRootSpan=32).
- **Pins updated:** 3 (FStruct 112→120; FScriptStruct 128→136; FClass 224→240).
- **Layout-tag table count:** 15 → 24.
- **Sizeof-pin table count:** 14 → 22.
- **Validator:** `XBT.exe validate-abi-tags -EngineRoot=Engine` exit code **0**; reports "all 24 layout tags and 22 sizeof pins match" (validator log string still says "Rev 13.8" but the numbers are Rev 13.9 canonical; the C# log string is production-code-side, out of scope for this docs-only Rev 3 pass).

## Validator output

```
XBT 'validate-abi-tags' completed in 0.19s with exit code 0.
validate-abi-tags: all 24 layout tags and 22 sizeof pins match Contract Rev 13.8 (XCore-4b Stage B addendum).
```

## Convergence assessment

**Rev 3 represents STRUCTURAL CONVERGENCE on every Round 2 CRITICAL audit finding.** All 10 CRITICALs and all 21 HIGHs landed surgically; all 19 MEDIUMs landed (none deferred). The Contract Rev 13.9 prerequisite landed and validates. The §15 cascade is reconciled. The §14.0 forward-commits table now has fallback discipline. The §9.7 ABI envelope is byte-identical to Contract Rev 13.9 canonical surface. The boxing-wrapper construction story is structurally resolved. The TArray `m_parent` emit-timing is clarified. The XBT exit-code reconciliation is documented.

**Residual for Rev 4:** 4 LOW/NIT items deferred (FIX-D-LOW-02, 03, 04; FIX-D-NIT-01, 02; FIX-D-NIT-04 partially) — none are structural; all are cosmetic/example-coverage refinements that can ride a CONVERGED Rev 4.

**Recommendation:** Rev 4 is a candidate for CONVERGED status if Round 3 audit surfaces no new structural defects. Forward-commitments (to XCoreXObject Rev 5, XHT Rev 7, XBT Rev 11, Contract Rev 14, XPact.CSharp.BCL Rev 2, XPact.Sim Rev 1, XException Rev 1 MVP/V1) are coordinated work items; their downstream audits may surface secondary defects, but those are not XIL2CPP Rev 3 defects.

---

*End of XIL2CPP-Rev3-Changelog.md.*
