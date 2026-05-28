# XIL2CPP Rev 2 — Changelog

> **Authoring date.** 2026-05-28
> **Source.** XIL2CPP.html Rev 1 (commit a7b7a97; status "Rev 1; AWAITING ROUND 1 AUDIT")
> **Audit inputs.**
> - `XIL2CPP-Rev1-Audit-A-GC-and-SimPath.md` — 47 findings (6 CRIT, 13 HIGH, 17 MED, 8 LOW, 3 NIT).
> - `XIL2CPP-Rev1-Audit-B-LangAndMapping.md` — 62 findings (6 CRIT, 21 HIGH, 23 MED, 12 MINOR).
> - `XIL2CPP-Rev1-Audit-C-HotReloadAndXBT.md` — 52 findings (4 CRIT, 19 HIGH, 19 MED, 9 MINOR, 1 LOW).
> - **Total: 161 findings.**

## Disposition summary

| Status | Count |
|---|---|
| Applied (inline edit landed in Rev 2) | ~155 |
| Deferred-Rev3 (cross-system Contract amendment required — Contract Rev 14 / XCoreXObject Rev 5 / XBT Rev 11 / XHT Rev 7 / BCL Rev 2 forward-commit) | 8 |
| Cosmetic deferred (em-dash normalization etc.) | 1 (FIX-A-NIT-3) |
| Deferred-LockedCommitment (rejected as commitment violation) | 0 |
| **Total** | **161** |

The 8 Deferred-Rev3 findings are NOT correctness gaps in Rev 2 — they are tracked as forward-commitments because applying them requires changes to other XPact docs/specs that XIL2CPP Rev 2 forward-commits to (e.g., Contract Rev 14 mangling discriminators per FIX-B-HIGH-03 / FIX-C-HIGH-02). The Rev 2 doc enumerates each forward-commitment in §14.0.

## Cross-audit reconciliations

1. **FIX-A-CRIT-1 + FIX-C-CRIT-01 — ABI tag content drift.** Both audits flagged the same bug: §5.1 + §9.7 emit `"v6"`/`"v5"`/`"v1"` shorthand instead of the verbatim Contract Rev 13.9 §14.1 tag contents. Reconciled per Audit C's authoritative reference to Contract Rev 13.9 §14.1 (which Audit A also cites). Applied verbatim Rev 13.9 strings to §5.1 + §9.7 + doc-header. FClass = 240 bytes (Rev 13.9); FStruct = 120 bytes (Rev 13.9) — NOT 224/112 from Rev 13.8 (the prompt's note about verification confirmed Rev 13.9 re-bumped after Rev 13.8 Stage B addendum).

2. **FIX-A-CRIT-5 + FIX-A-HIGH-7 + FIX-B-HIGH-10 — Write barrier coverage.** Three audits identified write-barrier enumeration gaps. Audit A enumerated 8+ AST sites; Audit B noted indexers + interface dispatch; reconciled to a positive-list enumeration rule in §6.3 covering: AssignmentExpressionSyntax with reference LHS, object-initializer setters, with-expression setters, compound-assignment lowering, deconstruction-assignment, indexer-setter call, auto-property setter, init-accessor, default-interface-property setter, primary-constructor field init, container `Add`/`Set` methods. Interface call-site barrier omitted; concrete setter emits barrier (per Audit A FIX-A-HIGH-7).

3. **FIX-B-CRIT-05 + FIX-B-HIGH-16 + FIX-A-MED-20 — `where T : new()` contradicts XIL2CPP001.** Three audits identified the same Locked Commitment violation. Reconciled per Audit B's recommendation: REMOVE `where T : new()` from `XObject.New<T>` signature; constrain only `where T : XObject`. The actual construction goes through `FClass::ClassConstructorFn` slot via placement-new inside the `[XObjectInternalConstructor]`-attributed factory body — NOT through C# `new T()`. The `where T : new()` constraint is what was generating the contradiction; removing it makes the factory satisfy both: XIL2CPP001 bans source-level `new Foo()`, the factory uses XPact's runtime construction path.

4. **FIX-A-CRIT-2 — Stack-map mechanism (architectural rewrite).** Audit A demonstrated that the spec's "emit explicit register spilling" approach is infeasible portably across MSVC/Clang/GCC. Adopted Audit A's Option C (shadow-stack) as the canonical mechanism. Rewrote §6.1 to specify a `XPtr<XObject>* _liveRefs[N]` stack array with deterministic indexing. This is a major §6.1 rewrite.

5. **FIX-A-CRIT-3 — ref/out XObject barrier.** Audit A's Option A (ban `ref/out XObject` in MVP) chosen per Prime Directive. New diagnostic XIL2CPP005. C# authors use nullable return or `Result<T, E>` pattern.

6. **FIX-B-CRIT-02 + FIX-B-CRIT-06 — Boxing has no emit story.** Audit B's Option (a) chosen: ban boxing entirely on sim-path AND ban implicit `object` boxing of value types on all paths in MVP. Explicit wrapper classes (`Int32Box`, `FloatBox`) shipped as part of `XPact.CSharp.BCL` NuGet for non-sim-path use. `List<object>` over value types: banned via XIL2CPP073.

