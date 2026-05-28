# XIL2CPP Rev 2 — Round 2 Audit D: Regression Check + Structural Soundness

> **Audit perspective.** Round 2 audit D — verify Rev 1 → Rev 2 fix application, catch new defects introduced by the 22k → 37.9k word edit, audit fix-quality + cascade landed completely.
> **Source.** `Documents/XIL2CPP.html` (commit `a6483f2`; status "Rev 2; AWAITING ROUND 2 AUDIT").
> **Compared against.**
> - Rev 1 (commit `a7b7a97`) for before-state of applied fixes.
> - `Documents/XIL2CPP-Rev2-Changelog.md` for disposition table.
> - `Documents/XToolchainContract.html` Rev 13.9 for ABI tag content.
> - `Documents/XCoreXObject.html` Rev 4 for lifecycle table + safe-point ABI.
> - `Documents/XBT.html` Rev 10 for `XActionType` enum + Phase 2 stub exit code.
> **Reading discipline.** Spot checks of Rev 1 only where cascade verification required.

## Summary of findings

| Severity | Count |
|---|---|
| CRITICAL | 6 |
| HIGH | 12 |
| MEDIUM | 13 |
| LOW | 6 |
| MINOR / NIT | 4 |
| **Total** | **41** |

## Top 3 most impactful findings

1. **FIX-D-CRIT-01.** Rev 2 §9.8 forward-commits `ReferenceCompileCSharpAction` to **XBT slot 17**, but XBT.html Rev 10 §5.1 explicitly states "slot 17 (formerly `RunPostBuildAction`) is RETIRED, not reused. Slot numbers are stable; reusing 17 would alias old ActionHistory entries to a different action type." Rev 2's chosen slot is the one slot the XBT contract specifically forbids reusing. The correct path is slot 14 or 15 (`Reserved_Phase2_F` / `Reserved_Phase2_G`), or a new slot 18+.
2. **FIX-D-CRIT-02.** §15 Phase 6.g still says "explicit register spilling at every safe-point" — the **exact mechanism Rev 2 §6.1 was rewritten to reject**. Three of the sub-phases (6.a / 6.g / 6.k) carry Rev 1 wording that contradicts the Rev 2 design body. The implementation plan in §15 reverts the entire Rev 2 program.
3. **FIX-D-CRIT-03.** Boxing wrappers `Int32Box` / `FloatBox` are referenced as XObject-derived classes in §5.6 (pattern-match cast: `::XCore::Reflect::Cast<::Int32Box>(o)`) — but §5.3 / §12 forbids constructing them via `new Foo()` (XIL2CPP001). The Rev 2 boxing-ban resolution says "use explicit `Int32Box`-style wrappers" but never specifies HOW they get constructed when XObject.New<Int32Box>(outer, name, flags) requires an Outer that boxing-time-throwaway wrapper doesn't have.

## Rev 2 convergence assessment

**Rev 2 represents partial structural convergence with substantial residual issues.** The largest body-of-text edits (§5.9 lambdas, §6.1 shadow-stack, §8.4 listener) are structurally sound — Rev 2 chose the right architectural answers. But Rev 2 has THREE classes of residual problems:

1. **Cascade failure to §15 (implementation order):** Phase 6.a still references ordinal 14 (post-FIX-C-CRIT-04 should be slot 17 per Rev 2's own claim, though that slot has its own problem per FIX-D-CRIT-01); Phase 6.g still references register spilling (post-FIX-A-CRIT-2 should be shadow-stack); Phase 6.k still references `Z_Construct_FClass_*`-site listener emit (post-FIX-C-CRIT-02 should be per-module aggregator). §15 was not updated.
2. **Cascade failure to forward-commitment slot count:** `FXObjectLifecycleTable` IS 8 slots per XCoreXObject (verified). §14.0.1's claim "Rev 2 enumerates 7" is internally inconsistent — Rev 2 §5.1 enumerates all 8 (PostInit, BeginDestroy, IsReadyForFinishDestroy, FinishDestroy, Serialize, AddReferencedObjects, PostLoad, PreSave). The Rev 2 changelog flagged a phantom contradiction.
3. **Structural new gaps** introduced by the Rev 2 edit body: pattern-match boxing wrapper construction (FIX-D-CRIT-03), TArray `m_parent` field collides with XObject* layout (FIX-D-HIGH-02), `XScopedGuard` lambdas escape the noexcept commitment (FIX-D-HIGH-05), `using` block dispose-time exception in catch is logged via XLog inside a `noexcept` lambda (FIX-D-MED-03).

**Recommendation:** Rev 3 is necessary. The §15 cascade alone requires editing 4 sub-phase blocks; combined with the new defects identified here, a Rev 3 pass is structurally cleaner than ad-hoc patching. **Rev 4 will likely also be required** because 8 forward-commitments to XCoreXObject Rev 5 / XHT Rev 7 / XBT Rev 11 / Contract Rev 14 / BCL Rev 2 / XPact.Sim land outside XIL2CPP's own boundary and may surface defects only at the receiving spec audit.

---

## Findings

### CRITICAL

#### FIX-D-CRIT-01 — XBT slot 17 is RETIRED, not free; ReferenceCompileCSharpAction forward-commit picks the forbidden slot

**§9.8** (line 3266):
> "The Rev 1 ordinal-14 claim conflicted with XBT Rev 10's `Reserved_Phase2_F` slot. Per Prime Directive, Rev 2 forward-commits XBT Rev 11 amendment to expand the action ordinal pool: **slot 17** is the next-available after BuildPluginManifestAction at slot 16; slots 14/15 stay reserved."

**Problem.** Per XBT.html Rev 10 §5.1 line 788-790:
> "NOTE Rev 3: slot 17 (formerly `RunPostBuildAction`) is RETIRED, not reused. Slot numbers are stable; reusing 17 would alias old `ActionHistory` entries to a different action type."

Slot 17 specifically cannot be reused — the auto-derived `CommandVersion` hash relies on append-only enum semantics with retired-slot stability. Rev 2's claim "slot 17 is next-available" is the exact path XBT Rev 10 documented as broken. A patch DLL whose source declares `XActionType = 17` would be aliased by old `ActionHistory.bin` records to "the old RunPostBuildAction" and break incremental rebuild deterministically.

**Also.** The Rev 1 ordinal-14 critique in Rev 2 is correct (slot 14 is `Reserved_Phase2_F`) — so the FIX-C-CRIT-04 problem is real. But picking slot 17 doesn't solve it.

**FIX.** Forward-commit `ReferenceCompileCSharpAction` to either:
- (a) **Slot 14 (`Reserved_Phase2_F`)** with XBT Rev 11 amendment renaming the slot's reserved-handle to the action name. The reservation specifically exists for new Phase 2 action allocation; using one of the reserved slots is the documented mechanism.
- (b) **A NEW slot 18** (or 18, 19, ... depending on hash-rotation tolerance). Adding a new highest-numbered slot is append-only-safe but rotates `CommandVersion` for all targets on rebuild (expensive but correct).

Option (a) is cleaner; the reserved slots exist precisely for this purpose.

**Severity.** CRITICAL — Locked Commitment violation: silent contract breakage with XBT.

---

#### FIX-D-CRIT-02 — §15 Phase 6.g reverts the §6.1 shadow-stack rewrite

**§15 Phase 6.g** (line 3638):
> "**Phase 6.g — Stack-map emit + safe-point check + GC root span emit**: ship the per-function FStackMapRecord emission with **explicit register spilling at every safe-point**; ship the XPACT_SAFEPOINT_CHECK..."

**Problem.** This is the **Rev 1 mechanism** that §6.1 explicitly rewrote and rejected:

§6.1 (line 2502):
> "The Rev 1 design proposed 'emit explicit register spilling at every safe-point' which is architecturally infeasible portably under the locked toolchain matrix (MSVC + Clang + GCC) — MSVC dropped x64 inline asm in 2008, C++ has no portable surface for forcing specific stack offsets... The audit rejected this approach."
> "Rev 2 mechanism: SHADOW STACK."

§15 Phase 6.g was NOT updated to reflect the Rev 2 rewrite. The implementation plan as written would direct a Phase 6 engineer to build the rejected mechanism. The cascade from FIX-A-CRIT-2 missed §15.

**FIX.** Rewrite Phase 6.g:
> "Phase 6.g — Stack-map emit + safe-point check + GC root span emit: ship the per-function FStackMapRecord emission with the **shadow-stack mechanism** (`XPtr<XObject>* _liveRefs[N]` stack array per §6.1, Rev 2); ship the XPACT_SAFEPOINT_CHECK at function entry + back-edge; ship the long-loop detection (Pass 3 augmentation); ship the XStackMapTable registration at module load (lazy-init per FIX-A-MED-30). Per-platform shadow-stack is platform-neutral by design (FIX-C-HIGH-17). Unit tests verify 100% function coverage via _liveRefs index tracking + verify the collector's binary search finds the right record + verify long-loop detection threshold. Estimated: 3-4 weeks. **X-IL2CPP-STACKMAP-COV + X-IL2CPP-SHADOWSTACK-COV + X10 acceptance gates.**"

**Severity.** CRITICAL — implementation plan contradicts the design body.

---

#### FIX-D-CRIT-03 — Boxing-wrapper construction path undefined; XIL2CPP001 collision

**§5.6** (line 1655-1656):
> ```
> if (auto* p = ::XCore::Reflect::Cast<::Int32Box>(o)) {
>     int32_t n = p->Value;
> ```

**§5.6** (line 1670):
> "Post-MVP non-sim-path adds the explicit wrapper-allocation path with documented XGCRootSpan registration; XIL2CPP-emitted boxing wrappers (`Int32Box`, `FloatBox`, etc.) are reference-typed XObject-derived classes that register their boxed value as a stack/heap root."

**Problem.** Three contradictions:

1. **`Int32Box` is XObject-derived → XIL2CPP001 fires.** Any author writing `var box = new Int32Box(5);` triggers XIL2CPP001 because Int32Box is XObject-derived per the cited prose. The boxing operation MUST go through `XObject.New<Int32Box>(outer, name, flags)` per §2.3 / Locked Commitment 3. **What is the Outer for an inline boxing expression?** None of the call sites can produce a stable Outer (boxing happens inline; the boxed value's lifetime is determined by GC reachability, not by an explicit Outer field).

2. **The Cast<Int32Box>(o) pattern operates on `object` typed o** — but §4.1 (line 787) bans implicit boxing via XIL2CPP062 in MVP. Where does an `object` typed `o` even come from? The implicit `int → object` conversion would have already emitted XIL2CPP062 at the call site. The only legal path is the AUTHOR explicitly constructs the wrapper via `XObject.New<Int32Box>(outer: ?, ...)` — but the Outer question recurs.

3. **§5.7 table (line 1714) still allows `List<object>` mapping to `TArray<void*>` with Conservative XGCRootSpan** — but §4.1 (line 788) bans `List<object>` with value-type entries via XIL2CPP073. The Rev 2 cascade left §5.7 still showing the old emit pattern as supported, and never specifies what happens when the author tries to use the legal `List<object>` with Int32Box entries (the boxing wrapper case the §5.6 prose suggests).

**FIX.** Boxing wrapper types `Int32Box` / `FloatBox` should be either:
- (a) **Plain non-XObject reference types** with `std::shared_ptr<T>` semantics per §5.1's Rev 2 default — boxing IS reference-identity-semantic but doesn't need GC integration if the boxed value doesn't transitively contain XObjects. Spec must clarify: "Int32Box is `[XValueClass]`-attributed AND derives from a marker `XBoxedPrimitive` non-XObject base class; it is NOT XObject-derived."
- (b) If they MUST be XObject-derived for the lifetime story, spec must add a new factory variant `XObject.NewTransient<Int32Box>(value)` that auto-routes through a per-thread transient root and DOES NOT require an explicit Outer. Add forward-commit to XCoreXObject Rev 5.

Either way, document the constructor path in §5.6 + §5.21 + BCL-Rev2-BOXING forward-commit.

**Severity.** CRITICAL — the boxing-ban resolution (FIX-B-CRIT-02) is a structurally incomplete fix: it bans implicit boxing but the alternative pathway has no construction story.

---

#### FIX-D-CRIT-04 — TArray `m_parent` field collides with XObject base class layout

**§5.7** (line 1736):
> ```cpp
> class TArray<XPtr<XActor>> {
>     XPtr<XActor>* m_data = nullptr;
>     int32_t       m_count = 0;
>     int32_t       m_capacity = 0;
>     XObject*      m_parent = nullptr;  // [Rev 2: applied FIX-A-MED-6]
>     XGCRootSpan   m_rootSpan;
> ```

**Problem.** Adding `m_parent` to TArray:
1. **Changes the container's byte layout.** Existing XCore-4a `TArray<T>` does NOT have `m_parent`. Per Contract Rev 13.9 + the `container = true` flag (§8.3), container layouts are layout-frozen mid-hot-reload session. Adding a new field is a layout change.
2. **Cascade to FIX-A-MED-17** says "TArray Add barrier rewrite — depends on container-parent inheritance, forward-commit XCoreXObject Rev 5." But the forward-commit is to ADD the field as a XCoreXObject Rev 5 addition — so XIL2CPP cannot ship the field independently. The current Rev 2 emit shows the field present, conflicting with the forward-commit framing.
3. **Memory cost.** Every TArray instance grows by 8 bytes (or 16 with alignment). On a typical XActor with 5-10 TArrays, this is 40-80 extra bytes per actor — non-trivial.
4. **Constructor signature breakage.** Existing C# code that writes `new List<XActor>();` (now banned via §5.3/XIL2CPP001 for XObject? No — List is not XObject-derived; the diagnostic is for XObject-derived types only) still compiles. The Rev 2 emit shows `TArray<XPtr<XActor>>(this)` — but where does the C++ compiler get the `this` parameter from? Either default-construction is allowed (parent = nullptr, which breaks the barrier), or the C# emit needs to forward `this` everywhere a `List<XActor>` is constructed.

**FIX.** Either:
- (a) Document the `m_parent` field as part of XCoreXObject Rev 5 contract (Contract Rev 14 cascade) and show the Rev 2 TArray emit as a target representation, NOT current XCore-4a; or
- (b) Use the existing implicit `this` linkage: every TArray declared as a field of an XObject knows its owner via field-pointer arithmetic at `Add` time (the call site knows the parent because the call site `actor.list.Add(x)` lets `actor` be derived from `this - offsetof(TArray, m_owner_field)`). This is fragile but doesn't bloat memory.

Specify the C# emit for `new List<XActor>()` constructor: is the parent argument synthesized from the enclosing object, the call-site, or the field-pointer's owner? §5.7 currently shows it but doesn't specify the emit rule.

**Severity.** CRITICAL — container layout change without coordination breaks hot-reload + container ABI.

---

#### FIX-D-CRIT-05 — §15 Phase 6.k OnClassReplaced listener emit location reverts §8.4 rewrite

**§15 Phase 6.k** (line 3642):
> "ship the OnClassReplaced delegate listener auto-emission **at `Z_Construct_FClass_*`**; ship the per-FClass sub-pool rebind cooperation..."

**Problem.** Rev 2 §8.4 (line 2997):
> "Per-module static listener — emitted by XIL2CPP at the per-module **`.init.cs.cpp` aggregator**, NOT per-Z_Construct_FClass site."

Phase 6.k still references the Rev 1 per-Z_Construct site emission. The cascade from FIX-C-CRIT-02 missed §15.

This is not merely cosmetic: per-Z_Construct vs per-module-aggregator changes (a) how many listener instances register (one per FClass vs one per module), (b) the deregistration sequencing in module-unload, (c) the ModuleInitGuard pattern's correctness. Engineers implementing Phase 6.k would build the wrong pattern.

**FIX.** Rewrite Phase 6.k:
> "Phase 6.k — Hot-reload integration (symbol mangling stability + cross-module rebind): ship the **typed `IXObjectClassReplacedListener` per-module static listener emission at the `.init.cs.cpp` aggregator** (per §8.4 Rev 2 rewrite per FIX-C-CRIT-02 / FIX-C-HIGH-09); ship the explicit `Subscribe`/`Unsubscribe` lifecycle around `ModuleInitGuard`; ship the per-FClass sub-pool rebind cooperation; ship the per-closed-instantiation generic FClass cache invalidation hooks (FIX-C-HIGH-08); verify symbol mangling stability across rebuild via SHA-256 comparison; verify container layout-freeze enforcement. Integration tests with XLiveCoding (synthetic patch DLL) verify Tier 1 shim RVA rewrite + Tier 2 module rebuild path + module-unload listener-leak prevention. Estimated: 2-3 weeks. **X-IL2CPP-MANGLE-DET + criterion (d) + criterion (e) acceptance gates.**"

**Severity.** CRITICAL — implementation plan contradicts the design body.

---

#### FIX-D-CRIT-06 — Phase 1 stub exit code vs XBT `run-xil2cpp` mode collision

**§9.6** (line 3150):
> "Phase 1 stub returns **exit code 61** with a custom message `"XIL2CPP Phase 1 stub: full implementation not yet shipped"`. XBT-side aggregation: 61 with the Phase-1-stub marker is treated as a 'deferred build'..."

**Problem.** XBT.html Rev 10 §1.1 (line 258):
> "`run-xil2cpp` ... Phase 2 wrapper for the XPact IL2CPP transpiler. Realised as a stub `IToolMode<T>` (`RunXIL2CPPMode.cs`) returning **exit code 24 (`PluginNotFound` mnemonic)** with a Phase 2 schedule diagnostic, symmetric to `run-xht`."

There are now TWO Phase 1 stubs for XIL2CPP:
1. **XBT's `run-xil2cpp` mode stub** in `RunXIL2CPPMode.cs` returning **24**.
2. **XIL2CPP's own Phase 1 stub binary** per FIX-C-HIGH-04 returning **61**.

The Rev 2 reconciliation correctly identified that exit 24 has the wrong semantic (collides with `PluginNotFound`), but didn't reconcile across XBT.html. The result: XBT spawns its own stub (returns 24), then XBT's stub-mode is presumably DIFFERENT from the XBT-spawns-XIL2CPP-binary-stub path. Which one is canonical? When does each fire?

**FIX.** Either:
- (a) Update XBT.html Rev 10 §1.1 to align `RunXIL2CPPMode.cs` to return 61 (with the `Phase1StubReturn` marker XIL2CPP §9.6 references) — propose Contract Rev 14 to allocate `Phase1StubReturn` as a dedicated exit code rather than overloading 61.
- (b) Or specify that the XBT `run-xil2cpp` mode is a wrapper that delegates to the XIL2CPP binary stub, and document the propagation: XIL2CPP binary returns 61 → XBT mode wraps and returns 24 to its caller (preserving symmetry with `run-xht`).

Document the resolution in §9.6 + flag as a forward-commit to XBT Rev 11 alongside the existing manifest field additions.

**Severity.** CRITICAL — exit-code semantics divergence between XBT and XIL2CPP-binary stubs.

---

### HIGH

#### FIX-D-HIGH-01 — §15 Phase 6.a still references ordinal-14 (the rejected Rev 1 ordinal)

**§15 Phase 6.a** (line 3632):
> "ship the ReferenceCompileCSharpAction (**XBT extension ordinal 14**)."

**Problem.** Rev 2 §9.8 specifically rejected ordinal 14 (cited it as the conflicting Rev 1 claim) and moved to slot 17. Phase 6.a wasn't updated. (Note: per FIX-D-CRIT-01, slot 17 itself is wrong, but Phase 6.a was supposed to track Rev 2 §9.8's reasoning.)

**FIX.** Update Phase 6.a to track the final slot decision once FIX-D-CRIT-01 is resolved. Cite "XBT slot per FIX-C-CRIT-04 / FIX-D-CRIT-01 resolution; see §9.8."

**Severity.** HIGH — cascade incompleteness; engineers reading §15 would emit the conflicting Reserved_Phase2_F slot.

---

#### FIX-D-HIGH-02 — Shadow-stack underflows entry safe-point ordering when `self` isn't an XPtr

**§6.1** (line 2553-2557):
> ```cpp
> ::XPtr<XObject> _liveRefs[2] = {};
> _liveRefs[0] = ::XPtr<XObject>(reinterpret_cast<XObject*>(self));
> XPACT_SAFEPOINT_CHECK();
> ```

**Problem.** Two issues:

1. **Order-of-emission contradicts §6.1's own discipline.** §6.1 line 2555 says: "Function-entry safe-point fires AFTER any container/RAII-managed-root constructors complete and BEFORE the function body's first user statement: *(1) shadow-stack init → (2) container constructors (XGCRootSpan registers) → (3) safe-point check → (4) user body*." But the example writes `_liveRefs[0] = ...` (step 1), then immediately calls `XPACT_SAFEPOINT_CHECK()` — but step 2 (container constructors) is missing or implicit. If the function has any TArray/lambda/state-machine in its body, the constructor must fire BEFORE the safe-point check.

2. **`self` re-cast loses XPtr semantics.** Building `XPtr<XObject>(reinterpret_cast<XObject*>(self))` from a raw `self` pointer constructs a fresh XPtr without notifying the GC's reachable-set. If the function were re-entrant, two XPtrs over the same XObject would exist (the caller's stack-rooted XPtr + this function's `_liveRefs[0]` XPtr) — both are visible to the collector, but the second XPtr's lifecycle (constructed on this frame, destructed on this frame return) is what the shadow-stack discipline depends on. The cast is correct semantically, but the syntactic form is fragile — a future XPtr that asserts "I'm the canonical reference" would break.

**FIX.** Rewrite §6.1's example to show: (a) shadow-stack array decl; (b) `self` slot write; (c) ALL container constructors (if any) inline; (d) safe-point check; (e) user body. Add a code comment clarifying that the `_liveRefs[i]` XPtr is a SCAN-ONLY view into the live ref, not a separately-owned reference.

**Severity.** HIGH — discipline statement contradicts example; engineers may compute wrong order.

---

#### FIX-D-HIGH-03 — §5.9 lambda example bypasses display-class for trivial captures

**§5.9** (line 1881):
> ```cpp
> auto addN = [&_dc0](int32_t x) noexcept -> int32_t { return x + _dc0.n; };
> ```

**Problem.** This is C++ lambda syntax with explicit `[&_dc0]` capture — but the surrounding prose says Rev 2 follows Roslyn's display-class pattern where the lambda BECOMES a member function on the display class. The example shows a C++-lambda-with-capture pattern, not a Roslyn-style class-with-method pattern. This contradicts the prose claim "the lambda becomes a member function on the display class."

The two patterns differ materially:
- **C++ lambda with capture** stores the captured `&_dc0` pointer in the lambda closure object's hidden state. Each lambda instantiation is a distinct closure type.
- **Roslyn display class with method** stores the captured local on the heap (the display-class instance) and the lambda is a member function — multiple lambdas over the same scope SHARE the display-class instance.

The §5.9 example shows the former; the prose claims the latter. Inconsistency.

**FIX.** Pick one:
- (a) If §5.9 means Roslyn-style display class: rewrite the example as
  ```cpp
  struct __DisplayClass_OuterMethod_Scope0 {
      int32_t n;
      int32_t __Method_addN(int32_t x) noexcept { return x + n; }
  };
  __DisplayClass_OuterMethod_Scope0 _dc0;
  _dc0.n = 5;
  auto addN = std::bind(&__DisplayClass_OuterMethod_Scope0::__Method_addN, &_dc0);
  ```
- (b) If §5.9 means C++ lambda with explicit display-class pointer in the capture clause: rewrite the prose to NOT claim "becomes a member function on the display class" and instead say "captures the display-class pointer in the closure object."

(a) matches Roslyn semantics more cleanly but adds `std::bind` overhead. (b) is C++ idiomatic and avoids the overhead but requires more C# semantics work to get right.

**Severity.** HIGH — major emit pattern is ambiguous between two different lowerings.

---

#### FIX-D-HIGH-04 — Async state machine example shows captured `this->owner` re-entrant call

**§5.9** (line 2017-2021):
> ```cpp
> ::XCore::Async::ReadFileAsync(path).Then([this](auto result) {
>     this->data = std::move(result);
>     this->state = State::AfterReadFile;
>     this->MoveNext( /* this captured XObject* */ this->owner.Raw() );
> });
> ```

**Problem.** The continuation lambda captures `[this]` where `this` is the async state machine struct (NOT an XObject). The lambda invokes `this->MoveNext(this->owner.Raw())`. Three issues:

1. **The lambda captures `[this]` of the state machine.** If the state machine is destroyed mid-await (the promise is cancelled, the awaiter unwinds), the captured `this` pointer is dangling. Standard async-runtime patterns either own the state machine via `shared_ptr` or use a stable id mechanism.
2. **The captured lambda has no XGCRootSpan.** It captures `this` of a state machine that owns an XPtr<XActor> — but the lambda itself doesn't register a span. If the GC runs during `ReadFileAsync.Then`'s wait, the `owner` XObject may be collected before the continuation fires.
3. **`this->MoveNext(this->owner.Raw())` calls MoveNext with `self = owner.Raw()`** — but MoveNext is the state machine's own method, and `self` is the original async function's `self` (the XObject that the original async call was on). These should be the SAME, but the comment "this captured XObject*" suggests they're meant to be different. Confusing.

**FIX.** Rewrite §5.9 async example to show:
- (a) Heap-allocate state machine via `FMemory::NewObject<_AsyncSM>(memTag, ...)` per FIX-A-MED-5.
- (b) Pass shared ownership of state machine into continuation (NOT `[this]` captured — use `XSharedPtr<_AsyncSM>` capture by value).
- (c) Show the per-state safe-point check at MoveNext entry.
- (d) Show how `_rootSpan` covers `owner` across the await — and explain why the continuation's captured lambda DOESN'T need to register a separate span (because the state machine itself is a heap-rooted owner).

**Severity.** HIGH — async state machine emit pattern has a GC reachability bug.

---

#### FIX-D-HIGH-05 — `XScopedGuard` lambdas are forced `noexcept` but contain throwing code (Dispose, log)

**§5.15** (line 2323-2327):
> ```cpp
> XScopedGuard _disposeGuard([&]() noexcept {
>     try { x.Dispose(); } catch (...) {
>         // Dispose-time exception swallowed to preserve the original exception
>         // per the C# language convention; logged via XLog at Verbose for diagnosis.
>     }
> });
> ```

**Problem.** The `XScopedGuard` lambda is marked `noexcept` — so the C++ standard requires it to not throw. The lambda has a `try { ... } catch (...) { ... }` block which IS noexcept-correct because all exceptions are caught. BUT the catch block says "logged via XLog at Verbose for diagnosis" — if XLog::Verbose itself throws (e.g., out-of-memory on log buffer allocation), the catch block's body propagates a new exception out of the lambda, violating noexcept and calling `std::terminate`.

**Also.** §5.12 (line 2193) shows:
```cpp
XScopedGuard _guard([&]() noexcept { Cleanup(); });
```
Cleanup() is presumably user code — the user has no obligation to make Cleanup() noexcept. Same noexcept violation if Cleanup throws.

**Also.** §5.12 (line 2209) shows the same `[&]() noexcept { Cleanup(); }` pattern for try-catch-finally lowering.

**FIX.** Three options:
- (a) Make `XScopedGuard` accept a `Func` that's NOT noexcept; document the contract that if the cleanup throws, the guard wraps and calls a fallback `XCore::HAL::Abort` (matching the FIX-B-MEDIUM-23 swallowing semantic).
- (b) Add a Pass 3 check: if the cleanup body could throw, generate a wrapper that catches everything internally and logs/aborts as appropriate. This requires Pass 4 tier classification of the cleanup body.
- (c) Use a different `XScopedGuardWithThrow` variant for cleanups that may throw, and `XScopedGuardNoThrow` for proven-non-throwing cleanups. The choice is per-call-site.

**Severity.** HIGH — `std::terminate` on dispose-time exception is a behavioral regression vs C#.

---

#### FIX-D-HIGH-06 — Display-class XGCRootSpan double-registers on move

**§5.9** (line 1902-1928):
```cpp
~__DisplayClass_MyActor_MakeUpdater_Scope0() {
    XGC_UnregisterRootSpan(&_rootSpan);
}
// Move constructor [Rev 2: applied FIX-A-MED-4] — atomic-with-mark-phase
__DisplayClass_...(__DisplayClass_...&& other) noexcept
  : _self(std::move(other._self))
{
    XGC_UnregisterRootSpan(&other._rootSpan);
    _rootSpan = XGCRootSpan{ ... };
    XGC_RegisterRootSpan(&_rootSpan);
}
```

**Problem.** The move-constructor flow:
1. `other._rootSpan` is unregistered.
2. `this._rootSpan` is constructed in-place (the `=` operator overwrites the existing span — but `this` is a fresh object, so the existing span is the default-zero-initialized one which is NOT registered).
3. `this._rootSpan` is registered.

But the destructor unconditionally calls `XGC_UnregisterRootSpan(&_rootSpan)` — for the move-source `other`, after the move, `other._rootSpan` has been unregistered (line "XGC_UnregisterRootSpan(&other._rootSpan)") but `other` is still destructible (it's the source of the move; its destructor will eventually fire). When `other`'s destructor fires, it calls `XGC_UnregisterRootSpan(&other._rootSpan)` AGAIN — double-unregister.

The standard pattern is: after move, the source's `_rootSpan.base = nullptr` is set so the destructor's unregister becomes a no-op. The current emit doesn't do this.

**FIX.** Add to move ctor: `other._rootSpan.base = nullptr;` (or equivalent invalidation that the destructor recognizes as "already unregistered, skip"). Document the move-source's destructor's idempotence requirement.

**Severity.** HIGH — double-unregister could corrupt XGCRootSpan registry state.

---

#### FIX-D-HIGH-07 — Async state machine `_rootSpan` covers ONLY `owner`, missing `data` reference

**§5.9** (line 1995):
> ```cpp
> ::XCore::Container::TArray<uint8_t> data;
> ::XCore::Reflect::XGCRootSpan        _rootSpan;  // covers owner across awaits
> ```

**Problem.** The state machine has TWO fields that span the await boundary:
- `owner: XPtr<XActor>` (line 1993)
- `data: TArray<uint8_t>` (line 1994)

The `_rootSpan` initialization (line 1999-2005) covers ONLY `owner`. If `data` were `TArray<XPtr<XActor>>` (any container with XObject references), the span would miss it. The current example uses `TArray<uint8_t>` so the omission is hidden, but the pattern fails for any state machine with reference-bearing locals beyond a single field.

The comment "covers owner across awaits" is incomplete — it should cover ALL XObject* locals that span awaits.

**FIX.** Either:
- (a) Show a state machine with multiple XPtr<T> locals; emit ONE root span that covers ALL of them via the start-of-locals address + total-XPtr-count.
- (b) Show multiple root spans, one per XPtr<T> local, each registered/unregistered in ctor/dtor.

Document in §5.9 prose that EVERY XObject reference that spans an await must be covered.

**Severity.** HIGH — async state machine emit is an example pattern that mis-leads engineers about coverage discipline.

---

#### FIX-D-HIGH-08 — Listener `g_listenerHandle` stored as namespace-scope but referenced by `static` guard

**§8.4** (line 3017-3032):
```cpp
namespace {
    XHealthPickup_OnClassReplacedListener g_listener;
    ::XCore::Reflect::FHandle g_listenerHandle;
    struct ModuleInitGuard {
        ModuleInitGuard() noexcept {
            g_listenerHandle = ...Subscribe(&g_listener);
        }
        ~ModuleInitGuard() noexcept {
            ...Unsubscribe(g_listenerHandle);
        }
    };
    static ModuleInitGuard g_moduleInitGuard;
}
```

**Problem.** Two issues:

1. **Static-init ordering.** `g_listener` and `g_listenerHandle` are namespace-scope locals; `g_moduleInitGuard` is a `static` (file-scope) local. Within an anonymous namespace, both have static storage duration but their init order is implementation-defined unless explicitly ordered. The guard's constructor depends on `g_listener` already being constructed; if `g_moduleInitGuard` initializes first, `&g_listener` points to uninitialized memory.

2. **Cross-translation-unit init ordering hazard.** The same pattern emitted in 100+ `.cs.cpp` files would each have a `ModuleInitGuard`. The order across these guards is the "static initialization order fiasco." If one guard subscribes and another guard runs during the subscribe and triggers an OnClassReplaced callback, the callback could fire on partially-initialized state.

**FIX.** Move all three namespace-scope statics into a single struct that controls construction order via member-init-list:
```cpp
namespace {
    struct ModuleInit {
        XHealthPickup_OnClassReplacedListener listener;
        ::XCore::Reflect::FHandle handle;
        ModuleInit() noexcept
            : listener{}, handle{ ... Subscribe(&listener) }
        {}
        ~ModuleInit() noexcept { ... Unsubscribe(handle); }
    };
    static ModuleInit g_moduleInit;
}
```
This makes the init order explicit (member-init-list enforces it).

**Severity.** HIGH — static init order fiasco; UB on early load.

---

#### FIX-D-HIGH-09 — §6.3 reference-store enumeration omits Span<T>'s slot writes

**§6.3** (line 2641-2655). The enumeration includes:
> "AssignmentExpressionSyntax with reference LHS, object-initializer setters, with-expression setters, compound-assignment lowering, deconstruction-assignment, indexer-setter call, auto-property setter, init-accessor, default-interface-property setter, primary-constructor field init, container `Add`/`Set` methods."

**Problem.** Missing:
1. **Span<T> indexer setter** (`span[i] = value`) — Span<T> per §5.13 is `{T*, size_t}` value type with `operator[]`. If T is `XPtr<XActor>`, the write IS a reference store. The barrier would need to fire. The Rev 2 emit ban (§4.1 Span<XActor> BANNED) makes this moot in MVP, but the enumeration should still mention it for completeness.
2. **Ref-typed return value assignment** (`ref var slot = ref obj.field; slot = newValue;`) — C# 7+ ref returns. If `slot` is a ref to an XObject reference field, the assignment is a reference store. Pass 3 enumeration would need to track ref-returns to identify the underlying owner.
3. **fixed (T* p = ...) { *p = x; }** with T being an XObject* — banned per XIL2CPP091 / FIX-A-CRIT-3, but for non-XObject `T*` the *p assignment doesn't trigger any concern; for `XPtr<T>*` the assignment WOULD bypass the barrier. The enumeration should explicitly list fixed-pointer writes and document the ban.

**FIX.** Add to §6.3 enumeration:
- Span<T> indexer setter (mentioned as "banned in MVP per §5.13; for non-XObject T no barrier; for XObject T see XIL2CPP080").
- Ref-typed local assignment (banned via XIL2CPP005 / FIX-A-CRIT-3 for XObject; for non-XObject ref, no barrier).
- Fixed-pointer writes (banned via XIL2CPP091; for non-XObject pointer, no barrier).

**Severity.** HIGH — barrier coverage is positively-listed; omissions are silent bugs.

---

#### FIX-D-HIGH-10 — Recursive struct walk example uses `Holder` field but doesn't show write barrier

**§6.1** (line 2528-2553):
```cpp
struct OuterContainer container = /* ... */;
_liveRefs[1] = XPtr<XObject>(
    reinterpret_cast<XObject*>(container.inner.Holder));
// Subsequent operations on container.inner.Holder maintain _liveRefs[1] consistency:
// every write to container.inner.Holder also updates _liveRefs[1].
```

**Problem.** "Every write to container.inner.Holder also updates _liveRefs[1]" — this is the SECOND emission point per write site (one for the field, one for the shadow stack). But:

1. The write barrier `XPACT_GC_STORE(parent_obj, &container.inner.Holder, newValue)` already exists per §6.3.
2. The shadow-stack update is an ADDITIONAL emission per write.
3. The struct ISN'T an XObject — `container.inner.Holder` is a stack-resident field. There's no `parent_obj` to use in the barrier — the barrier expects the parent to be an XObject (the card-table mark is on XObjects).

So: stack-resident structs with XObject references should NOT emit `XPACT_GC_STORE` (the parent is the stack, not an XObject). The example's "subsequent operations... maintain _liveRefs[1] consistency" hand-waves over the write barrier question.

**FIX.** Document explicitly: for stack-resident structs containing XObject references, writes to the embedded reference field:
- Update `_liveRefs[index]` to the new value (shadow-stack tracking).
- Do NOT emit `XPACT_GC_STORE` because there's no XObject parent for the card-table mark.
- The collector's mark phase sees the new value via the shadow stack.

Specify the pass-3 enumeration walks struct-with-ref fields and marks them as shadow-stack-only writes, not barrier writes.

**Severity.** HIGH — emit rule ambiguity for stack-struct-with-ref.

---

#### FIX-D-HIGH-11 — ABI tag content has FStruct, FScriptStruct version mismatch

**§9.7** (line 3179-3186):
> ```
> XPACT_FSTRUCT_LAYOUT_TAG: "FStruct-v5: 120 bytes; ..."
> XPACT_FSCRIPTSTRUCT_LAYOUT_TAG: "FScriptStruct-v5: 120 FStruct base ... = 136 bytes; ..."
> ```

**Problem.** Contract Rev 13.9 §14.1 says these are versions **v4** in Rev 13.8, bumped to **v5** in Rev 13.9 (confirmed at Contract.html line 86). XIL2CPP §9.7 says v5 — matches.

But §9.7 line 3188-3190 says:
> ```
> XPACT_FCLASS_LAYOUT_TAG: "240 bytes = 120 FStruct base (with appended RefSchema@112) + 120 FClass-specific (with appended LifecycleTable@112 relative to FClass-specific start = FClass-absolute offset 232)"
> ```

This omits the **"FClass-v6:"** prefix that Contract Rev 13.9 §14.1 line 88 explicitly includes:
> `XPACT_FCLASS_LAYOUT_TAG — v4 → v6 (v5 skipped to align the version int with the XCoreXObject Rev 3 spec's §11.1 tag table publication per FIX-N-R2-1): "240 bytes = 120 FStruct base..."`

If the Contract Rev 13.9 content is "FClass-v6: 240 bytes = ..." but Rev 2 §9.7 emits `static_assert(XPactDetail::CompileTimeStrEq(XPACT_FCLASS_LAYOUT_TAG, "240 bytes = ..."))` — the literal strings don't match, and the static_assert fails.

**Wait** — let me re-read Contract Rev 13.9 §14.1 carefully. Line 88 of XToolchainContract.html says the **tag macro is bumped v4 → v6**, but the CONTENT STRING is the one in quotes. Re-reading: the content is "240 bytes = ..." (without "FClass-v6:" prefix). Then the prefix in earlier rows like "FStruct-v5:" is part of the content. So FClass should ALSO have a "FClass-v6:" prefix? Looking at Rev 13.8 line 2381: "FClass-v4: 112 FStruct base + ..." (with prefix). So Rev 13.9 SHOULD have "FClass-v6:" prefix.

**The XIL2CPP Rev 2 emit doesn't include the "FClass-v6:" prefix** — this is a real discrepancy. The static_assert would fire spuriously.

**FIX.** Update §9.7 line 3188-3190:
```cpp
static_assert(XPactDetail::CompileTimeStrEq(XPACT_FCLASS_LAYOUT_TAG,
    "FClass-v6: 240 bytes = 120 FStruct base (with appended RefSchema@112) "
    "+ 120 FClass-specific (with appended LifecycleTable@112 relative to "
    "FClass-specific start = FClass-absolute offset 232)"));
```
Add the "FClass-v6:" prefix.

(Verify all other tags in §9.7 against Contract Rev 13.9 §14.1 verbatim — the FProperty-v2, FFakeVTable-v2, etc. prefixes are present, so the pattern is partial-application.)

**Severity.** HIGH — ABI envelope static_assert would spuriously fail at every TU compile.

---

#### FIX-D-HIGH-12 — sizeof pin missing for `XGCRootSpan` content; only modulus check shown

**§9.7** (line 3242-3243):
> ```cpp
> static_assert(sizeof(XGCRootSpan) == 32);
> static_assert(sizeof(FStackMapRecord) % 8 == 0);
> ```

**Problem.** Contract Rev 13.9 §14.2 enumerates 21 sizeof pins. Rev 2 §9.7 enumerates 22 entries — one extra. The XGCRootSpan=32 IS in §14.2 (line 21 of the table per the 13.9 amendment). But XGCRootSpan isn't in the standard Contract Rev 13.9 §14.2 table (only XObjectArrayEntry, XObjectKey, XWeakPtr, XPtr<XObject>, FXObjectLifecycleTable, FXObjectRefSchema, XObject). Let me re-verify:

XCore-4b Rev 13.9 §14.2 table has 14 reflection-type pins + 7 Rev 13.9 additions = 21 entries (confirmed at XToolchainContract.html line 103). The 7 additions are XObject, FXObjectArrayEntry, XObjectKey, XWeakPtr, XPtr, FXObjectLifecycleTable, FXObjectRefSchema.

So Contract §14.2 has 21 entries, NOT including XGCRootSpan and NOT including FStackMapRecord.

XIL2CPP §9.7 emits:
- 14 reflection-type sizeof pins (matching) — wait, let me count: FName(8), FField(32), FFieldClass(48), FFieldVariant(8), FProperty(104), FFakeVTable(128), FStruct(120), FScriptStruct(136), FCppStructOpsFakeVTable(136), FClass(240), FRepRecord(16), FEnum(72), FInterface(64), FCustomVersion(32) = 14 entries ✓.
- 7 XObject-side sizeof pins (matching): XObject(56), FXObjectArrayEntry(32), XObjectKey(8), XWeakPtr(8), XPtr<XObject>(8), FXObjectLifecycleTable(72), FXObjectRefSchema(24) = 7 entries ✓.
- Total so far: 21 entries ✓ matches Contract.
- THEN: XGCRootSpan(32) + FStackMapRecord modulus check = 2 extra entries = 23 total.

But §9.7 prose says "(Contract Rev 13.9 §14.2 — 21 entries)". So the COUNT is wrong: Rev 2 emits 23 entries with the prose claiming 21. The 2 extras (XGCRootSpan + FStackMapRecord) aren't in Contract §14.2 but are emitted anyway. Either:
- (a) Document the 2 extras as XIL2CPP-side ABI extensions beyond Contract §14.2 (legitimate; XIL2CPP can add its own checks).
- (b) Coordinate Contract Rev 14 to add XGCRootSpan and FStackMapRecord to §14.2.

**Also.** Rev 2 §14.0.1 says: "Contract Rev 13.9 ABI tag count: 24 layout tags + 21 sizeof pins (per the surface table). XIL2CPP §9.7 must emit all of them — not the partial list in Rev 1. Rev 2 has the full list." But Rev 2 emits 23 sizeof, not 21. The count discrepancy is real.

**FIX.** Either:
- (a) Remove the XGCRootSpan and FStackMapRecord sizeof pins from §9.7 (they're not in Contract §14.2); or
- (b) Document them as XIL2CPP-specific extensions: "Plus 2 XIL2CPP-side ABI pins: sizeof(XGCRootSpan)==32; sizeof(FStackMapRecord) % 8 == 0. Forward-commit Contract Rev 14 §14.2 addition."

Update §14.0.1 to reflect the actual count (21 Contract + 2 XIL2CPP-side = 23) and clarify the discrepancy.

**Severity.** HIGH — emit count vs prose count mismatch; documentation drift.

---

### MEDIUM

#### FIX-D-MED-01 — `using` block dispose-time catch fires `XLog::Verbose` from inside `noexcept` lambda

(Covered structurally by FIX-D-HIGH-05 above; rated MEDIUM if XLog::Verbose is guaranteed-noexcept — but XLog is not yet defined here.)

**§5.15** (line 2324-2327): The dispose-time catch logs via XLog at Verbose. XLog::Verbose has no documented noexcept guarantee in any XPact doc. If XLog::Verbose can throw (e.g., buffer-allocation OOM), the catch body propagates, violating the lambda's noexcept.

**FIX.** Either document XLog::Verbose as noexcept (forward-commit XLog spec) OR don't log inside a noexcept guard OR wrap the XLog::Verbose call in another try-catch internally.

**Severity.** MEDIUM — second-order concern; depends on XLog spec.

---

#### FIX-D-MED-02 — §6.3 indexer-setter call labeled "TMap internal barrier" but TArray treatment differs

**§6.3** (line 2645-2655). The enumeration says:
> "Indexer-setter call (`dict[key] = actor`) — the TMap's internal barrier; no caller-site emit needed."

**Problem.** Per §5.7 (line 1755-1757), TArray::Add does emit the barrier internally:
```cpp
XPACT_GC_STORE(m_parent, &m_data[m_count], item.Raw());
```

But §6.3 says the caller doesn't emit a barrier for indexer-setter calls. Two inconsistencies:

1. The TMap's "internal barrier" is mentioned but the emit template for TMap isn't shown in §5.7 (only TArray is shown). The cascade missed TMap.
2. The TArray::Add example uses `m_parent` — but TMap::Set would similarly need `m_parent`. The container-parent inheritance forward-commit (FIX-A-MED-6) applies to TMap too. Show the TMap template emit alongside.

**FIX.** Add to §5.7 a TMap<K, V> template emit showing the internal barrier with `m_parent`. Cross-reference §6.3 enumeration item.

**Severity.** MEDIUM — emit template for TMap container missing.

---

#### FIX-D-MED-03 — §1.4 "Memory-tag attribution table added to §6" referenced but table is in §6.9

**§1.4 table row for XCoreXObject** says:
> "FIX-A-MED-33; Memory-tag attribution table added to §6."

But the actual memory-tag attribution table is in **§6.9** (line 2774-2786). Section reference granularity is inconsistent.

**FIX.** Update the §1.4 row to say "§6.9" specifically.

**Severity.** MEDIUM — navigation hint inaccurate.

---

#### FIX-D-MED-04 — §5.21 `using X = SomeType;` requires file-scope but C++ namespace is not file-scope

**§5.21** (line 2463-2466):
> ```cpp
> // C++ output (in HealthPickup.cs.h or equivalent .cs.h):
> namespace {
>     using FlowMap = ::XCore::Container::TMap<::FName, ::XCore::Reflect::XPtr<::XActor>>;
> }
> ```

**Problem.** C++ anonymous namespace at the top level of a header is a common antipattern — it has internal linkage per TU, so every TU that includes the header gets ITS OWN copy of the alias. This is fine for type aliases (which are just compile-time names; no ODR issue), but the comment "File-scoped: the using alias lives in the file's anonymous namespace so it does not leak to other TUs" is somewhat misleading — the alias DOES leak to every TU that includes the header, just each gets its own local copy.

**Also.** If the alias is in a header and the header is included in 100 TUs, the alias is declared 100 times in anonymous namespaces — at the C++ level this is fine, but at the IDE-navigation level it's confusing.

**FIX.** Move the alias to the `.cs.cpp` file (which is single-TU, anonymous namespace is appropriate) instead of the `.cs.h` header. Or use a `using` declaration in the named C# namespace (`Simgenics::XPact::Game`) so the alias is per-namespace rather than per-TU. Document the actual scope.

**Severity.** MEDIUM — emit pattern is functional but documentation overclaims about scope.

---

#### FIX-D-MED-05 — `XPACT_MAX_STACKALLOC_BYTES = 64KB` is the limit but `alloca` is unguarded for static-sized

**§5.13** (line 2284-2289):
> "Compile-time-sized stackalloc (`stackalloc int[16]`) emits no runtime check; the size is statically known."

**Problem.** A C# author writing `stackalloc int[100_000]` would emit `int32_t _stack_buf[100000];` — 400 KB on the stack, no runtime check. The 64 KB limit applies only to dynamically-sized stackalloc.

**FIX.** Add a Pass 3 check: compile-time-sized stackalloc whose total size exceeds `XPACT_MAX_STACKALLOC_BYTES` emits diagnostic XIL2CPP08x — `stackalloc T[N] exceeds 64KB stack-allocation limit (static size)`. The author can either reduce N or use TArray.

**Severity.** MEDIUM — limit advertised but not enforced for static-size case.

---

#### FIX-D-MED-06 — §6.5 `[[unlikely]]` is C++20 attribute but on a do-while branch is ambiguous syntax

**§6.5** (line 2698-2704):
> ```cpp
> #define XPACT_SAFEPOINT_CHECK() \
>     do { \
>         if ([[unlikely]] XGCSafepoint::s_tlsPendingFlag) { \
>             XGCSafepoint::HandlePending(); \
>         } \
>     } while (0)
> ```

**Problem.** Per C++20 [dcl.attr.unlikely], the `[[unlikely]]` attribute applies to a **label** or a **statement**. The position `if ([[unlikely]] condition)` places the attribute on the EXPRESSION, which is not what `[[unlikely]]` accepts. The correct C++20 syntax is:
```cpp
if (condition) [[unlikely]] {
    ...
}
```
The attribute follows the parenthesized condition.

**FIX.** Correct the macro to:
```cpp
#define XPACT_SAFEPOINT_CHECK() \
    do { \
        if (XGCSafepoint::s_tlsPendingFlag) [[unlikely]] { \
            XGCSafepoint::HandlePending(); \
        } \
    } while (0)
```

**Severity.** MEDIUM — C++ syntax error; would fail compilation on conformant C++20 compilers.

---

#### FIX-D-MED-07 — XIL2CPP049 has overloaded meanings (Random AND FName-from-literal)

**§12** (line 3447):
> "XIL2CPP049 — Error / Warning — Random.* banned on sim-path; OR sim-path code constructs FName from non-literal string"

**Problem.** Per FIX-A-HIGH-3 and FIX-A-MED-2, both diagnostics were ASSIGNED the SAME code (XIL2CPP049). Two distinct sim-path violations sharing one code:
- The author sees "XIL2CPP049" but doesn't know which one fired without reading the message.
- The IDE's quick-fix infrastructure can't disambiguate.
- Cross-referencing in the catalog is ambiguous.

The same pattern recurs:
- XIL2CPP033 (line 3433): "Exception filter relies on non-unwinding semantics; OR [XOnClassReplaced] method has wrong signature"
- XIL2CPP053 (line 3452): "Type.MakeGenericType / Type.MakeGenericMethod; OR locale-dependent ToString/Parse on sim-path"
- XIL2CPP054 (line 3453): "typeof(T) at open-generic site; OR [ThreadStatic] banned on sim-path"
- XIL2CPP125 (line 3487): "Generic constraint T : unmanaged violated; OR cross-module duplicate FClass"
- XIL2CPP145 (line 3495): "vtable slot count/order changed; OR backing-field naming mismatch"

These are 6 overloaded codes — diagnostic catalog hygiene is broken.

**FIX.** Allocate distinct codes per diagnostic. The catalog has reserved bands (110-119, 130-139, 150-159) — use them. Specifically:
- XIL2CPP049 → keep for one of the two; allocate XIL2CPP155 for the other.
- XIL2CPP033 → keep for filter; allocate XIL2CPP156 for OnClassReplaced.
- (etc.)

The Rev 2 changelog explicitly mentions FIX-B-MINOR-10 / FIX-C-MED-15 / FIX-C-LOW-01 about reserved bands; use them.

**Severity.** MEDIUM — diagnostic catalog is fundamental UX; overloaded codes are a maintainability defect.

---

#### FIX-D-MED-08 — `Z_Construct_FClass_*` body uses `static const FClass* sCachedClass = ...` not constexpr

**§5.1** (line 939-947):
> ```cpp
> extern "C" const FClass* Z_Construct_FClass_MyModule_XHealthPickup() noexcept
> {
>     static const FClass* sCachedClass = []() noexcept {
>         const auto* cls = &XHealthPickup_Class;
>         XReflectionRuntime::RegisterClass(cls);
>         return cls;
>     }();
>     return sCachedClass;
> }
> ```

**Problem.** This is a function-local static with thread-safe-init (C++11 mandatory). Three subtle issues:
1. **First-call thread-safe init.** The lambda runs once. If two threads call `Z_Construct_FClass_*` concurrently on first use, only one wins. The C++ standard guarantees this. OK.
2. **`RegisterClass` is called once.** Good — no double-registration.
3. **But the call site has no safe-point check.** `Z_Construct_FClass_*` is reachable from XReflectionRuntime::FindClass and from `obj.GetType()` chains; if the runtime is in a GC mark phase, the registration COULD race with the marker walking the class table.

This is XCoreXObject's domain — but XIL2CPP's emit of `Z_Construct_FClass_*` should add a safe-point check at the start of the lambda body so the registration happens at a safe-point boundary.

**FIX.** Add `XPACT_SAFEPOINT_CHECK()` at the start of the lambda body, OR document that `RegisterClass` is GC-safe internally (forward-commit XCoreXObject Rev 5 to document this property).

**Severity.** MEDIUM — race between class registration and GC mark phase is plausible.

---

#### FIX-D-MED-09 — XIL2CPP100 diagnostic title says "post-MVP" but XIL2CPP020 says "MVP-supported" — inconsistency

**§12 XIL2CPP100** (line 3482):
> "LINQ is post-MVP; use explicit foreach + TArray helpers"

**§12 XIL2CPP020** (line 3425):
> "Module declares LangVersion higher than MVP-supported C# 12"

**Problem.** Different wording for similar concerns. XIL2CPP100 says "post-MVP" (future MVP); XIL2CPP020 says "MVP-supported" (current state). The "MVP" suffix means different things. Same applies to XIL2CPP015 / XIL2CPP016 ("is post-MVP"). Consistency across catalog wording would help authors understand the deferred-vs-permanent distinction.

**FIX.** Standardize: "is permanently BANNED" vs "is deferred to post-MVP". Apply to entire catalog.

**Severity.** MEDIUM — minor UX inconsistency.

---

#### FIX-D-MED-10 — §3.4 cache key includes `XIL2CPP_binary_content_hash` but not Roslyn workspace cache state

**§3.4** (line 545-548):
> ```
> CacheKey = SHA256(
>   XIL2CPP_binary_content_hash,
>   XPact_Mangling_NuGet_version,
>   ...
> ```

**Problem.** The in-process mode per §3.5 says Roslyn workspace is "per-call" — but if Pass 2 mode keeps a cached merged TierTable in memory across calls, the cache key should reflect that. Currently the key doesn't include `MergedTierTable_hash` or equivalent.

**FIX.** Add to cache key:
- `MergedTierTable_hash` (for Pass 2 mode; empty in Pass 1 mode).
- `InProcessSessionId` (to distinguish in-process call 1 vs call 2; reset by `ResetAsync`).

**Severity.** MEDIUM — Pass 2 mode cache could see stale state across calls.

---

#### FIX-D-MED-11 — §5.5 `XCSharpStringBuilder` rename forward-commit references `XPact.CSharp.BCL Rev 2` but XCore-4a Rev 3 already has `FStringBuilder`

**§5.5** (line 1635):
> "Note: the existing Rev 1 mentions of `XCSharpStringBuilder`... refer to a type in the `XPact.CSharp.BCL` NuGet. If XCore-4a's existing `FStringBuilder` covers the use case, the BCL surface aliases to it; if not, `XCSharpStringBuilder` is added as a new BCL type."

**Problem.** This is an unresolved alternative. The doc should commit to one path. If XCore-4a's `FStringBuilder` covers the use case, the type alias is the right answer; if not, a new type is. The Rev 2 forward-commit `BCL-Rev2-HANDLER` (line 3588) only mentions `XDefaultInterpolatedStringHandler + XScopedGuard + XStackTraceHandle`. It does NOT add `XCSharpStringBuilder`. So the BCL-Rev2 commitment is for the unknown case.

**FIX.** Check XCore-4a Rev 3 §X for `FStringBuilder` — if it exists, document the alias in §5.5; if not, add to `BCL-Rev2-HANDLER` forward-commit table row.

**Severity.** MEDIUM — incomplete forward-commitment.

---

#### FIX-D-MED-12 — §5.7 tuple type cache key collision: `(int X, string Y)` and `(int A, string B)` both → `_Tuple_int_XCSharpString`

**§5.7** (line 1803-1810):
> ```cpp
> struct _Tuple_int_XCSharpString {
>     int32_t Item1;
>     XCSharpString Item2;
>     bool operator==(const _Tuple_int_XCSharpString& other) const = default;
> };
> ```

**Problem.** Two C# tuples of the same shape but different element names map to the same C++ struct — this is intended per FIX-B-MEDIUM-10. But:
1. **Per-TU emission collision.** If `(int X, string Y)` is used in TU A and `(int A, string B)` is used in TU B, both TUs emit `_Tuple_int_XCSharpString`. The C++ linker accepts this only if the struct definition is BYTE-IDENTICAL across the TUs (ODR).
2. **`operator==` default behavior.** `operator== = default` requires C++20 default-comparison support. Across compilers, the implementations are slightly different (MSVC's defaulted comparison vs Clang's). Cross-arch consistency may differ.

**FIX.** Specify:
- (a) Tuple struct emission is in a shared inline header (e.g., `Generated/Tuples.h`) so every TU sees the same definition.
- (b) Or `inline` keyword + `selectany` for the tuple types so linker dedups.
- (c) Document that defaulted `operator==` is required and gives byte-identical results across MSVC 19.44+ / Clang 17+ / GCC 13+.

**Severity.** MEDIUM — emit ODR question is real.

---

#### FIX-D-MED-13 — §5.9 lambda's `<Method>$()` clone reference doesn't exist in show — FIX-B-MEDIUM-02 mention vs lambda emit

**§3.2 / §5.1** (line 1042-1043):
> Records emit `Clone() const { return *this; }` for record's clone semantics.

**Versus §5.9** — no mention of synthesized `<Clone>$` for non-record class with lambda capture, even though the Rev 2 changelog says: "with expression on record-class invokes synthesized `<Clone>$()` and init-setter-with-privilege" (FIX-B-MEDIUM-02 disposition).

**Problem.** `with`-expression emit is mentioned in changelog but not shown in any §5.* example. Round 1 audit B's FIX-B-MEDIUM-02 expected the emit to be documented. The Rev 2 cascade was incomplete.

**FIX.** Add to §5.1 or §5.4 an emit example for `with`-expression:
```cpp
// C# input:
//   var p2 = p1 with { X = 10 };
// C++ output:
auto p2 = p1.Clone(); // p1's <Clone>$() — emits a copy of the record
p2.X = 10; // init-setter via privileged path
```

**Severity.** MEDIUM — cascade missed an emit pattern.

---

### LOW

#### FIX-D-LOW-01 — §5.8 generic-method principal-TU rule references "lexicographically-first source file" but doesn't define lexicographic order

**§5.8** (line 1844):
> "the closed-instantiation FClass is emitted by the module where the closed instantiation is FIRST referenced in the dependency-aware closed-walk — specifically, the lexicographically-first dependent module that touches the closed instantiation."

**Problem.** Lexicographic order is unambiguous for ASCII but ambiguous for Unicode module names (e.g., with accented characters). The order across Windows vs Linux filesystems may differ.

**FIX.** Specify "lexicographic by Unicode codepoint" or "lexicographic by NFC-normalized Unicode codepoint" — either is unambiguous.

**Severity.** LOW — pedantic but determinism-relevant.

---

#### FIX-D-LOW-02 — §6.1 shadow-stack array `_liveRefs[N]` doesn't show initialization for partial-scope locals

**§6.1** (line 2571-2589) shadow-stack example for `DoWork()`. N=3 (self, a, c). The example writes all three slots up-front. But what if `c` is declared INSIDE a conditional block? The §6.1 prose mentions "PC range demarcates the range" — but the example doesn't show a multi-range record.

**FIX.** Add a second example showing conditional locals + multiple PC ranges in the FStackMapRecord. Currently it's only the simple "linear function" case.

**Severity.** LOW — example coverage gap.

---

#### FIX-D-LOW-03 — §8.4 listener `OnClassReplaced` body uses `XIL2CPP_RebindClassCache` undefined function

**§8.4** (line 3012):
> `::XCore::Reflect::XIL2CPP_RebindClassCache(ctx.OldClass, ctx.NewClass);`

**Problem.** `XIL2CPP_RebindClassCache` is referenced but not defined in any cited spec. It's used in the OnClassReplaced callback to "walk the per-module cache table" — but the cache table data structure isn't defined either. This is a forward reference to an unspecified function.

**FIX.** Either:
- (a) Document `XIL2CPP_RebindClassCache` in §8.4 with signature + behavior.
- (b) Mark as forward-commit XCoreXObject Rev 5 (alongside `IXObjectClassReplacedListener`).

**Severity.** LOW — undefined identifier in spec.

---

#### FIX-D-LOW-04 — §10.4 forward-commits XHT Rev 7 schema vector emit but doesn't specify HOW XIL2CPP signals "I'm consuming the schema vector"

**§10.4** (line 3322-3332). The Q14 resolution says XHT emits the schema vector for C#-declared types. But XIL2CPP needs to read it to consume FProperty descriptors. The contract surface for this read isn't specified.

**FIX.** Add to §10.4 the read protocol: "XIL2CPP reads the schema vector via `XReflectionRuntime::GetClassSchemaVector(cls)` returning `TConstSpan<FXObjectRefSchemaOp>`. Forward-commit XCoreXObject Rev 5."

**Severity.** LOW — cross-tool data flow missing detail.

---

#### FIX-D-LOW-05 — §13 BoxingBanTest expected emit doesn't include XIL2CPP073 explicitly listed

**§13** (line 3530):
> "**BoxingBanTest** [Rev 2 addition per FIX-B-CRIT-02 / FIX-B-CRIT-06]: 30 sim-path TUs that attempt to box value types... Every TU MUST emit the appropriate diagnostic; the XIL2CPP001/060/061/062/073 codes are validated."

**Problem.** XIL2CPP001 is the "new Foo()" diagnostic, NOT a boxing diagnostic. Including it in the BoxingBanTest's expected codes conflates two unrelated concerns. The boxing diagnostics are XIL2CPP060 / 061 / 062 / 073. XIL2CPP001 belongs to the X-IL2CPP-NEW-FACTORY test.

**FIX.** Remove XIL2CPP001 from the BoxingBanTest expected codes; keep it as the assertion of the X-IL2CPP-NEW-FACTORY test.

**Severity.** LOW — test target coverage incorrectness.

---

#### FIX-D-LOW-06 — §3.5 `ResetAsync` says ~10-50 ms cost but should specify GC.Collect generation

**§3.5** (line 619):
> "invokes GC.Collect(generation: 2, mode: Forced) to compact freed managed memory."

**Problem.** GC.Collect with `mode: Forced` is well-specified, but in .NET 8+ Forced mode is deprecated. Use `GCCollectionMode.Aggressive` (which is .NET 7+ canonical) or `GCCollectionMode.Default`.

**FIX.** Update to `GCCollectionMode.Aggressive` for the documented behavior.

**Severity.** LOW — minor API drift.

---

### MINOR / NIT

#### FIX-D-NIT-01 — §2.3 still cites "XPact.CoreXObject.XObjectInternalConstructorAttribute" but §5.21 / §10.5 use various abbreviated forms

**§2.3** (line 257-261): cites full namespace `XPact.CoreXObject.XObjectInternalConstructorAttribute`. §10.5 cites the same. But some places (e.g., §1.4 row, §10.5 line 3342) say `XPact.CoreXObject.XObjectInternalConstructorAttribute`. The namespace is documented consistently — good. But the `Attribute` suffix is sometimes omitted (Roslyn convention; technically OK). Document the convention: full attribute name with `Attribute` suffix is canonical.

**Severity.** NIT.

---

#### FIX-D-NIT-02 — §11.2 acceptance gates table has X-IL2CPP-SHADOWSTACK-COV cell that wraps awkwardly

**§11.2** (line 3395). The cell content has the [Rev 2 addition] marker mid-row and the "Test:" subnote runs together. Cosmetic.

**Severity.** NIT.

---

#### FIX-D-NIT-03 — §14.1 (Round 2 contradictions surfaced) mentions XCoreXObject §1.3 lifecycle 8 vs Rev 2 7 — but Rev 2 emits 8

§14.0.1 line 3598:
> "XCoreXObject §1.3 says lifecycle table has 8 slots; Rev 2 enumerates 7 explicit slots."

Verified: Rev 2 §5.1 line 906-921 enumerates 8 slot bodies. The Round 2 audit input's claim of "Rev 2 enumerates 7" is incorrect — there are 8 (PostInitProperties, BeginDestroy, IsReadyForFinishDestroy, FinishDestroy, Serialize, AddReferencedObjects, PostLoad, PreSave). The cross-system contradiction "Rev 2 lifecycle 7 vs XCoreXObject 8" is phantom.

**FIX.** Remove the §14.0.1 first bullet (phantom contradiction).

**Severity.** NIT.

---

#### FIX-D-NIT-04 — §16 revision history Rev 2 cell length exceeds reasonable table-row content

§16 revision history row for Rev 2 is ~7,200 characters in a single table cell. This is a documentation rendering concern: most browsers handle it fine, but PDF export / Word import may break.

**FIX.** Move the Rev 2 detail to a separate dedicated section "Rev 2 detailed change log" with the table cell pointing to it.

**Severity.** NIT.

---

## Cross-system contradictions verified

### Re §14.0.1 entries

1. **Lifecycle table slot count (8 vs 7).** **PHANTOM** — Rev 2 §5.1 emits 8 slots. The Round 2 input was wrong. See FIX-D-NIT-03.

2. **Contract Rev 13.9 ABI tag count (24 layout + 21 sizeof).** Verified Contract Rev 13.9 §14.1 has 24 layout tags + §14.2 has 21 sizeof pins. Rev 2 §9.7 emits 24 layout tags ✓ and **23 sizeof pins** (21 Contract + 2 XIL2CPP-side: XGCRootSpan + FStackMapRecord). The "21" claim in §14.0.1 is incomplete; the actual Rev 2 emit is 23. See FIX-D-HIGH-12.

3. **XHT NoThrow surface duality (C# + C++).** Verified: the forward-commit XHT-Rev7-NOTHROW-CPP in §14.0 covers this (line 3581). The §14.0.1 third bullet is correct.

4. **Boxing wrappers Int32Box/FloatBox not in BCL.** Verified: forward-commit BCL-Rev2-BOXING in §14.0 (line 3587). The fourth bullet is correct. **BUT** see FIX-D-CRIT-03 about the deeper construction-path issue.

5. **Manifest schema_version vs contract_version.** Verified: forward-commit XBT-Rev11-MANIFEST-FIELDS in §14.0 (line 3584) covers `schema_version: uint32` addition. The fifth bullet is correct.

### New cross-system contradictions D-D found

6. **XBT slot 17 reuse.** FIX-D-CRIT-01 above.
7. **XBT `run-xil2cpp` mode stub vs XIL2CPP binary stub exit code.** FIX-D-CRIT-06 above.
8. **FClass content tag prefix missing.** FIX-D-HIGH-11 above.

---

## Surface-only fixes (FIX did not address ROOT cause)

### FIX-B-CRIT-02 / FIX-B-CRIT-06 — Boxing ban: SURFACE-ONLY

The ban (XIL2CPP062 / 073) addresses the SYMPTOM: implicit conversion of value to `object` fails. But the ROOT cause — what is the canonical way to author code that needs to put primitives into `List<object>` or `Dictionary<K, object>` — is not specified. The "explicit Int32Box-style wrapper" answer is mentioned but the wrapper's construction story (Outer? Lifetime? Pool?) is undefined. **See FIX-D-CRIT-03.**

### FIX-A-MED-6 — TArray container-parent inheritance: SURFACE-ONLY

The Rev 2 emit shows `m_parent` field added to TArray, but the forward-commit XCXO-Rev5-CONTAINER-PARENT (line 3578) says the field is XCoreXObject Rev 5's addition. The emit reads as if the field already exists; the forward-commit reads as if it's a future addition. The actual ROOT — when does the field land, and how does XIL2CPP's emit fit in until then — is unspecified. **See FIX-D-CRIT-04.**

### FIX-A-CRIT-3 — ref/out XObject ban: ADDRESSES SYMPTOM, leaves new pattern undefined

XIL2CPP005 forbids `ref XObject` / `out XObject`. Authors using such patterns must adopt `Result<T, E>` or nullable returns. But the `Result<T, E>` type itself isn't defined in any XPact spec — it's mentioned as if it exists, but is it XCore-4a-side? BCL-side? Or a new type? Without specification, authors can't migrate.

**FIX.** Forward-commit `BCL-Rev2-RESULT` (or similar) to publish `Result<T, E>` with its API surface.

### FIX-C-CRIT-04 — ReferenceCompileCSharpAction ordinal: SURFACE-ONLY

The Rev 1 ordinal-14 conflict was identified, and slot-17 was proposed. But slot 17 itself has the same class of conflict (per FIX-D-CRIT-01). The Rev 2 fix didn't dig deep enough into XBT.html Rev 10's slot-stability invariant.

### FIX-A-CRIT-2 — Shadow-stack: PARTIALLY ADDRESSES ROOT

The shadow-stack mechanism is a strong replacement for register-spilling. But the cascade to §15 Phase 6.g was missed (FIX-D-CRIT-02), and the example's safe-point ordering is ambiguous (FIX-D-HIGH-02). The body changes are correct; the cascade is incomplete.

---

## Conclusion

Rev 2 represents **substantial structural improvement** over Rev 1: the major architectural decisions (shadow-stack, typed listener, boxing ban, display-class lambdas, full ABI tag enumeration) are correct, well-reasoned, and well-cascaded within the body of the document. The Rev 2 changelog claims "applied 155/161" — most of the body-of-text claims hold up under audit.

**However:** Rev 2 has substantial residual issues that require Rev 3. Specifically:

1. **§15 (implementation order) was not updated** to reflect §6.1 / §8.4 / §9.8 body changes. Phases 6.a / 6.g / 6.k still cite Rev 1 mechanisms. (FIX-D-CRIT-02, FIX-D-CRIT-05, FIX-D-HIGH-01.)
2. **XBT slot 17 reuse is forbidden** by XBT.html Rev 10 — the FIX-C-CRIT-04 reconciliation chose the one slot that breaks the XBT contract. (FIX-D-CRIT-01.)
3. **Boxing wrapper construction path is undefined.** The Rev 2 ban is correct, but the alternative pathway (`Int32Box` / `FloatBox`) has no construction story. (FIX-D-CRIT-03.)
4. **TArray `m_parent` field is shown as present but is forward-committed to XCoreXObject Rev 5.** Emit timing is undefined. (FIX-D-CRIT-04.)
5. **XBT-vs-XIL2CPP-binary Phase 1 stub exit codes diverge** (24 vs 61). (FIX-D-CRIT-06.)
6. **ABI tag content has FClass-v6 prefix omitted** — static_assert would spuriously fail. (FIX-D-HIGH-11.)
7. **23 sizeof pins emitted vs 21 documented.** Documentation drift. (FIX-D-HIGH-12.)

**Recommendation:** Rev 3 is necessary. Rev 4 is likely (the 8 forward-commitments to other systems may surface defects on their target spec audit).

**Severity distribution.** 6 CRITICAL findings is high relative to the changelog's claim of "all CRITICAL addressed" — but most of these are NEW critical issues introduced by the Rev 2 edit body or by incomplete cascade rather than regressions of Rev 1 critical findings. The Rev 1 critical fixes (shadow-stack, ABI envelope, etc.) DID land structurally; the residual criticals are about the cascade landing across §15 and the boundary with XBT.

---

*End of XIL2CPP-Rev2-Audit-D-Regression.md.*