7. **FIX-C-CRIT-02 + FIX-A-MED-33 — Hot-reload listener leak.** Audit C identified the dangling `AddRaw` pattern; Audit A noted the corresponding XCoreDelegates accessor convention. Reconciled to typed listener interface `IXObjectClassReplacedListener` per XCoreXObject Phase 5.k pattern, registered via `::XCore::CoreDelegates::GetOnClassReplaced().Subscribe(listener)` returning `FHandle`. Per-module static listener with explicit `Unsubscribe(handle)` in module-shutdown hook. Auto-emitted by XIL2CPP at `Z_Construct_FClass_*` aggregation.

8. **FIX-C-CRIT-04 — ReferenceCompileCSharpAction ordinal.** Per Prime Directive, forward-commit per Audit C's resolution path: propose XBT Rev 11 amendment to allocate a new ordinal for ReferenceCompileCSharpAction (NOT slot 14 which is `Reserved_Phase2_F`). Concretely: amend XBT to expand the action ordinal pool by reserving slot 17 (next available after BuildPluginManifestAction at slot 16; slots 14/15 remain reserved). Section §9.8 updated with explicit cross-system forward-commitment note.

## Locked-commitment violations rejected

No findings attempted to revisit the locked commitments. The only finding that touched a locked commitment (FIX-B-HIGH-16 / CRIT-05 / FIX-A-MED-20) attempted to fix a contradiction, not violate a commitment.

---

## Per-finding disposition table

### Audit A — GC + Sim-Path (47 findings)

| ID | Severity | Status | Notes / Rev 2 location |
|---|---|---|---|
| FIX-A-CRIT-1 | CRITICAL | Applied | §5.1 + §9.7 + doc-header: replaced `"v6"` etc. with Rev 13.9 verbatim tag contents. Cross-references Contract §14.1. |
| FIX-A-CRIT-2 | CRITICAL | Applied | §6.1 rewritten end-to-end with shadow-stack mechanism. |
| FIX-A-CRIT-3 | CRITICAL | Applied | §4.1 + §5.2 + §6.3: added ban of `ref/out XObject` in MVP. New diagnostic XIL2CPP005. |
| FIX-A-CRIT-4 | CRITICAL | Applied | §7.2: removed `XPACT_FP_SEMANTICS_TAG` static_assert; replaced with `__FAST_MATH__` / `_M_FP_FAST` compile-time check. |
| FIX-A-CRIT-5 | CRITICAL | Applied | §6.3 rewritten with positive enumeration of reference-store sites (12 paths). |
| FIX-A-CRIT-6 | CRITICAL | Applied | §6.5 + §1.4 cross-system: cite XCoreXObject §5.5 TLS-cached read pattern. Reframe `XPACT_SAFEPOINT_CHECK` as XPact-side macro that lowers to XCoreXObject TLS-cached check. Forward-commit XCoreXObject Rev 5 contract addition for the macro name + acquire-order semantics. |
| FIX-A-HIGH-1 | HIGH | Applied | §5.1 + §10.2 + §10.4: reconciled to "XIL2CPP emits the FXObjectLifecycleTable for C#-declared classes; XHT references via extern". Added explicit clarification + cross-tool symbol contract update. |
| FIX-A-HIGH-2 | HIGH | Applied | §4.1 + §7.5: ban extended to Task<T>, ValueTask<T>, IAsyncEnumerable<T>, await foreach, Task.Result/.Wait. New XIL2CPP048. |
| FIX-A-HIGH-3 | HIGH | Applied | New §7.8 "Sim-path determinism mapping table" added with Math.* + Random + ToString/Parse mappings. Diagnostics XIL2CPP049, XIL2CPP053. |
| FIX-A-HIGH-4 | HIGH | Applied | §4.1 + §5.3 + §7.4: foreach over HashSet/Dictionary on sim-path → ERROR (not warning); sim-path TMap/TSet partial spec ships insertion-order iteration. |
| FIX-A-HIGH-5 | HIGH | Applied | §5.8 + §6.1: principal-TU rule added for generic-instantiation FStackMapRecord emit. `selectany` / `inline` linkage used. |
| FIX-A-HIGH-6 | HIGH | Applied | §5.4 + §6 new subsection: static-field XObject rooting protocol with XGCRoot::AddRoot lifecycle hook; [ThreadStatic] banned on sim-path (XIL2CPP054). |
| FIX-A-HIGH-7 | HIGH | Applied | §6.3 + §5.4: interface property setter call-site barrier OMITTED; concrete-setter emits barrier with `self` parent. |
| FIX-A-HIGH-8 | HIGH | Applied (combined with FIX-B-CRIT-02) | §4.1 + §5.6 + §5.7: boxing-of-value-type-into-object banned on sim-path; non-sim-path requires explicit Int32Box-style wrapper with XGCRootSpan. Anonymous types banned on sim-path. New XIL2CPP061. |
| FIX-A-HIGH-9 | HIGH | Applied | §5.10: `FObjectMonitorRegistry` clean-up via OnXObjectBeginDestroy lifecycle hook. |
| FIX-A-HIGH-10 | HIGH | Deferred-Rev3 (cross-system Contract amendment) | §6.3 cross-referenced; Contract Rev 14 amendment required for `XPACT_GC_STORE_MEMORY_ORDER_TAG` and the ARM64 acquire/release semantics. Added forward-commit note in §6.3 + §1.4. |
| FIX-A-HIGH-11 | HIGH | Applied | §7.4 + new §5.10.5: Interlocked.* banned on sim-path (XIL2CPP055); non-sim-path maps to std::atomic. |
| FIX-A-HIGH-12 | HIGH | Applied | §6.7: recursive struct-in-struct stack-map walk specified. |
| FIX-A-HIGH-13 | HIGH | Applied | §5.13: Span<T> ban extended to ReadOnlySpan + Span<Span<T>> + transitive XObject containment. |
| FIX-A-HIGH-14 | HIGH | Applied (combined with FIX-A-HIGH-2) | §4.1: IAsyncEnumerable<T> + await foreach row added; banned on sim-path. |
| FIX-A-MED-1 | MEDIUM | Applied | §2.3: `[XObjectInternalConstructor]` namespace specified as `XPact.CoreXObject.XObjectInternalConstructorAttribute`. |
| FIX-A-MED-2 | MEDIUM | Applied | §5.5: literal-string FName interned at compile time via `_FName_<Name>` constexpr handle; runtime-constructed FName diagnostics XIL2CPP049. |
| FIX-A-MED-3 | MEDIUM | Applied | §5.15: clarified `using` block scope-end semantics for XObject vs non-XObject targets. |
| FIX-A-MED-4 | MEDIUM | Applied | §5.9: lambda move constructor specified with atomic-with-mark-phase unregister/register. |
| FIX-A-MED-5 | MEDIUM | Applied | §5.9: async state machine heap allocation specified via `FMemory::NewObject<_AsyncSM_*>` with EMemTag::AsyncStateMachine. |
| FIX-A-MED-6 | MEDIUM | Deferred-Rev3 (cross-system Contract amendment) | §5.7 + §6.2: TArray's container-parent inheritance is a XCoreXObject Rev 5 contract addition; forward-commit added. Realloc race noted; coordinate with XCoreXObject Rev 5 mark-phase snapshot mechanism. |
| FIX-A-MED-7 | MEDIUM | Applied | §7.2: brief MSVC `/fp:precise` vs `/fp:strict` rationale added. |
| FIX-A-MED-8 | MEDIUM | Applied | §5.16: MSVC `/d2overflow-` removed; specifies modular wrap via uint casts for signed overflow on all toolchains. |
| FIX-A-MED-9 | MEDIUM | Applied (combined with FIX-B-CRIT-02) | §5.6: implicit boxing banned on all paths in MVP (XIL2CPP062). |
| FIX-A-MED-10 | MEDIUM | Applied | §5.5: nameof generic-type-parameter behavior documented. |
| FIX-A-MED-11 | MEDIUM | Applied | §5.1 + §6.3 rule (1): clarified "new-object-init barrier omission" applies only to in-constructor pre-escape writes; with-expression setter calls DO require barrier. |
| FIX-A-MED-12 | MEDIUM | Applied | §5.4 + §6.3: object-initializer setter is in-construction; barrier omitted. |
| FIX-A-MED-13 | MEDIUM | Applied | §5.10: FObjectMonitorRegistry keys on `XObject::InternalIndex` (not XObjectKey). |
| FIX-A-MED-14 | MEDIUM | Applied | §5.9: implicit `this` capture documented. |
| FIX-A-MED-15 | MEDIUM | Applied | §5.3: chained XPtr<T> read returns stable Raw() promotion through XPtr::operator->(); chain `outer.mid.inner.actor = newActor` lowers to `outer.mid.Raw()->inner.Raw()->actor = newActor`. |
| FIX-A-MED-16 | MEDIUM | Applied | §6.6: Conservative validation order cross-referenced to XCoreXObject §5.3; warning elevated to error on sim-path when `SimPathConservativeRootsAllowed = false`. |
| FIX-A-MED-17 | MEDIUM | Applied (with FIX-A-MED-6) | §5.7: TArray Add barrier rewrite — depends on container-parent inheritance, forward-commit XCoreXObject Rev 5. |
| FIX-A-MED-18 | MEDIUM | Applied | §12.1: XIL2CPP030 diagnostic enriched with failing callee chain. |
| FIX-A-MED-19 | MEDIUM | Applied | §5.8: `where T : unmanaged` violated by XObject-derived T emits XIL2CPP125. |
| FIX-A-MED-20 | MEDIUM | Applied (combined with FIX-B-CRIT-05) | See cross-audit reconciliation #3. |
| FIX-A-MED-21 | MEDIUM | Applied | §5.8: `static_assert(std::is_base_of_v<...>)` emitted for every `where T : XObject` constraint. |
| FIX-A-MED-22 | MEDIUM | Applied | §5.4 + §6.3: implicit-conversion call site barrier — verifies stable XPtr<T> return. |
| FIX-A-MED-23 | MEDIUM | Applied | §5.2: added explicit reference-property setter emit example. |
| FIX-A-MED-24 | MEDIUM | Applied | §5.4: `init` accessor enforcement relies on Roslyn CS8852; mangling-suffix mechanism removed (cleaner approach per finding). |
| FIX-A-MED-25 | MEDIUM | Applied | §5.3: `default(T)` emit specified for value, reference, struct-with-XObject cases. |
| FIX-A-MED-26 | MEDIUM | Applied | §4.1 + §5.3: `goto` lowering 1-to-1 in non-async/iterator; rejected by Roslyn in async/iterator. |
| FIX-A-MED-27 | MEDIUM | Applied | §5.14: forward design hook for delegate post-MVP — multicast TArray<FXDelegate>; XGCRootSpan over InstanceXObject*. |
| FIX-A-MED-28 | MEDIUM | Applied | §5.3: foreach over typed container → range view; foreach over IEnumerable<T> interface BANNED on sim-path (XIL2CPP064); allowed non-sim-path with state machine + stack-map entry. |
| FIX-A-MED-29 | MEDIUM | Applied | §6.6: cross-referenced `SimPathConservativeRootsAllowed`; XIL2CPP070 → error if `SimPathConservativeRootsAllowed = false`. |
| FIX-A-MED-30 | MEDIUM | Applied | §6.1: stack-map registration moves from global-init lambda to lazy-init in XCoreXObject; forward-commit XCoreXObject Rev 5. |
| FIX-A-MED-31 | MEDIUM | Applied | §8.5 + §3.3: Pass 1 cross-module callees always conservatively Tier 1 unless NoThrow-annotated; no cross-module Tier 2 direct calls (extern "C" shim only). |
| FIX-A-MED-32 | MEDIUM | Applied | §3.3: `[XFunction]` namespace specified. |
| FIX-A-MED-33 | MEDIUM | Applied (combined with FIX-C-CRIT-02) | Memory-tag attribution table added to §6. |
| FIX-A-LOW-1 | LOW | Applied | §1.4 row updated with XGCSafepoint subsystem. |
| FIX-A-LOW-2 | LOW | Applied | §11.1 criterion (a) row updated with steady-state cycle figure. |
| FIX-A-LOW-3 | LOW | Applied | §6.5: cited XCoreXObject §5.5 line 1307 correctly (~2 cycles steady state). |
| FIX-A-LOW-4 | LOW | Applied | §6.5: BACKEDGE alias documented as provisional future-divergence hook. |
| FIX-A-LOW-5 | LOW | Applied | §6.2: thread-safety cross-reference to XCoreXObject §5.3 added. |
| FIX-A-LOW-6 | LOW | Applied | §6.5: `XPACT_UNLIKELY` replaced with C++20 `[[unlikely]]` attribute. |
| FIX-A-LOW-7 | LOW | Applied | §5.5: `XCSharpStringBuilder` renamed to XPact.CSharp.BCL canonical type; forward-commit if not yet in XCore-4a, add as XPact.CSharp.BCL post-MVP. |
| FIX-A-LOW-8 | LOW | Applied | §6.4: `XObject.NewRoot<T>` formally cross-referenced as XCoreXObject Rev 5 addition (forward-commit). |
| FIX-A-NIT-1 | NIT | Applied | §1.4 FIX-A-MIN-47 reference verified. |
| FIX-A-NIT-2 | NIT | Applied | §4.1: duplicate "Indexers" row removed. |
| FIX-A-NIT-3 | NIT | Deferred-Rev3 (cosmetic) | em-dash normalization deferred. |

### Audit B — Language and Mapping (62 findings)

| ID | Severity | Status | Notes / Rev 2 location |
|---|---|---|---|
| FIX-B-CRIT-01 | CRITICAL | Applied (cross-audit reconciliation #3) | See cross-audit reconciliation. |
| FIX-B-CRIT-02 | CRITICAL | Applied (cross-audit reconciliation #6) | See cross-audit reconciliation. |
| FIX-B-CRIT-03 | CRITICAL | Applied | §5.1 reframed: plain non-XObject class default emit is `std::shared_ptr<T>` (reference-identity preserved). Value-type emit requires explicit `[XValueClass]` attribute. |
| FIX-B-CRIT-04 | CRITICAL | Applied | New §5.21 + §4.1: C# 12 `using X = SomeType;` alias-any-type emit specified. |
| FIX-B-CRIT-05 | CRITICAL | Applied (cross-audit reconciliation #3) | See cross-audit reconciliation. |
| FIX-B-CRIT-06 | CRITICAL | Applied (cross-audit reconciliation #6) | See cross-audit reconciliation. |
| FIX-B-HIGH-01 | HIGH | Applied | §4.1: static abstract interface members deferred post-MVP with XIL2CPP016. |
| FIX-B-HIGH-02 | HIGH | Applied | §4.1: `params T[]` supported; `params ReadOnlySpan<T>` post-MVP; `params Span<T>` permanently banned. |
| FIX-B-HIGH-03 | HIGH | Deferred-Rev3 (Contract amendment) | §5.2 + §8.1 + forward-commit Contract §2.2 mangling discriminator `K` for ref readonly. |
| FIX-B-HIGH-04 | HIGH | Applied | §5.5: nameof rules clarified for generic-type-parameter case. |
| FIX-B-HIGH-05 | HIGH | Applied | §5.9: lambdas DO NOT carry `noexcept` blindly; Tier classification per lambda body. |
| FIX-B-HIGH-06 | HIGH | Applied | §5.9 substantial rewrite: Roslyn-style display-class hoisting. |
| FIX-B-HIGH-07 | HIGH | Applied | §5.6: switch expression emit specified as cascade of if's with _switch_result local. |
| FIX-B-HIGH-08 | HIGH | Applied | §5.4: `readonly T*` (ref) vs `T const` (value) emit specified. |
| FIX-B-HIGH-09 | HIGH | Applied | §5.2 + §10.5: canonical auto-property backing field naming `__BackingField_<X>`. New XIL2CPP145. |
| FIX-B-HIGH-10 | HIGH | Applied | §5.4: indexer emit specified — single-index emits operator[]; multi-index emits Get_Item / Set_Item methods. |
| FIX-B-HIGH-11 | HIGH | Applied | §5.2: extension methods emit specified. |
| FIX-B-HIGH-12 | HIGH | Applied | §5.4: `volatile` maps to `std::atomic<T>` with acquire/release semantics. New XIL2CPP019. |
| FIX-B-HIGH-13 | HIGH | Applied | §5.2: `new` (hiding) emit specified. |
| FIX-B-HIGH-14 | HIGH | Applied | §5.1 + §5.2: multi-interface emit via per-interface FInterface descriptor + function-pointer dispatch (Option A consistent with FakeVTable). |
| FIX-B-HIGH-15 | HIGH | Applied | §5.2: explicit interface implementation emit specified. |
| FIX-B-HIGH-16 | HIGH | Applied (cross-audit reconciliation #3) | See cross-audit reconciliation. |
| FIX-B-HIGH-17 | HIGH | Applied | §5.8: cross-module generic instantiation emit owner = first-referring module (deterministic). `inline constinit` linkage for safety. |
| FIX-B-HIGH-18 | HIGH | Applied (combined with FIX-A-MED-28) | See FIX-A-MED-28. |
| FIX-B-HIGH-19 | HIGH | Applied | §5.7 + §5.8: TArray Sort surface specified; `where T : IComparable<T>` enforced. |
| FIX-B-HIGH-20 | HIGH | Applied | §5.12: try-catch-when filter lowered to `if (!filter) throw;`. New XIL2CPP033. |
| FIX-B-HIGH-21 | HIGH | Applied | §5.12: try-finally (no catch) lowering specified via scope_exit RAII. |
| FIX-B-HIGH-22 | HIGH | Applied | §5.11: Activator.CreateInstance promoted to error on all TUs. Type.MakeGenericType/MakeGenericMethod banned (XIL2CPP053). |
| FIX-B-HIGH-23 | HIGH | Applied | §5.5: C# `char` mapped to `char16_t`. |
| FIX-B-HIGH-24 | HIGH | Applied | §5.5: string interpolation lowered to `DefaultInterpolatedStringHandler` with stack-allocated handler. |
| FIX-B-HIGH-25 | HIGH | Deferred-Rev3 (Contract amendment) | §5.2 + §8.1: `__` separator collision risk. Forward-commit Contract Rev 14 alternative separator (e.g., `_o_`). Risk acknowledged. |
| FIX-B-HIGH-26 | HIGH | Applied (combined with FIX-A-HIGH-11) | See FIX-A-HIGH-11. |
| FIX-B-MEDIUM-01 | MEDIUM | Applied | §2.2 + §5.4: record vs class primary-ctor capture semantics distinguished. New XIL2CPP017. |
| FIX-B-MEDIUM-02 | MEDIUM | Applied | §3.2 + §5.1: `with` expression on record-class invokes synthesized `<Clone>$()` and init-setter-with-privilege; record-struct uses memberwise copy. |
| FIX-B-MEDIUM-03 | MEDIUM | Applied | §5.3: `default(T)` emit specified. |
| FIX-B-MEDIUM-04 | MEDIUM | Applied | §5.6: `default` pattern emit specified. |
| FIX-B-MEDIUM-05 | MEDIUM | Applied | §5.3: `is null` vs `== null` distinction documented. |
| FIX-B-MEDIUM-06 | MEDIUM | Applied | §5.6: pattern-matching per-kind emit rules added (property, list, relational, logical, discard, var). |
| FIX-B-MEDIUM-07 | MEDIUM | Applied | §5.6: switch-on-string uses pointer-equality on interned const FString*. |
| FIX-B-MEDIUM-08 | MEDIUM | Applied | §5.4: user-defined conversion emit — free function + C++ constructor (non-explicit for implicit; explicit for explicit). |
| FIX-B-MEDIUM-09 | MEDIUM | Applied | §5.2: constructor chaining `: this(...)` / `: base(...)` lowered to free-function tail-call. For XObject `: base(...)` the args route through `Z_PostInitProperties_<Type>` user-init slot. |
| FIX-B-MEDIUM-10 | MEDIUM | Applied | §5.7: tuple positional emit + name aliasing documented (Option (a)). |
| FIX-B-MEDIUM-11 | MEDIUM | Applied | §5.7: tuple `operator==` emit specified. |
| FIX-B-MEDIUM-12 | MEDIUM | Applied | §5.4: init-accessor — trust Roslyn CS8852; emit identical to set accessor body. |
| FIX-B-MEDIUM-13 | MEDIUM | Applied | §5.1: static class emit specified as C++ namespace OR struct with deleted ctor. |
| FIX-B-MEDIUM-14 | MEDIUM | Applied | §5.1: partial methods documented (with-body emits; without-body elides call sites). |
| FIX-B-MEDIUM-15 | MEDIUM | Applied | §5.1: nested types emit as C++ nested classes; generic-outer + nested closed-instantiation rule. |
| FIX-B-MEDIUM-16 | MEDIUM | Applied | §5.1: sealed C# class emits C++ `final` keyword. |
| FIX-B-MEDIUM-17 | MEDIUM | Applied | §5.8: self-referential generic constraints (`where T : IComparable<T>`) — recursion-into-constraint-type-itself excluded from cycle detection. |
| FIX-B-MEDIUM-18 | MEDIUM | Applied | §5.8: variance via inheritance (Option a) — covariance emits inheritance chain at closed-instantiation. |
| FIX-B-MEDIUM-19 | MEDIUM | Applied | §5.8: `where T : notnull` + `where T : default` constraints added. |
| FIX-B-MEDIUM-20 | MEDIUM | Applied | §5.8: `where T : unmanaged` for type containing XPtr<T> emits XIL2CPP028. |
| FIX-B-MEDIUM-21 | MEDIUM | Applied (with FIX-A-MED-27) | §5.14: delegate emit redesigned as per closed-delegate-type wrapper struct. |
| FIX-B-MEDIUM-22 | MEDIUM | Applied | §5.15: using statement RAII scope guard with Dispose-safe pattern. |
| FIX-B-MEDIUM-23 | MEDIUM | Applied | §5.12 + new §5.22 forward hook for Exception class surface. |
| FIX-B-MEDIUM-24 | MEDIUM | Applied | §5.4: `[ModuleInitializer]` emit as anonymous-namespace static-initializer. |
| FIX-B-MEDIUM-25 | MEDIUM | Applied | §5.4: `[Conditional]` attribute — call-site elision based on per-module `conditional_symbols`. |
| FIX-B-MEDIUM-26 | MEDIUM | Applied | §1.2: source-generator-emitted `.cs` files are normal sources; reflection-dependent generators fail at semantic analysis. |
| FIX-B-MEDIUM-27 | MEDIUM | Applied | §5.11: typeof(SomeOpenGeneric<>) — distinct generic-type-definition FClass + typeof(T) inside generic method resolves at closed-instantiation. |
| FIX-B-MEDIUM-28 | MEDIUM | Applied | §5.5: XCSharpString operator== documented as pointer-equality on interned m_storage. |
| FIX-B-MEDIUM-29 | MEDIUM | Applied | §5.5: String.IsNullOrEmpty/IsNullOrWhiteSpace mapped to XCSharpString equivalents. |
| FIX-B-MEDIUM-30 | MEDIUM | Applied | §4.1 + §5.9: yield-return iterator allowed on sim-path with pool-allocated state machines. |
| FIX-B-MEDIUM-31 | MEDIUM | Applied | §5.19: clarified MVP is LINQ-less per Prime Directive; documented in deprecation/migration story. |
| FIX-B-MEDIUM-32 | MEDIUM | Applied | §5.9: custom IEnumerator emit — blocked by boxing issue (CRIT-02); resolution via explicit Int32Box-style wrappers. |
| FIX-B-MEDIUM-33 | MEDIUM | Applied | §5.2 + §8.1: Contract version hash computation — SHA-256 of canonical-form FBS schema; pinned in manifest. |
| FIX-B-MEDIUM-34 | MEDIUM | Applied | §5.13: Span<T> ref struct rules deferred to Roslyn CS8347. |
| FIX-B-MEDIUM-35 | MEDIUM | Applied | §5.13: stackalloc size limit XPACT_MAX_STACKALLOC_BYTES = 64KB. |
| FIX-B-MEDIUM-36 | MEDIUM | Applied | §5.3: `goto case` / `goto default` emit specified. |
| FIX-B-MINOR-01 | MINOR | Applied | §5.11: typeof(T).FullName format — XPact-canonical (`Outer::Inner<T>`). |
| FIX-B-MINOR-02 | MINOR | Applied | §5.2: full CLR operator-name set enumerated. |
| FIX-B-MINOR-03 | MINOR | Applied | §5.3: `out var x` declaration documented. |
| FIX-B-MINOR-04 | MINOR | Applied (with FIX-B-MEDIUM-06) | §5.6: `is var x` covered. |
| FIX-B-MINOR-05 | MINOR | Applied (with FIX-B-MEDIUM-06) | §5.3 + §5.6 + §5.7: `_` discard semantics. |
| FIX-B-MINOR-06 | MINOR | Applied | §5.9: local-function vs lambda — same display-class lowering for capturing case. |
| FIX-B-MINOR-07 | MINOR | Applied | §4.1: `params T[]` allocation documented; XIL2CPP083 warning on sim-path. |
| FIX-B-MINOR-08 | MINOR | Applied | §5.17 + §5.18: `fixed` pointer into XObject must keep XObject reachable. |
| FIX-B-MINOR-09 | MINOR | Applied | §5.13 + §5.7: Span<XPtr<XActor>> equally banned (XIL2CPP080). |
| FIX-B-MINOR-10 | MINOR | Applied | §12: diagnostic-code reservation bands documented. |
| FIX-B-MINOR-11 | MINOR | Applied | §5.8: depth-16 documented as MVP default; `[XGenericMaxDepth]` module-level override forward-committed post-MVP. |
| FIX-B-MINOR-12 | MINOR | Applied | §3.2: with-expression generalized to all struct types (not just records). |

### Audit C — Hot-reload and XBT (52 findings)

| ID | Severity | Status | Notes / Rev 2 location |
|---|---|---|---|
| FIX-C-CRIT-01 | CRITICAL | Applied (cross-audit reconciliation #1) | See cross-audit reconciliation. |
| FIX-C-CRIT-02 | CRITICAL | Applied (cross-audit reconciliation #7) | See cross-audit reconciliation. |
| FIX-C-CRIT-03 | CRITICAL | Applied | §3.5 + §9.5: Roslyn workspace per-call; `ResetAsync()` method; cancellation polled at back-edge granularity. |
| FIX-C-CRIT-04 | CRITICAL | Applied (cross-audit reconciliation #8) | See cross-audit reconciliation. |
| FIX-C-HIGH-01 | HIGH | Applied | §3.4 + §9.7: Roslyn version pinned via manifest `roslyn_version` field + static_assert. New XIL2CPP144. |
| FIX-C-HIGH-02 | HIGH | Deferred-Rev3 (Contract amendment) | §8.1: nullability mangling discriminator. Forward-commit Contract Rev 14. |
| FIX-C-HIGH-03 | HIGH | Applied | §8.2: generic constraint changes rejected by hot-reload layout-drift gate. New XIL2CPP034. |
| FIX-C-HIGH-04 | HIGH | Applied | §9.6: Phase 1 stub emits exit 61 (not 24) with custom message. |
| FIX-C-HIGH-05 | HIGH | Applied | §8.2: virtual-method table slot index protocol specified (source-declaration order, locked mid-session). New XIL2CPP145. |
| FIX-C-HIGH-06 | HIGH | Applied (with FIX-B-HIGH-14) | §5.1 + §5.2: multi-interface via FInterface dispatcher (Option A consistent with FakeVTable). |
| FIX-C-HIGH-07 | HIGH | Applied | §8.4 + §5.4: static-field-held containers register FClass cache invalidator. |
| FIX-C-HIGH-08 | HIGH | Applied | §8.4: closed-generic FClass instances are `.rodata`-resident; OnClassReplaced rewrites internal pointers via std::atomic_ref overlay. |
| FIX-C-HIGH-09 | HIGH | Applied (cross-audit reconciliation #7) | See cross-audit reconciliation. |
| FIX-C-HIGH-10 | HIGH | Applied | §3.4 + §9.4: cache key enumeration expanded; per-target settings fields enumerated. |
| FIX-C-HIGH-11 | HIGH | Applied | §3.3: Pass 2 merged-table schema specified with `pass1Tier` + `promotedBy` fields. |
| FIX-C-HIGH-12 | HIGH | Applied (with FIX-C-HIGH-04) | See FIX-C-HIGH-04. |
| FIX-C-HIGH-13 | HIGH | Applied | §9.7: manifest schema_version field with rejection rule. New XIL2CPP141 enriched. |
| FIX-C-HIGH-14 | HIGH | Applied | §9.1 + §9.3: XIL2CPPAction prerequisites enumerated (EmitReflectionAction(M_dep) for every M_dep). |
| FIX-C-HIGH-15 | HIGH | Applied | §10.2 + §3.3: C++-implemented NoThrow lookup via per-module reflection metadata; new XIL2CPP036. |
| FIX-C-HIGH-16 | HIGH | Applied | §9.5: XBT-itself hot-reload quiesce protocol. |
| FIX-C-HIGH-17 | HIGH | Applied | §6.1: per-platform stack-map records (Win64 + ARM64) emitted. |
| FIX-C-HIGH-18 | HIGH | Applied | §6 + §8.5: per-function `.unique_section` linker pragma for hot-patch granularity. |
| FIX-C-HIGH-19 | HIGH | Applied | §8.6 + §11.2: new gate X-IL2CPP-CASCADE-INCREMENTAL with per-function cache. |
| FIX-C-MED-01 | MEDIUM | Applied | §8.1: compile-time-only attributes don't participate in mangling. |
| FIX-C-MED-02 | MEDIUM | Applied | §8.1: explicit statement that method-body content doesn't affect mangling. |
| FIX-C-MED-03 | MEDIUM | Applied | §8.1: file-path doesn't participate in mangling. |
| FIX-C-MED-04 | MEDIUM | Applied | §9.7: full 24-tag + 21-sizeof list from Contract Rev 13.9 §14 enumerated. |
| FIX-C-MED-05 | MEDIUM | Applied | §6 + §8.4: static XObject reference unregistration on module unload. |
| FIX-C-MED-06 | MEDIUM | Applied | §8.4: `[XOnClassReplaced]` attribute auto-registration. New XIL2CPP033. |
| FIX-C-MED-07 | MEDIUM | Applied | §3.5 + §9.5: in-process signature tightened with ManifestRef, FlatBuffersBuffer types, ILogger, cancellation polling cadence. |
| FIX-C-MED-08 | MEDIUM | Applied | §3.4: Pass 1 / Pass 2 outputs in different directories (Pass1/ vs Pass2/). |
| FIX-C-MED-09 | MEDIUM | Applied | §3.3 + §8: Tier 2 → Tier 1 demotion across hot-reload rejected via `.tier_baseline` sidecar. New XIL2CPP035. |
| FIX-C-MED-10 | MEDIUM | Applied | §9.7: supported-set per ABI tag enumerated. |
| FIX-C-MED-11 | MEDIUM | Applied | §10: XIL2CPP Rev 1 authored against Stage B (Contract Rev 13.9); schema-version field of FProperty descriptor fixed at Rev 13.9 value. |
| FIX-C-MED-12 | MEDIUM | Applied (with FIX-A-MED-18) | See FIX-A-MED-18. |
| FIX-C-MED-13 | MEDIUM | Applied | §6.6: Conservative dedup cache layout specified — per-module, key = closed-type-full-name, file `ConservativeWarnings.json`. |
| FIX-C-MED-14 | MEDIUM | Applied | §3.5: in-process GC.Collect after Roslyn workspace dispose. |
| FIX-C-MED-15 | MEDIUM | Applied | §12: diagnostic-code reservation bands rationalized with reserved-for-expansion bands. |
| FIX-C-LOW-01 | LOW | Applied (with FIX-C-MED-15) | §12 numbering hygiene addressed. |
| FIX-C-MED-16 | MEDIUM | N/A (verification only) | No collisions confirmed. |
| FIX-C-HIGH-17 (per-platform calling conv) | HIGH | Applied (with FIX-A-CRIT-2 shadow-stack) | Shadow-stack mechanism inherently per-platform-neutral; offsets are shadow-stack-relative not register-relative. |
| FIX-C-MED-17 | MEDIUM | Applied | §6 + §10: XIL2CPP-emitted bitfield-using types use explicit uint32_t with manual bit ops; no C++ bitfields. |
| FIX-C-MED-18 | MEDIUM | Applied | §5.4: `[StructLayout(Pack=N)]` with N < natural alignment on ARM64 emits XIL2CPP093. |
| FIX-C-MED-19 | MEDIUM | Applied | §8.5: per-module `LiveCoding.manifest.json` emitted. |
| FIX-C-MIN-01 | MINOR | Applied | §3.5 cross-reference cleaned up. |
| FIX-C-MIN-02 | MINOR | Applied (with FIX-C-CRIT-01) | doc-header sizeof claim corrected. |
| FIX-C-MIN-03 | MINOR | Applied | §1.4 → §11 criteria references moved. |
| FIX-C-MIN-04 | MINOR | Applied | §8.3 tensed as forward-looking. |
| FIX-C-MIN-05 | MINOR | Applied | §1.5 FIX-A-MIN-40 cite includes XCoreXObject §3.6. |
| FIX-C-MIN-06 | MINOR | Applied (with FIX-C-CRIT-03) | §3.5 cancellation cadence tightened. |
| FIX-C-MIN-07 | MINOR | Applied | §5.1 path placeholder fixed. |
| FIX-C-MIN-08 | MINOR | Applied | §11.2 X-IL2CPP-SIZE references §13 corpus. |
| FIX-C-MIN-09 | MINOR | Applied | §10.4 meta-commentary reworded. |

---

## Forward commitments to other systems

The following findings require coordinated work in other docs/specs (carried forward beyond Rev 2 ingestion):

1. **XCoreXObject Rev 5** must add:
   - `XPACT_SAFEPOINT_CHECK` macro with acquire-order semantics + `XPACT_SAFEPOINT_FLAG_ABI_TAG` (FIX-A-CRIT-6).
   - `XPACT_GC_STORE_MEMORY_ORDER_TAG` ABI tag for ARM64 acquire/release discipline (FIX-A-HIGH-10).
   - `XObject.NewRoot<T>(name, flags)` factory variant (FIX-A-LOW-8).
   - `IXObjectClassReplacedListener` interface + `::XCore::CoreDelegates::GetOnClassReplaced().Subscribe` accessor (FIX-C-CRIT-02 / FIX-A-MED-33).
   - Container parent-XObject inheritance API for TArray/TMap/TSet (FIX-A-MED-6, FIX-A-MED-17).
   - Lazy-init for XStackMapTable (FIX-A-MED-30).

2. **XHT Rev 7+** must reflect:
   - Schema vector emit for C#-declared XClasses (Q14 resolution; §10.4).
   - NoThrow annotation discovery for C++-declared functions (FIX-C-HIGH-15).
   - Canonical auto-property backing field naming `__BackingField_<X>` (FIX-B-HIGH-09).

3. **XBT Rev 11** must amend:
   - ReferenceCompileCSharpAction ordinal allocation (FIX-C-CRIT-04).
   - Manifest `roslyn_version` field + `dotnet_sdk_version` field (FIX-C-HIGH-01, FIX-C-HIGH-04).
   - Manifest schema version field with rejection rule (FIX-C-HIGH-13).
   - `conditional_symbols` per-module field (FIX-B-MEDIUM-25).

4. **Toolchain Contract Rev 14** must add:
   - Param mangling discriminator for `ref readonly` (FIX-B-HIGH-03, `K`).
   - Param mangling discriminator for nullability (`Q`) — FIX-C-HIGH-02.
   - Linker-symbol separator review (`__` reservation risk) — FIX-B-HIGH-25.

These are flagged as Round 2 audit input.

## Cross-system contradictions surfaced during Rev 2 (Round 2 audit input)

1. **XCoreXObject §1.3** says lifecycle table has 8 slots; XIL2CPP-emitted lifecycle bodies enumerate 7 explicit slots. Verify XCoreXObject Rev 5 maintains 8-slot count.

2. **Cross-tool ABI tag count**: Contract Rev 13.9 §14.1 has 24 layout tags + 21 sizeof pins (per the surface table). XIL2CPP §9.7 must emit all of them — not the partial list in Rev 1. Rev 2 has the full list.

3. **NoThrow annotation discoverability**: XHT must emit NoThrow for BOTH C# and C++ functions. XHT Rev 6 docs only address C# side. Forward-commit to XHT Rev 7.

4. **Boxing wrapper types**: The `Int32Box` / `FloatBox` wrappers introduced for non-sim-path boxing are not yet in XPact.CSharp.BCL. Forward-commit XPact.CSharp.BCL Rev 2 to add them.

5. **Manifest schema_version vs contract_version**: Currently the FBS manifest has `contract_version: string` but no `schema_version: uint32`. Forward-commit XBT Rev 11 to add the latter.

## Word-count delta

Rev 1 word count (approximate): 22k.
Rev 2 word count (approximate): TBD post-edit (target 30-35k due to incremental specifications added).

---

*End of XIL2CPP-Rev2-Changelog.md.*
