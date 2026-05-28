# XIL2CPP Rev 1 Audit — Perspective A: GC + Sim-path

Auditor: Claude Code subagent (Perspective A — GC integration + sim-path discipline + cross-arch determinism)
Audit date: 2026-05-28
Audited revision: `Documents/XIL2CPP.html` Rev 1 (HEAD of `main`; status "AWAITING ROUND 1 AUDIT")
Cross-referenced docs:
- `Documents/XCoreXObject.html` Rev 4 (CONVERGED) §1.3, §2.4, §3.5, §4, §5.2–5.6, §10.7, §13.2
- `Documents/XToolchainContract.html` Rev 13.9 §0, §3, §4, §5, §10
- `Documents/XCore-4b.html` Rev 4 (FName XXH3 scalar hash; FProperty taxonomy)
- `Documents/XBT.html` Rev 10 (FPSemantics enum)
- `Documents/XIL2CPP-Constraints.md` (Sections 4-5 + Section 10 open questions)

## Summary

- Total findings: **47**
- CRITICAL: **6**
- HIGH: **13**
- MEDIUM: **17**
- LOW: **8**
- NIT: **3**

## Top 3 most impactful findings

1. **FIX-A-CRIT-1** — Rev 1 emits the `XPACT_*_LAYOUT_TAG` `static_assert`s with **wrong tag-content strings** (`"v1"`, `"v6"`) instead of the contract-defined human-readable layout descriptions (`"56-byte XObject header"`, `"240 bytes = 120 FStruct base..."`). Every transpiled `.cs.cpp` will fail to compile against Contract Rev 13.9, because `XPACT_*_LAYOUT_TAG` macros are content strings, not version ints. This breaks the ABI-envelope-pinning mechanism *as currently specified*. The same kind of error appears in §5.1 and §9.7.
2. **FIX-A-CRIT-2** — The §6.1 stack-map design says XIL2CPP will "emit explicit register spilling assembly" at every safe-point. This is **architecturally infeasible** under a pure-C++ output model with MSVC / Clang / GCC, which do not accept inline-asm safe-point fixups portably; the C++ compiler owns register allocation and spill placement. The correct mechanism for precise stack scanning of C++-compiled functions is one of: (a) LLVM `gc.statepoint` intrinsics (only Clang, breaks MSVC + GCC, breaks the toolchain matrix); (b) volatile-store sinks that prevent the optimizer from holding XObject* in callee-saved regs across `XPACT_SAFEPOINT_CHECK()`; (c) explicit C++ "shadow stack" of `XPtr<T>*`s the optimizer cannot fold away. Rev 1 promises (a) at design-time but ships none of the implementation mechanics — the audit must force a real mechanism in Rev 2 because the X10 acceptance gate ("100% of XObject locals enumerated") depends on this and the current spec cannot satisfy it.
3. **FIX-A-CRIT-3** — Rev 1 does **not specify how `XPACT_GC_STORE` works for `ref` / `out` parameters of XObject reference type**, which is a routine C# pattern (`TryGetActor(out XActor actor)`, `void Promote(ref XPtr<XActor> slot)`). The callee receives a `void**`-equivalent and writes through it; the LHS-resolution rule in §6.3 only walks `AssignmentExpressionSyntax` on the *callee's* side, where the slot is an alias for a *caller's* field. The barrier must fire against the **caller's parent object**, which the callee does not know. Either ban `ref XObject` parameters (cleanest), or specify a wrapper-type protocol (e.g., `XRef<T>` ABI-pair `{XObject* parent, T** slot}` so the callee can call `XPACT_GC_STORE(parent, slot, newValue)`). This is silently wrong as currently specified — GC will lose live references.

## Whether Rev 1 is structurally sound

**Rev 1 needs significant rework (more than one round).** The pipeline architecture, tier-classification protocol, and locked-commitment enforcement are all sound. But the GC-integration section (§6) and sim-path discipline (§7) — the two sections this audit perspective is responsible for — have **multiple architectural gaps that ship a broken design**:

- §6.1's stack-spill-at-safe-point story is a hand-wave; no portable mechanism is specified.
- §6.3's reference-store enumeration only covers `AssignmentExpressionSyntax`, missing ~8 distinct C# write paths (compound assignment, `??=`, `with` expressions, object/collection initializers, `ref`/`out`, tuple deconstruction with reference, indexer setters, primary-constructor field init, default-interface-property setter, `init` accessor, deconstruct assignment).
- §6.5's safe-point design assumes `XPACT_SAFEPOINT_CHECK()` exists in `XCoreXObject` but XCoreXObject Rev 4 only documents `g_SafePointRequested` flag-style checks at conceptual "safe-point insertion points" — the macro name and lowering are NOT in the locked contract.
- §7 misses several determinism hazards: no policy on `Math.Sin`/`Cos`/`Sqrt` cross-compiler bit-exactness; no policy on cross-arch memory ordering for the few atomic ops the doc admits exist (`Interlocked.*` is mentioned nowhere); no policy on `double.ToString` / `float.Parse` whose impl is locale-dependent; no policy on FName-from-runtime-string (the constructor in §5.3's emit example, `::FName("Pickup_1")`, requires runtime interning, which is non-deterministic across init order if the literal is dynamic).
- §6.7 misses the recursive-struct case (struct-in-struct-in-stack-frame).

The audit found **47 issues, of which 6 are CRITICAL and 13 are HIGH** — comparable to the XCoreXObject Round 1 audit (65 fixes) given XIL2CPP Rev 1's broader surface area but narrower scope per perspective.

## Cross-system contradictions uncovered (not in constraint inventory)

- **§9.7 lists `XPACT_FP_SEMANTICS_TAG` but Contract Rev 13.9 §10.2 line 1984 explicitly excludes FPSemantics from the manifest schema.** XIL2CPP cannot `static_assert` against a tag the Contract does not define. (FIX-A-CRIT-4)
- **§5.1 says "XHT emits the static FXObjectLifecycleTable for this XClass"; §10.2 says XIL2CPP emits it for C#-declared classes.** Internal contradiction in the doc itself. (FIX-A-HIGH-1)
- **§6.5 references `XCoreXObject.html` §5.5 line 1301 ("~3-5 cycles per call")** but the cited section actually says "~2 cycles" for the TLS-cached check (steady-state). The 3-5 cycle figure in XCoreXObject is for the *write barrier* (line 1214), not the safe-point check. (FIX-A-LOW-3)
- **§7.4's `XSimPathBannedAPIs.txt` list contains `object.GetHashCode()` as the runtime ref-hash check.** But §5.10's lock-mapping uses `FObjectMonitorRegistry::GetOrCreate(someObject)` which key on object identity — i.e., reference-hash-of-the-XObject. The same mechanism that's banned on sim-path is needed by `lock(obj)` mapping; while both happen to be sim-path-banned, the implementation has an implicit shared dependency that nobody noticed. (FIX-A-MED-13)

---

## Findings

### FIX-A-CRIT-1 [severity: CRITICAL] — ABI envelope tags emit wrong content strings

**Location**: XIL2CPP.html §5.1 (lines 783-788), §9.7 (lines 2282-2299).

**Issue**: Both code blocks emit `static_assert` against the `XPACT_*_LAYOUT_TAG` macros using **version-int strings**:

```cpp
static_assert(::XPactDetail::CompileTimeStrEq(XPACT_FCLASS_LAYOUT_TAG, "v6"));
static_assert(::XPactDetail::CompileTimeStrEq(XPACT_XOBJECT_LAYOUT_TAG, "v1"));
static_assert(::XPactDetail::CompileTimeStrEq(XPACT_XGC_CARDTABLE_LAYOUT_TAG, "v1"));
// etc.
```

But per `XToolchainContract.html` Rev 13.9 §0 lines 86-99, the tag content is a **human-readable layout description**, not a version int:

- `XPACT_XOBJECT_LAYOUT_TAG` → `"56-byte XObject header"`
- `XPACT_FCLASS_LAYOUT_TAG` → `"240 bytes = 120 FStruct base (with appended RefSchema@112) + 120 FClass-specific (with appended LifecycleTable@112 relative to FClass-specific start = FClass-absolute offset 232)"` (Rev 13.9 content)
- `XPACT_FSTRUCT_LAYOUT_TAG` → `"120 bytes; appends RefSchema@112"` (v5)
- `XPACT_XOBJECT_LIFECYCLE_TABLE_TAG` → `"72-byte per-FClass lifecycle dispatch table (8-byte header + 8 slots * 8 bytes)"`
- `XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG` → `"24-byte schema-vector header (NumOps + Version + Ops* + _padTail)"`

The "v1" / "v6" suffixes in the contract are the *version int* identifying which iteration the content describes; they are NOT the macro expansion content. XIL2CPP transpiled `.cs.cpp` files emitting `static_assert(... "v1")` will fail every compile because the macro expands to a different string.

**Fix**: Replace every `"v1"` / `"v6"` / `"v5"` literal in the `static_assert` blocks with the exact tag content string from Contract Rev 13.9 §0 (the audit recommends generating these strings from the same `ContractSurface.AbiLayoutTags` table the Contract uses, rather than re-typing them in XIL2CPP source). For Rev 2: replace §5.1 lines 783-788 + §9.7 lines 2282-2299 with the verbatim content strings, OR change the design to emit "the macro's expanded content as recorded in the manifest's `gc_root_abi` / `mangling_scheme` fields" with a generated table.

Severity rationale: every transpiled .cs.cpp would fail to compile against Contract Rev 13.9. Ships a non-functional system.

---

### FIX-A-CRIT-2 [severity: CRITICAL] — Stack-map "explicit register spilling" is not implementable as specified

**Location**: XIL2CPP.html §6.1, paragraph beginning "Critical design decision (live-ref enumeration via stack-spilling)" (lines 1888-1889).

**Issue**: Rev 1 commits to: "XIL2CPP solves this by emitting an explicit spill at every safe-point: at function entry, immediately before `XPACT_SAFEPOINT_CHECK()`, every live XObject* register is spilled to a designated stack slot. The FStackMapRecord lists those slot offsets."

This is **architecturally infeasible** under the locked commitment of "pure C++ output, MSVC + Clang + GCC toolchain matrix":

1. **MSVC, Clang, and GCC own register allocation**. There is no portable C++ surface to force a specific local to a specific stack offset. A C++ program emits source code; the optimizer decides whether a local lives in a callee-saved register, a caller-saved register, or a stack slot — and this choice differs **per-arch and per-optimization-flag**. The same `.cs.cpp` compiled on MSVC vs Clang will produce different register allocations for the same local. The FStackMapRecord at .rodata-emit time would have to commit to a stack offset that doesn't yet exist.
2. **The "designated stack slot" language assumes XIL2CPP can emit inline assembly** to copy from RCX/RDX/R8/R9 (Win64) or X0–X7 (AAPCS64) to a stack slot. This is not portable. MSVC dropped inline asm for x64 in 2008; Clang's `__asm__` is GCC-style only; GCC's syntax differs from Clang's; ARM64 inline asm differs from x64. The C++ source XIL2CPP emits cannot contain `mov [rsp+0x40], rcx`-style asm.
3. **Even if (2) were solved, the C++ optimizer can rematerialize the register from the stack-spill slot**, leaving the stack-spill slot stale by the time the safe-point fires. The current §6.1 design does not mention this.

The Rev 1 design hand-waves the implementation. The correct mechanism for stack-map-driven precise scanning of C++-compiled output is documented in the Unity IL2CPP / V8 / Mono SGen literature:

- **Option A (compiler intrinsics)**: LLVM `gc.statepoint` + `gc.relocate` intrinsics generate a stack-map directly from the IR. Clang supports this (with `-Xclang -fexperimental-gc-statepoints`); MSVC does NOT (no equivalent intrinsic); GCC has partial support via `-fgcse`. **Cannot satisfy the locked toolchain matrix.**
- **Option B (forced-stack-spill via `volatile` + sink)**: declare every live `XPtr<T>` as `volatile` for the duration of the safe-point window; the compiler must spill to a real stack slot before the safe-point and reload after. Cost: forces a load + store per safe-point regardless of register pressure, ~5-15 cycles per live XObject local. Works portably across MSVC + Clang + GCC. But the *offset* is still optimizer-chosen.
- **Option C (shadow stack of `XPtr*`)**: at function entry, allocate an array `XPtr<T>* _live[N]` on the stack and store pointers-to-XPtr-locals into the array; the FStackMapRecord describes the shadow-stack array's offset within the frame (which the optimizer DOES respect for explicit array layouts). At safe-point, the collector walks the shadow stack instead of the function's locals. Cost: extra register pressure (N+1 stack slots per function); ABI-stable layout the spec can control. This is the Unity IL2CPP model.

**Fix**: Rev 2 must pick a mechanism and specify it concretely. Recommended: Option C (shadow stack). Rewrite §6.1 to:
1. Specify that XIL2CPP-emitted functions allocate a `XPtr<XObject>* _liveRefs[N]` array on the stack at function entry, where N is the count of distinct XObject* locals declared in the function (Pass 3 enumerates).
2. Each live `XPtr<T>` local declaration in the function body is paired with an emit-time-stable index into `_liveRefs`; the array slot is stored at the local's declaration site and cleared at end-of-scope.
3. The FStackMapRecord's `liveRefOffsets[]` lists offsets within the `_liveRefs` array, not within the unpredictable frame.
4. Long-lived locals (those whose lifetime spans a safe-point) get stored once; short-lived locals get stored at their last-use boundary.
5. Document the per-function overhead: 1 stack slot per distinct XObject* local + 2-4 cycles per safe-point per live local (stack-store + stack-load).

If Rev 2 cannot commit to a mechanism, the precise-stack-scanning commitment in §1.3 of XCoreXObject must be relaxed to "conservative stack scanning" — but per Prime Directive and the locked Master Plan, that's a major architectural retreat. Better to ship the shadow-stack mechanism correctly.

Severity rationale: X10 acceptance gate ("precise stack scanning enumerates 100% of XObject locals") is not satisfiable by the current spec.

---

### FIX-A-CRIT-3 [severity: CRITICAL] — `ref` / `out` parameter write barrier not specified

**Location**: XIL2CPP.html §4.1 (no row for `ref`/`out` of reference type), §6.3 (write barrier emission rules).

**Issue**: Routine C# patterns:

```csharp
public bool TryGetActor(int id, out XActor actor) { actor = ...; return true; }
public void PromoteSlot(ref XPtr<XActor> slot) { slot = newActor; }
public void Swap(ref XActor a, ref XActor b) { var t = a; a = b; b = t; }
```

Rev 1's §6.3 enumeration rule walks `AssignmentExpressionSyntax` and checks the LHS's resolved FProperty kind. For `out actor = expr;` inside `TryGetActor`, the LHS resolves to a *parameter* (not a field), and the parameter's storage is in the caller's frame — possibly a field, possibly a stack local, possibly a TArray slot. **The callee cannot know the correct `parent_obj` argument for `XPACT_GC_STORE(parent_obj, &slot, value)`** because the callee was passed only the slot pointer; the parent is per-caller.

If XIL2CPP emits `*slot = value;` with no barrier (the most natural lowering of `ref`/`out`), every write through a `ref XActor` / `out XActor` parameter **silently misses the SATB pre-store barrier and the card-table mark.** This is a GC correctness bug.

If XIL2CPP emits `XPACT_GC_STORE(nullptr, slot, value);` (parent=nullptr), the SATB barrier still captures `oldValue`, but the card-table marker has no parent object to associate the card with — XCoreXObject's `XGC_WriteBarrier` (line 1081 of XCoreXObject.html) **does not document the parent_obj-nullable case**. The card-table mark may go to a wrong card.

**Fix**: Rev 2 must pick one:

- **Option A (cleanest; Prime-Directive-correct)**: BAN `ref XObject` / `out XObject` parameters in MVP. Emit diagnostic `XIL2CPP005 — ref/out parameter of XObject-reference type is not supported in MVP; return XActor or use the Result<T, E> pattern instead`. C# authors use `TryGetActor` returning `XActor?` (nullable). 95% of `out` uses become `?`-returning methods.

- **Option B**: introduce a wrapper-type protocol. `ref XActor` lowers to `XRef<XActor>` value-type `{XObject* parent, XPtr<XActor>* slot}`. The caller constructs the `XRef` at the call site with `parent = this` (or the resolved owning object); the callee writes via `xref.Store(newValue)` which calls `XPACT_GC_STORE(xref.parent, xref.slot, newValue)`. Requires runtime ABI for `XRef<T>` (8 bytes parent + 8 bytes slot = 16 bytes, NOT pointer-trivial — Win64 calling convention will spill via stack rather than registers), so the call-site cost is non-trivial.

- **Option C**: emit `XPACT_GC_STORE(nullptr, slot, value)` and amend `XCoreXObject` to document the parent=nullptr case: SATB barrier always fires; card-table mark falls back to a conservative "mark the card containing the slot pointer." Requires XCoreXObject Rev 5 patch.

**Recommendation**: Option A for MVP. Option B post-MVP. The audit will not commit on the user's behalf to Option C since it touches the locked XCoreXObject ABI.

Severity rationale: GC correctness — silent loss of references through `out` / `ref` parameters causes use-after-free.

---

### FIX-A-CRIT-4 [severity: CRITICAL] — `XPACT_FP_SEMANTICS_TAG` is not in the Contract manifest

**Location**: XIL2CPP.html §7.2 (lines 2062-2065).

**Issue**: §7.2 emits:

```cpp
static_assert(::XPactDetail::CompileTimeStrEq(XPACT_FP_SEMANTICS_TAG, "Precise"),
              "sim-path TU compiled with non-Precise FP semantics; cross-arch determinism broken");
```

But per `XToolchainContract.html` Rev 13.9 §10.2 line 1984:

> **`FPSemantics` enum** is not in the schema (XBT resolves FP semantics from `ModuleRules` at flag-derivation time rather than persisting it through the manifest; XHT / XIL2CPP do not consume it).

XIL2CPP cannot `static_assert` against `XPACT_FP_SEMANTICS_TAG` because no such macro exists in the Contract's ABI-tag surface. The XPact toolchain has 24 ABI layout tags as of Rev 13.9 (per Contract line 101); none of them is FPSemantics.

The constraint inventory's Q15 explicitly notes: "XBT resolves FP semantics from `ModuleRules` at flag-derivation time rather than persisting it through the manifest." The mechanism for ensuring sim-path TUs compile with `/fp:precise` is XBT's per-target flag derivation (Contract §4.2), NOT a runtime/static_assert pin.

**Fix**: Delete the `static_assert` against `XPACT_FP_SEMANTICS_TAG` in §7.2. Replace with a comment explaining that XBT's flag propagation enforces the sim-path FP semantics at the compile step itself (`/fp:precise` failure to apply would cause every sim-path TU to fail per Contract §4.4's banned-flag check). If Rev 1 wants a belt-and-braces compile-time guard, the correct mechanism is a `static_assert` against a *predefined compiler macro* that signals the FP mode (`__FAST_MATH__` for Clang/GCC; `_M_FP_PRECISE` is not defined by MSVC, but MSVC sets `_M_FP_FAST` and `_M_FP_STRICT` — sim-path expects neither). Example:

```cpp
#if defined(__FAST_MATH__) || defined(_M_FP_FAST)
#  error "sim-path TU compiled with fast-math; banned per Contract §4.2"
#endif
```

Severity rationale: invents a non-existent ABI tag; every sim-path .cs.cpp would fail to compile.

---

### FIX-A-CRIT-5 [severity: CRITICAL] — Write barrier missing for many reference-store sites

**Location**: XIL2CPP.html §6.3, four-step "detection rule" (lines 1956-1961).

**Issue**: §6.3 walks only `AssignmentExpressionSyntax`. The list of C# constructs that **write a reference field but are NOT `AssignmentExpressionSyntax`** is large:

1. **Object initializer**: `new Foo { Bar = actor }` lowers (per §5.4) to `temp = new(); temp.Bar = actor;`. The lowering happens at AST-normalization in Pass 2 — but §6.3 says the walk is at Pass 3, *after* normalization. Verify the walk sees the lowered form. (For XObject-derived `Foo`, this whole pattern is the XIL2CPP001 error; but for non-XObject `Foo` with reference fields, the barrier is still required.)
2. **Collection initializer**: `new TArray<XActor> { actor1, actor2 }` becomes `Add(actor1); Add(actor2);` — the `Add` is a call, not an assignment. §5.7's TArray emit DOES include `XPACT_GC_STORE(this, &m_data[m_count], item.Raw())` inside `Add`, so this case is actually correct, but §6.3 does not name it.
3. **Compound assignment**: `obj.RefField += newRef;` lowers to `obj.RefField = obj.RefField + newRef;` — but for reference types, `+=` typically does not compile (no operator+). `??=` does compile: `obj.RefField ??= other;` lowers to `obj.RefField = obj.RefField ?? other;`. Spec doesn't say whether this is one barrier (at the final assignment) or two (one per branch). §5.3 mentions `??=` as "Supported" but the emit example is absent.
4. **`with` expression on records**: `record1 with { RefProp = newRef }` per §2.2 lowers to "copy constructor invocation followed by property setter calls". The setter calls go through property-setter free functions; §5.4 says reference-typed property setters emit `XPACT_GC_STORE`. BUT records are usually value types; the `with` produces a *new* record value, so the barrier target is the new value's field — and the new value is being constructed, so the barrier is omitted under §6.3 rule (3) "New-object initialization". Verify the "new-object-init barrier omission" applies to `with` expression too. **Currently undocumented.**
5. **Auto-property `set` accessor**: §5.4 says reference-property setters emit `XPACT_GC_STORE`. But the AST walk in §6.3 looks at AssignmentExpressionSyntax with LHS resolving to FProperty — the setter body `_BackingField = value;` has the LHS as a *backing field*, NOT the auto-property itself. The semantic model knows the backing field is the auto-property's storage; verify §6.3's "resolved FProperty kind" check unwraps from backing field to property correctly.
6. **`init` accessor**: same as setter but restricted-visibility. Same barrier emission.
7. **Default interface property setter** (C# 12 supports default-implemented interface members with reference type): the setter body lives on the interface declaration; calls dispatch through interface vtable. The barrier emission must fire at the dispatched implementation, not at the interface call site. Currently undocumented.
8. **Tuple deconstruction with reference**: `(actor1, actor2) = SwapActors();` lowers to two assignments. If `SwapActors` returns `(XActor a, XActor b)` and `actor1`/`actor2` are fields on `this`, two barriers must fire.
9. **Indexer setter**: `dict[key] = actor;` calls the `[]` setter, which contains the `m_slots[bucket].value = actor;` write internally. The TMap emit in §5.7 should ensure the barrier fires here (it does, via TMap's GC-aware insert), but §6.3's enumeration rule (which only walks AssignmentExpressionSyntax) misses this case.
10. **`stackalloc T[n]` where T is XObject-derived**: §5.13 bans this case via `XIL2CPP081`, OK.
11. **`Span<T>` where T is XObject-derived**: §5.13 bans this via `XIL2CPP080`, OK.
12. **Primary constructor field init** (C# 12): `record Foo(XActor actor) { public XActor Actor { get; } = actor; }` initializes the property's backing field at construction. Inside a constructor (per §6.3 rule (1)), the barrier is omitted. But for a primary-constructor parameter that's also captured by a closure inside the body, the closure's XGCRootSpan needs the reference recorded as live. Multi-step semantics.
13. **Multi-step expression `a.b.c = d`**: §6.3 rule (3) says intermediate reads are bare loads, final assignment emits the barrier. But what if `a.b` returns `XPtr<T>` by value (a temporary)? Writing `temporary.c = d` writes to a temporary, which the GC will not see. §5.3's "obj.a.b.c.Field = x" example doesn't address whether intermediate reads must be re-rooted. Likely a non-issue for `XPtr<T>` since `XPtr<T>::operator->()` returns the raw `T*` and the chain is read-through (no temporary copy), but verify.

**Fix**: Rev 2 must rewrite §6.3 enumeration as a positive list of "every C# construct that writes a reference slot" rather than a negative "every AssignmentExpressionSyntax" rule. Reference: `XCoreXObject.html` §5.2 lists three barrier-emission paths but is incomplete. Recommended Rev 2 enumeration:

> A reference-store site is any AST node N such that N either is an AssignmentExpressionSyntax whose resolved LHS is an FObjectProperty/FWeakObjectProperty/FStructProperty-with-ref/FArrayProperty-with-ref/FMapProperty-with-ref/FSetProperty-with-ref, OR is one of the following lowered forms produced by Pass 2 normalization: object-initializer assignment, `with`-expression property mutation, compound-assignment lowering, `??=` lowering, deconstruction-assignment lowering, indexer-setter call lowering, auto-property setter backing-field write, init-accessor backing-field write, default-interface-property setter dispatch, primary-constructor parameter-to-field initialization (when the field is NOT consteval-initialized and the construction site is post-PostInitProperties).

Severity rationale: GC correctness — silent loss of references through any of the missed sites.

---

### FIX-A-CRIT-6 [severity: CRITICAL] — `XPACT_SAFEPOINT_CHECK` macro is invented; not in XCoreXObject contract

**Location**: XIL2CPP.html §6.5 (lines 1990-1998), §6.1 (line 1906), §3.1 (line 365).

**Issue**: §6.5 specifies the `XPACT_SAFEPOINT_CHECK()` macro shape:

```cpp
#define XPACT_SAFEPOINT_CHECK() \
    do { \
        if (XPACT_UNLIKELY(::XCore::Reflect::XGCSafepoint::s_pendingFlag.load(std::memory_order_relaxed))) { \
            ::XCore::Reflect::XGCSafepoint::HandlePending(); \
        } \
    } while (0)
```

But XCoreXObject Rev 4 §5.5 (lines 1296-1307) describes "every XIL2CPP-emitted function entry (the compiler inserts the check at function prologue; cost is ~3-5 cycles)" without defining the macro name, the namespace, or the function-pointer-table contract. The macro `XPACT_SAFEPOINT_CHECK` does not appear in XCoreXObject's locked ABI surface (it does not appear anywhere in XCoreXObject.html as a search confirms). Neither does `::XCore::Reflect::XGCSafepoint::s_pendingFlag` or `::XCore::Reflect::XGCSafepoint::HandlePending()`.

`std::memory_order_relaxed` for the pending-flag load: per XCoreXObject §5.5 the check is a "TLS-cached read + one branch" (~2 cycles). The XIL2CPP design uses an atomic global (`s_pendingFlag`) with relaxed load — but TLS is per-thread, not atomic. The two designs differ in:
- Performance: TLS load is ~1 cycle; atomic relaxed load on global is a real memory read, ~3-5 cycles depending on cache state.
- Cross-thread visibility: TLS doesn't need atomic semantics; global flag does need cross-thread visibility, but relaxed ordering may not be sufficient for x64+ARM64 cross-arch (ARM64 is weakly ordered; an x64 store to `s_pendingFlag` with relaxed ordering may not be visible to an ARM64 thread's relaxed load without acquire/release semantics).

**Fix**: Rev 2 must either:
- **(A) Cite the existing XCoreXObject macro/function** if one exists. (None does in Rev 4; see §10.7 of XCoreXObject which only lists barrier emit + stack-map + lifecycle bodies as XIL2CPP commitments; no safe-point macro contract.)
- **(B) Coordinate with XCoreXObject Rev 5** to add `XPACT_SAFEPOINT_CHECK` to the locked public surface, with explicit memory-order specification:
  - The pending flag should be `std::atomic<uint8_t>` (1-byte for tighter cache footprint).
  - Loads at every check site use `std::memory_order_acquire` (NOT `_relaxed`) so the safe-point handler's published state (e.g., the gray-queue snapshot pointer) is visible to the suspending thread.
  - The Contract should add `XPACT_SAFEPOINT_FLAG_ABI_TAG` if the flag's address is part of the cross-tool ABI envelope.
- **(C) Use the TLS-cached version** described in XCoreXObject §5.5 lines 1296+1307. This is `s_pendingFlag.load(std::memory_order_relaxed)` per-thread, updated by a fan-out broadcast when the GC requests safe-point. Per-thread TLS may not be at constant offset across MSVC vs Clang (TLS implementation differs); this would need an ABI envelope tag.

The XIL2CPP design must commit to one of these. Recommendation: (B) — explicit Contract addition with acquire-order semantics. The cross-arch concern (FIX-A-CRIT-7 below) demands acquire.

Severity rationale: every transpiled function will fail to compile against the current XCoreXObject because the macro does not exist. Either XIL2CPP creates a private fallback (which then drifts from the Contract) or XCoreXObject must publish it. Currently the spec is inconsistent.

---

### FIX-A-HIGH-1 [severity: HIGH] — Internal contradiction: who emits `FXObjectLifecycleTable` for C#-declared classes?

**Location**: XIL2CPP.html §5.1 (line 794 comment) vs §10.2 (line 2346) vs §10.4 Q14 resolution (line 2357).

**Issue**: Three different statements in the same document about who emits the `FXObjectLifecycleTable` for C#-declared XClasses:

- §5.1 line 794: "XHT emits the static FXObjectLifecycleTable for this XClass; XIL2CPP emits the bodies."
- §10.2 line 2346: "FXObjectLifecycleTable for C++-declared classes (XHT emits; **XIL2CPP emits the matching table for C#-declared classes**)."
- §10.4 Rev 1 resolution: "XHT emits the schema vector for BOTH C++-declared and C#-declared classes. XIL2CPP emits only the singleton-getter function body + **the FXObjectLifecycleTable slot bodies** + the ClassConstructorFn."

The constraint inventory line 747 ("FXObjectLifecycleTable for C++-declared classes (XHT emits). For C#-declared classes, XIL2CPP must emit the matching table per the cross-tool symbol contract.") aligns with §10.2 line 2346 — i.e., XIL2CPP emits the table *for C#-declared classes*.

But §5.1 line 794 says XHT emits the table.

§10.4 says XIL2CPP emits only the slot *bodies*, not the table itself.

The three positions are mutually inconsistent. Round 1 audit must lock to one.

**Fix**: Pick: **XIL2CPP emits the table for C#-declared classes** (matches §10.2 + the constraint inventory + the locked Q14 resolution split: XHT owns the schema-vector + FProperty descriptors; XIL2CPP owns the lifecycle table + the singleton getter + the ClassConstructorFn). Update §5.1's comment to read "XIL2CPP emits the static FXObjectLifecycleTable for this C#-declared XClass at the .cs.cpp side; XHT only emits the FClass + schema vector". Update §10.4 to add the lifecycle-table to XIL2CPP's emit list. Note: the FClass's `LifecycleTable` pointer must reference the XIL2CPP-emitted table; XHT emits the FClass with `LifecycleTable = &X<Type>_LifecycleTable` where `X<Type>_LifecycleTable` is the XIL2CPP-emitted constinit table — i.e., XHT *references* the symbol; XIL2CPP *defines* it. Add this protocol to the cross-tool symbol contract list in §1.4.

Severity rationale: cross-tool ABI ambiguity; build failure or duplicate definition at link time, depending on which tool's behavior the implementer follows.

---

### FIX-A-HIGH-2 [severity: HIGH] — Sim-path async ban is incomplete (`Task.FromResult`, `IAsyncEnumerable`, `await foreach`, `.Result`/`.Wait()`)

**Location**: XIL2CPP.html §7.5 (line 2115), §4.1 row "async / await" (line 624), §5.9 (lines 1525-1580).

**Issue**: Rev 1 says `async` / `await` are banned on sim-path TUs (diagnostic XIL2CPP044). The audit perspective requested checking adjacent surfaces:

- **`Task.FromResult(value)`**: returns a completed Task synchronously. No await needed at the call site, but the return type is `Task<T>`. Is it banned on sim-path? Currently no diagnostic listed. **Likely should be banned** (cross-arch determinism is fine for sync Tasks, but the surface invites async migration). Recommendation: ban via `XIL2CPP015 — Task<T> surface is banned on sim-path TUs`.
- **`IAsyncEnumerable<T>` / `await foreach`**: same surface concern as async/await; lowered to a state machine. Currently no diagnostic listed. **Should be banned** via XIL2CPP044 (same code; the lexical match is `await` keyword presence in `await foreach`).
- **`Task.Result` / `Task.Wait()` blocking get**: synchronous block on an async surface. On sim-path's SimPathSerialExecutor (single thread), this is a guaranteed deadlock if the awaited Task's continuation needs to resume on the same thread. Currently no diagnostic listed. **Should be banned** via XIL2CPP044 or a new code XIL2CPP048.
- **`ValueTask<T>`**: same as Task<T>. Currently no diagnostic listed. **Should be banned** parallel to Task<T>.
- **`Task.Delay(N)`**: §7.4 lists this as banned API (`System.Threading.Tasks.Task.Delay`) — covered, OK.
- **`Task.Run(action)`**: §7.4 lists this. But verify the diagnostic fires for `var t = Task.Run(...)` as well as `await Task.Run(...)` — both should be `XIL2CPP013` per the doc.

**Fix**: Rev 2 expands §4.1's async row to cover the full Task<T> / ValueTask<T> / IAsyncEnumerable<T> / await foreach surface as a single banned cluster. Add diagnostic XIL2CPP048 — "Task<T> / ValueTask<T> / IAsyncEnumerable<T> / await foreach is banned on sim-path TUs (same rationale as async/await: continuation-pinning would be required)". §7.5 enumerates the cluster explicitly.

---

### FIX-A-HIGH-3 [severity: HIGH] — Math.Sin/Cos/Sqrt cross-arch bit-exactness not specified for XIL2CPP-emitted code

**Location**: XIL2CPP.html §1.6 (last bullet, lines 116-117), §7.7 (lines 2125-2129), §5.3 (arithmetic operators row).

**Issue**: §1.6 commits to "cross-arch determinism (Win64 + Quest 3 ARM64 bit-exact) commitment: same C# source → same C++ emit → same runtime behavior on both architectures, including bit-exact floating-point semantics per Contract §4". §7.7 reiterates this commitment.

But Rev 1 does NOT specify what happens when C# source calls `Math.Sin(x)` / `Math.Cos(x)` / `Math.Tan(x)` / `Math.Sqrt(x)` / `Math.Pow(x, y)`:

- `Math.Sin(x)` in .NET 8 delegates to `Math.SinPi(x)` style implementations that ultimately call the platform's `sin()` libm. Different libm implementations differ in low bits. Contract §4.3 mandates Sleef-vendored transcendentals for sim-path C++ TUs (lines 853-859), but how does XIL2CPP emit C# `Math.Sin(x)` calls?
- Option 1: Emit `::sin(x)` (raw libm). On sim-path, the XBT-injected `XSimPathMathOverrides.h` redirects to `XSleef_sin`. Works for sim-path; but XIL2CPP must emit the bare `::sin` call, NOT a managed wrapper like `XCSharpMath::Sin(x)`. The mapping rule is NOT documented in §5.7's BCL surface table.
- Option 2: Emit a managed wrapper `XCSharpMath::Sin(x)` whose implementation calls `::sin` (and gets the sim-path override on sim-path TUs). Same effect, more emit work.
- Option 3 (wrong): Emit a manual sin-LUT implementation. Diverges from XPact's locked Sleef-vendored transcendentals.

`Math.Sqrt` is special: on x64 + ARM64, hardware `sqrtsd` and `fsqrt` both produce IEEE 754 correctly-rounded results, so cross-arch sqrt IS bit-exact without Sleef. Rev 1 should document this exception.

`float.ToString()` / `double.ToString()`: locale-dependent on .NET. The CultureInfo.InvariantCulture format is the only deterministic path. Currently undocumented.

`double.Parse(s)` / `int.Parse(s)`: same locale issue.

`Random.Next()`: §7.4 lists `System.Random` as banned, OK. But what about `XPact.Random` (the deterministic sim-path-compatible PRNG)? The spec doesn't say what's available; gameplay authors will need *some* deterministic PRNG.

**Fix**: Rev 2 §5.7 or §7 adds a "Sim-path determinism mapping" table:

| C# surface | Sim-path emit | Non-sim-path emit | Reference |
|---|---|---|---|
| `Math.Sin(x)` | `::sin(x)` (XBT redirects to `XSleef_sin`) | `::sin(x)` | Contract §4.3 |
| `Math.Cos(x)` | `::cos(x)` → `XSleef_cos` | `::cos(x)` | Contract §4.3 |
| `Math.Tan(x)` | `::tan(x)` → `XSleef_tan` | `::tan(x)` | Contract §4.3 |
| `Math.Sqrt(x)` | `::sqrt(x)` (hardware; bit-exact across arches per IEEE) | `::sqrt(x)` | IEEE 754 |
| `Math.Pow(x, y)` | `XSleef_pow(x, y)` | `::pow(x, y)` | Contract §4.3 |
| `Math.Exp(x)` | `XSleef_exp(x)` | `::exp(x)` | Contract §4.3 |
| `Math.Log(x)` | `XSleef_log(x)` | `::log(x)` | Contract §4.3 |
| `Math.Atan2(y, x)` | `XSleef_atan2(y, x)` | `::atan2(y, x)` | Contract §4.3 |
| `Math.Floor` / `Math.Ceiling` / `Math.Round` | hardware-implemented; banker-rounding mode locked per Contract | same | Contract §4.2 |
| `double.ToString()` (no format) | `XCSharpString::FromDouble(d, InvariantCulture, "R")` (always invariant culture, "R" round-trip format) | same | Contract §6.4 |
| `double.Parse(s)` | `XCSharpString::ParseDouble(s, InvariantCulture)` | same | Contract §6.4 |
| `int.ToString()` / `int.Parse(s)` | invariant culture | same | Contract §6.4 |
| `Random.*` (System.Random) | BANNED XIL2CPP049 | allowed | §7.4 |
| `System.Diagnostics.Stopwatch` | BANNED XIL2CPP040 | allowed | §7.4 |
| `XPact.Random` / `XPact.Sim.IDeterministicRng` | Allowed; seed locked via construction parameter | allowed | new addition needed |

Add diagnostic codes XIL2CPP049 (`Random.* banned; use XPact.Sim.IDeterministicRng`) and XIL2CPP053 (`Locale-dependent ToString/Parse call on sim-path; specify CultureInfo.InvariantCulture explicitly`).

Severity rationale: cross-arch determinism gate (X-DET) cannot pass without this. The current Rev 1 has a gap.

---

### FIX-A-HIGH-4 [severity: HIGH] — `HashSet<T>` / `Dictionary<K,V>` iteration order: warning not error on sim-path

**Location**: XIL2CPP.html §4.1 row "foreach" (line 634), §5.3 (line 1204), §7.4 (entry list).

**Issue**: Rev 1 says `foreach` over `HashSet<T>` emits XIL2CPP041 *warning* (non-deterministic iteration order). But sim-path's locked commitment (criterion (c)) is **bit-exact cross-arch determinism across 100 replays**. A warning is not sufficient — different MSVC vs Clang heap-bucket layouts WILL produce different iteration orders for the same HashSet contents. The same applies to `Dictionary<K, V>` enumeration (§4.1 lists no warning for Dictionary at all, despite the same issue).

The `[XFunction(SimPathHashSetIterOK = true)]` suppression attribute (mentioned in §5.3 line 1205) is also problematic: if an author marks a method as "iteration-order doesn't matter to me", how does XIL2CPP verify? The attribute is an assertion, not a proof.

Three correct strategies:

1. **Hard ban**: error XIL2CPP041 instead of warning on sim-path. Author must convert to `foreach (var kv in dict.GetEnumeratorSortedByKey())` or `dict.Keys.ToSortedArray()`.
2. **Force deterministic iteration**: ship a `TMap`/`TSet` partial specialization for sim-path TUs that always iterates in insertion order (Dictionary's actual .NET behavior in newer versions DOES preserve insertion order — but the underlying storage doesn't guarantee bit-exact insertion-order replay across rebuilds if the keys are non-deterministic hashes). Combined with FName XXH3-scalar hash (per XCore-4b §75), insertion-order iteration would be deterministic if and only if the insertion sequence is deterministic.
3. **Author opt-in to determinism**: keep warning, but force the suppression attribute to be replaced by an explicit `dict.IterateInOrder(SortBy.Key)` API. The compiler can't prove iteration-order independence; the author asserts it explicitly.

**Fix**: Rev 2 picks: **strategy (2) for sim-path TUs**. Convert XIL2CPP041 from warning to error in sim-path TUs. On non-sim-path TUs, keep as warning. The TMap/TSet sim-path partial specialization (currently undocumented) ships with insertion-order iteration. Add `TArray<K> TMap<K,V>::SortedKeys()` and `TArray<V> TMap<K,V>::SortedValues()` for the cases that need lexical-order iteration.

Add diagnostic XIL2CPP041 specifically for `Dictionary<K,V>` enumeration too (currently only listed for `HashSet<T>`).

---

### FIX-A-HIGH-5 [severity: HIGH] — Generic methods on XObject-derived types: FStackMapRecord location undefined

**Location**: XIL2CPP.html §5.8 (generic emit), §6.1 (FStackMapRecord emit).

**Issue**: §6.1 says "every transpiled C# function ... MUST emit a per-function FStackMapRecord in .rodata". §5.8 covers generic methods: each closed instantiation emits a per-instantiation FClass. But the audit perspective requested verification of **where the FStackMapRecord goes for a generic method on an XObject-derived type**:

```csharp
public class Inventory : XObject {
    public T GetSlot<T>(int index) where T : XObject { ... }  // generic method
}

// Called as:
//   var actor = inv.GetSlot<XActor>(0);
//   var item  = inv.GetSlot<XItem>(0);
// Each closed instantiation (GetSlot<XActor>, GetSlot<XItem>) emits a separate function body.
```

Each closed instantiation has its own function body with its own stack frame. Each instantiation needs its own FStackMapRecord. The current spec doesn't say:

- Whether the FStackMapRecord goes in the per-instantiation .cs.cpp (one of the consuming TUs) or in a per-method .cs.cpp.
- Whether the per-instantiation FStackMapRecord is deduplicated (two TUs in the same module both call `GetSlot<XActor>` — does each emit the record, or does one win?).
- How the stack-map registration at module load fires for a generic instantiation reached only by a downstream module.

C++ template specialization rules say two TUs both emitting the same template specialization will have the linker merge (with comdat semantics). But the FStackMapRecord at `.rodata`-registered-via-static-initialization-pattern would register *twice* (once per TU), leading to double-registration with the collector.

**Fix**: Rev 2 specifies:

1. Per closed-instantiation, the FStackMapRecord is emitted in the .cs.cpp file that declares the closed instantiation (the originating TU where the call site appears). Multiple call sites in different TUs of the same closed instantiation: only the **principal TU** (the one chosen as the symbol-defining TU per linker comdat rules — typically the lexicographically-first source file path) emits the record.
2. Pass 3's generic instantiation closed-walk records the principal TU for each closed instantiation; Pass 6 emits the FStackMapRecord at the principal TU only.
3. The registration call uses `__attribute__((selectany))` (Clang/GCC) / `__declspec(selectany)` (MSVC) so duplicate emit by accident is safe (the linker dedups).

Document the principal-TU rule in §5.8 and §6.1.

---

### FIX-A-HIGH-6 [severity: HIGH] — Static fields holding XObject references: rooting protocol unspecified

**Location**: XIL2CPP.html §4.1 (no row for static field), §6 (no mention of static field rooting), §5.4 (field emit).

**Issue**: C# allows static fields:

```csharp
public class XEngine : XObject {
    public static XGameInstance Singleton;  // static reference to XObject-derived
}
```

The static field `Singleton` holds an XObject reference but is not in any instance's reflected slot, not in a stack frame, not in a TArray. The GC must root this reference, otherwise the GameInstance will be collected even when accessible via `XEngine.Singleton`.

XCoreXObject Rev 4 §5.3 lists root sources: (a) FXObjectArray with `kRootPinnedBit`; (b) XGCRootSpan-registered containers; (c) precise stack-map; (d) XGCRoot::AddRoot/RemoveRoot manual roots. None of these covers "static field of an XPact runtime type".

Two correct strategies:

1. **Per-static-field XGCRootSpan**: XIL2CPP emits one XGCRootSpan per static field of XObject-derived type, registered in the static initializer block. Cost: ~32 bytes per static field. Works portably.
2. **Call XGCRoot::AddRoot at $cctor / module-init**: XIL2CPP-emitted $cctor for the declaring type calls `XGCRoot::AddRoot(Singleton.Raw())` after the static-field assignment. But `XGCRoot::AddRoot` is pinned-root (per XCoreXObject Rev 4 §5.3 line 1221: "set via `XGCRoot::AddRoot(XObject*)` from native code"). The static field must use *the AddRoot pattern* not the XGCRootSpan pattern (the static field doesn't have a stable slot pointer that survives reassignment).

Rev 1 §5.2 documents `$cctor` emit (lines 1099-1110) but says nothing about static-field XObject rooting.

Compounding question: thread-static fields (`[ThreadStatic] static XActor PerThreadActor`). On the SimPathSerialExecutor (single thread), thread-static degenerates to a single instance. On non-sim-path, each thread has its own copy. Each TLS-slot's reference must be a root.

**Fix**: Rev 2 §5.4 adds a "Static field with reference type" emit section:

- For a static field of XObject-derived type: emit the field as a `XPtr<T>` storage in the .cs.cpp anonymous namespace; emit a `static_init` block that registers the slot via XGCRoot::AddRoot at module load (and AddRoot of the new value on every write); the corresponding RemoveRoot/AddRoot pair on each write goes through `XPACT_GC_STORE` with a parent_obj of nullptr (per FIX-A-CRIT-3 resolution) — actually NO, the static field is not a card-marked slot; it's a manually-rooted slot. The correct emit:
  ```cpp
  // C# input: public static XGameInstance Singleton;
  namespace { XPtr<XGameInstance> XEngine_Singleton; }
  
  // Setter (per field write):
  void set_XEngine_Singleton(XGameInstance* newValue) {
      if (XEngine_Singleton.Raw()) XGCRoot::RemoveRoot(XEngine_Singleton.Raw());
      // SATB barrier (capture old value before reassignment):
      XCore::Reflect::XGC_WriteBarrier(reinterpret_cast<void**>(&XEngine_Singleton), newValue);
      XEngine_Singleton = XPtr<XGameInstance>(newValue);
      if (newValue) XGCRoot::AddRoot(newValue);
  }
  ```
- For thread-static fields: emit one root-register per thread that the gameplay-tier runtime creates. The SimPathSerialExecutor model means this is a single registration on sim-path. On non-sim-path, each thread's gameplay-task entry / exit pairs the Add/Remove. Recommendation: ban `[ThreadStatic]` on sim-path TUs (XIL2CPP054).

Add diagnostic XIL2CPP054 (`[ThreadStatic] is banned on sim-path TUs (single executor; semantics collapse)`).

Update §6 acceptance gates to cover static-field rooting.

Severity rationale: GC correctness; missed static-field roots are a guaranteed use-after-free.

---

### FIX-A-HIGH-7 [severity: HIGH] — Interface dispatch through XObject reference: write barrier semantics undefined

**Location**: XIL2CPP.html §5.1 (interface emit), §6.3 (barrier emit).

**Issue**:

```csharp
public interface IInventoryHolder { XActor CurrentActor { get; set; } }

void DoStuff(IInventoryHolder holder) {
    holder.CurrentActor = newActor;  // setter dispatch through interface vtable
}
```

The `holder.CurrentActor = newActor;` lowers to a call through the interface vtable. The actual setter implementation knows the concrete type (e.g., `MyHolder.CurrentActor`'s setter) and emits the barrier correctly per FIX-A-CRIT-5 resolution. But at the **call site** (`DoStuff`), Pass 3 cannot resolve the LHS to a concrete FProperty — it only knows the LHS is an interface property of reference type.

If §6.3's enumeration only walks AssignmentExpressionSyntax and resolves the LHS via the semantic model, what FProperty does the semantic model return for an interface property write? Roslyn returns an `IPropertySymbol` whose declaring type is the interface. The XPact FProperty taxonomy (XCore-4b's 28 subclasses) has FObjectProperty for an XObject reference field, but does it have an FInterfaceProperty for a property declared on an interface? **The audit cannot find FInterfaceProperty in XCore-4b's 28-subclass list.**

If the LHS is an interface property and the implementation's setter does the barrier internally, the call site doesn't need to emit a barrier — but then §6.3's enumeration rule must recognize "property setter dispatch through interface" and skip the call-site barrier. Currently it doesn't say.

If the LHS is an interface property and the call site is expected to barrier (because the dispatched implementation doesn't), the call site emits ... what? It doesn't know the concrete parent_obj for the card-table mark.

**Fix**: Rev 2 §6.3 specifies:

- **Interface property setter call**: NO call-site barrier. The dispatched concrete-class setter is responsible. The dispatched concrete-class setter emit (per §5.4 + FIX-A-CRIT-5) emits the barrier internally with the correct parent_obj.
- Rev 2 §5.4 must explicitly say: "Every C# property setter on a reference type emits `XPACT_GC_STORE(self, &_BackingField, value)` at the start of the setter body, regardless of whether the property is declared on a class or an interface (default implementation)."
- Default-implemented interface property setters (C# 8+): the default impl on the interface emits the barrier; the implementing class either inherits the default (no override emit) or overrides (its own emit). Both paths must barrier.

Severity rationale: silent loss of references through interface dispatch is the same kind of correctness bug as FIX-A-CRIT-3 / FIX-A-CRIT-5.

---

### FIX-A-HIGH-8 [severity: HIGH] — Boxing of value type holding XObject reference: undocumented

**Location**: XIL2CPP.html §5.6 (line 1347 mentions int boxing), §4.1 row "Pattern matching (positional...)", no general boxing row.

**Issue**: Several C# patterns box value types:

```csharp
// Tuple (record-like value type containing XObject):
(XActor actor, int count) tuple = (myActor, 5);
object boxed = tuple;  // boxed onto the heap; the boxed payload contains an XObject ref

// Anonymous types (always reference types in C#):
var anon = new { Actor = myActor, Count = 5 };
// anon is a reference-typed compiler-generated class; its Actor field is an XObject ref.

// Pattern match on object with positional pattern:
object o = (someActor, 42);
if (o is (XActor a, int n)) { ... }
```

§5.6 line 1347 acknowledges value-type boxing into `object` and bans it on sim-path: "XIL2CPP060 — pattern match on object with value-type pattern requires boxing; not supported on sim-path". But:

1. **The ban is only on the *pattern-match* surface**, not on the **assignment-to-object** surface. `object boxed = tuple;` (with tuple containing an XObject ref) emits no diagnostic and produces an Int32Box-style wrapper on the heap. Where is the wrapper allocated? Who roots the XObject reference inside the wrapper? §5.6 doesn't say.
2. **Anonymous types**: `var anon = new { Actor = myActor };` allocates a compiler-generated reference-typed class. On the heap. With an XObject reference field. Currently undocumented.
3. **Value tuples in cross-method calls**: `(XActor, int) GetActorAndCount()` returns a value tuple. The caller's local destination is a stack-resident value type containing an XObject ref. Per FIX-A-HIGH-12 (struct-with-XObject-field stack-resident handling), the stack-map must register the offset. But the **return mechanism** may pass the tuple via registers (SystemV ABI: first two `int`-sized fields in registers RAX/RDX; on Win64: first 16 bytes through return-value-via-stack); register-passed XObject refs are visible to the safe-point check only after the callee spills them. Currently undocumented.

**Fix**: Rev 2 adds:

- §4.1 "Boxing of value type holding XObject reference" row: status BANNED on sim-path (`XIL2CPP061`); allowed on non-sim-path with explicit Int32Box-style wrapper that registers an XGCRootSpan.
- §4.1 "Anonymous types" row: status BANNED on sim-path; allowed on non-sim-path as a compiler-generated class with XPACT_GC_STORE on every reference field setter.
- §5.7 adds value tuple handling: if the tuple contains an XObject reference field AND is stored in a register at return (per ABI), the receiving function spills to stack at function entry; FStackMapRecord registers the spill offset. Update §6.1 to cover this case.

---

### FIX-A-HIGH-9 [severity: HIGH] — `lock(obj)` non-sim-path emit leaks `FObjectMonitorRegistry` entries

**Location**: XIL2CPP.html §5.10 (lines 1611-1619).

**Issue**: §5.10 maps `lock(obj)` on non-sim-path to `FScopedCriticalSection(FObjectMonitorRegistry::GetOrCreate(someObject))`. The monitor "lifetime is the process; entries are not GC'd individually".

This is a **memory leak vector**: every distinct object that's ever been the target of a `lock(obj)` adds a 56-byte FCriticalSection + hash-table entry. For a game session of 10 hours with 10k locked objects, that's ~600 KB of permanent allocation. Worse, hot-reload's class replacement (cascade scenarios) creates new XObject instances; old instances' monitor entries persist in the registry pointing at dead XObjects.

**Fix**: Rev 2 §5.10 specifies: `FObjectMonitorRegistry` clean-up protocol via OnXObjectBeginDestroy hook. When an XObject's BeginDestroy lifecycle slot fires (XCoreXObject §2.4 EXObjectLifecycleSlot::BeginDestroy = 1), the runtime emits an OnBeginDestroy callback; FObjectMonitorRegistry subscribes to it and removes the entry. Cost: O(1) hash-remove per object destruction (typical).

Alternative: `lock(obj)` is banned on **all** XObject-derived locks even on non-sim-path; the only legal lock target is a dedicated non-XObject `FLockObject` instance. This is the safer Prime-Directive answer but breaks compatibility with C# locking patterns.

Recommendation: Option 1 (clean-up via OnBeginDestroy). Document the lifecycle hook + the rule.

Also note: O-LOCK-DEADLOCK in §14 (Rev 1's audit table) flags this exact issue with priority LOW; this audit upgrades to HIGH because the leak compounds with hot-reload and accumulates per-session.

---

### FIX-A-HIGH-10 [severity: HIGH] — Memory ordering for cross-thread XObject reference visibility: not specified

**Location**: XIL2CPP.html §6.3 (XPACT_GC_STORE definition), §6.5 (safe-point check), §5 (general).

**Issue**: x64 has Total Store Order (TSO): a store followed by a load to a different address is not reordered; all stores are visible to other threads in program order. ARM64 has weaker ordering: stores and loads can be reordered, and cross-thread visibility requires explicit memory barriers (DMB).

XPACT_GC_STORE (per §6.3 line 1170):
```cpp
#define XPACT_GC_STORE(parent_obj, slot_ptr, new_value) \
    do { \
        ::XCore::Reflect::XGC_WriteBarrier(reinterpret_cast<void**>(slot_ptr), \
                                          static_cast<void*>(new_value)); \
        *(slot_ptr) = (new_value); \
    } while (0)
```

The store `*(slot_ptr) = (new_value);` is a plain C++ store, NOT atomic. On x64 it's visible to other threads in program order (TSO). On ARM64 it can be reordered — a GC marker thread reading the slot may see the OLD value while the write-barrier has already published the new card. This is the classic SATB ordering bug.

XCoreXObject Rev 4 §5.2 (lines 1080-1090) implements XGC_WriteBarrier:
```cpp
extern "C" void XGC_WriteBarrier(void** Slot, void* NewValue) noexcept {
    // 1. SATB pre-store barrier (only if marking is active; checks TLS flag).
    if (s_isMarkingActive.load(std::memory_order_relaxed)) {
        if (auto* oldValue = *Slot) {
            g_SATBQueue.PushTLS(static_cast<XObject*>(oldValue));
        }
    }
    // ...
}
```

The SATB queue push happens BEFORE the slot is written. On x64 TSO this works. On ARM64 weakly-ordered, the SATB push (which is a TLS write) and the slot store can be reordered by the CPU — the marker thread can see the new value in the slot before the old value lands in the SATB queue, defeating the snapshot.

Per the SATB literature, the correct ordering requires:
- Pre-store barrier: `Slot.load(memory_order_acquire) // old value`; `SATB_push(oldValue)`; `release fence`; `Slot.store(newValue, memory_order_relaxed)`. The release fence ensures the SATB push is visible before the new slot publication.

Rev 1 XIL2CPP doesn't specify memory ordering at all. It's expected to inherit XCoreXObject's contract — but XCoreXObject Rev 4 §5.2 uses `memory_order_relaxed` for the marking-flag load. The slot store is plain (not atomic). This is broken on ARM64.

**Fix**: Rev 2 must coordinate with XCoreXObject Rev 5 to:

1. Define `XPACT_GC_STORE` as expanding to a sequence that on ARM64 emits the necessary DMB ISHST or equivalent acquire/release fence.
2. The `s_isMarkingActive.load` is `memory_order_acquire` (NOT relaxed) so the consequent SATB push happens-after a publishing release elsewhere in the marker.
3. The slot store is plain (relaxed), but a release-fence follows the SATB push.

XIL2CPP-side commitment: the macro definition shipped by XIL2CPP-emitted .cs.cpp must match the XCoreXObject runtime contract exactly. Add `XPACT_GC_STORE_MEMORY_ORDER_TAG` ABI tag to lock the memory-order discipline.

Cross-arch determinism implication: ARM64 weakly-ordered reorderings can produce slot writes that x64 wouldn't — and at GC mark time, this is observable as different live-reference sets on ARM64 vs x64 (the X-DET gate would catch this in worst case, but only if the test scene exercises the race).

Severity rationale: ARM64 GC correctness; potential silent reference loss; criterion (c) cross-arch determinism may not hold.

---

### FIX-A-HIGH-11 [severity: HIGH] — `Interlocked.*` operations on sim-path: not specified

**Location**: XIL2CPP.html §7.4 (banned API list), §5 (general).

**Issue**: `System.Threading.Interlocked.CompareExchange`, `Interlocked.Increment`, `Interlocked.Add` are not in §7.4's banned list. They map to C++ atomic operations (`std::atomic<T>::compare_exchange_strong` etc.).

Three concerns:

1. **Cross-arch memory ordering**: same as FIX-A-HIGH-10. Interlocked's default ordering in .NET is `MemoryOrder.SequentialConsistent`; C++ default is also `memory_order_seq_cst`. Both arches honor this. OK.
2. **Sim-path determinism**: Interlocked.Increment on a shared counter from multiple threads has thread-interleaving-dependent results. On the SimPathSerialExecutor (single thread), Interlocked degenerates to plain increment. But the C++ emit cost is non-trivial (full memory barrier on every Interlocked call). Could XIL2CPP emit plain ops on sim-path TUs?
3. **API surface**: should `Interlocked` be allowed at all on sim-path? It's the standard C# atomic-shared-state surface. If banned, the alternative is FCriticalSection (which is also banned on sim-path per §5.10). Sim-path code has NO atomic-state surface left. This may be intentional (sim-path is single-threaded; no shared mutable state needed) — but Rev 1 should say so.

**Fix**: Rev 2 §7.4 + §5 adds:

- Sim-path: `Interlocked.*` BANNED with diagnostic `XIL2CPP055 — Interlocked.* is banned on sim-path TUs (single executor; no atomic surface needed)`. Author replaces with plain assignment.
- Non-sim-path: `Interlocked.CompareExchange` maps to `std::atomic_compare_exchange_strong`; `Interlocked.Increment` maps to `std::atomic_fetch_add(1)`; same ordering as .NET (seq_cst).
- Document: XCore-4a's `atomic` family (referenced in §1.4) is the canonical atomic surface for native C++ TUs. C# `Interlocked` calls bind to that.

---

### FIX-A-HIGH-12 [severity: HIGH] — Stack-map recursion into struct-on-struct-on-stack-frame

**Location**: XIL2CPP.html §6.7 (struct-with-XObject-field handling), §6.1 (FStackMapRecord).

**Issue**: §6.7 covers struct-with-XObject-field cases:
- Stack-stored: stack-map registers the XObject field's byte offset.
- Class-field: schema vector recurses via `EXObjectRefSchemaOp::Struct`.
- Array-stored: TArray<TStruct> with XGCRootSpan partial spec recurses.

The audit perspective asked specifically about: **struct containing struct containing XObject reference, stack-resident**:

```csharp
struct InnerSlot { public XActor Holder; }
struct OuterContainer { public InnerSlot inner; public int meta; }

void DoWork() {
    OuterContainer container = ...;
    // container.inner.Holder is a stack-resident XObject ref nested two structs deep
}
```

§6.7 only addresses one-level struct nesting ("stack-stored" struct with XObject field). For multi-level nesting, the audit perspective asks: "Recursive emit?"

The current spec does NOT explicitly say. Implementation must:
1. Pass 3 walks every stack-resident local's type recursively.
2. For each transitive XObject reference (any depth in the type tree), compute the byte-offset within the stack frame (frame_offset + inner_offset + inner_inner_offset + ... + xobject_field_offset).
3. Emit one liveRefOffsets entry per transitive XObject reference.

Same applies for `Span<TStruct>` on the stack, `stackalloc TStruct[n]` with struct-with-XObject-field elements (already banned per XIL2CPP081 — OK), and recursive arrays (`TStruct[]` where TStruct contains XObject — same as class field case, schema vector handles).

What about a stack-resident `TArray<TStruct>`? TArray is a class (heap-storage of the array body); the TArray instance itself on the stack is a 32-byte structure (m_data ptr + m_count + m_capacity + m_rootSpan). The XGCRootSpan in m_rootSpan registers as the array's roots; the stack-map for the TArray's enclosing function does NOT need to walk the array contents (they live on the heap, registered by m_rootSpan). But the stack-resident TArray instance itself has a m_rootSpan field that must be REGISTERED — this happens in the TArray constructor (§5.7), so the safe-point check at function entry must run AFTER the TArray constructor (or m_rootSpan registration is undefined). Verify safe-point placement at constructor.

**Fix**: Rev 2 §6.7 adds an explicit recursive-walk rule:

> For stack-resident value-type locals (struct / record struct / tuple), Pass 3 performs a recursive walk of the type tree. For each transitive XObject reference field (at any nesting depth), compute the byte offset within the stack frame as the sum of (a) the local's frame offset, (b) each enclosing struct's offset within its parent struct, (c) the XObject field's offset within its directly-enclosing struct. Each transitive XObject reference contributes one entry to liveRefOffsets[].

Note: with the shadow-stack mechanism from FIX-A-CRIT-2 resolution, the recursive walk simplifies: each transitive XObject reference gets its own shadow-stack slot, not a frame-offset.

Document the safe-point placement rule: function-entry safe-point fires AFTER any container/RAII-managed-root constructors complete and BEFORE the function body's first user statement. Order: shadow-stack init → container constructors (XGCRootSpan registers) → safe-point check → user body.

---

### FIX-A-HIGH-13 [severity: HIGH] — `XPtr<T>` inside `Span<T>` on stack: not addressed

**Location**: XIL2CPP.html §5.13 (Span emit), §6.1 (FStackMapRecord).

**Issue**: §5.13 line 1738 says `Span<XObject*>` is BANNED in MVP (XIL2CPP080). OK. But:

- `Span<TStruct>` where TStruct contains XObject is NOT banned. The Span points into a TArray's underlying buffer or into stackalloc'd memory (which is also banned for XObject elements). The TArray case is OK because m_rootSpan registers the array body. The stackalloc case is banned per XIL2CPP081. But: what about a Span into a class's array field (`obj.actorArray.AsSpan()`)? The Span points into the underlying buffer which is on the heap, registered by obj.actorArray's m_rootSpan. **OK**.
- `Span<TArray<XActor>>` — a span of arrays-of-XActor. Each TArray instance in the span has its own m_rootSpan. **OK** because each TArray manages its own root span.
- The Span value-type pair `{T*, size_t}` is itself on the stack. If T transitively contains XObject*, the Pass 3 walk should NOT walk into T's contents from the Span's perspective; the Span is just a view. But the Span's m_data pointer points to an array body; if that array body is NOT a TArray (e.g., `int[]` cast to Span), there's no m_rootSpan. **Edge case to verify**.

Also: `ReadOnlySpan<XObject*>` (read-only view) — the same ban (XIL2CPP080)? Or allowed because no writes? Current spec doesn't distinguish; recommend banning both equally.

**Fix**: Rev 2 §5.13 extends the Span ban to read-only views (`ReadOnlySpan<XObject*>` and `ReadOnlySpan<TStruct>` where TStruct transitively contains XObject*) and the Span-of-Span case (`Span<Span<T>>`). Update XIL2CPP080's description.

Document that the Span value type itself, when stored on the stack, contributes zero entries to the stack-map (the Span is a view; the rooted body is elsewhere).

---

### FIX-A-HIGH-14 [severity: HIGH] — `IAsyncEnumerable<T>` / `await foreach` not in §4.1 row matrix

**Location**: XIL2CPP.html §4.1, §5.9 (state machine emit).

**Issue**: §4.1's table has rows for `async / await` (BANNED sim-path), `yield return iterators` (supported with state machine + XGCRootSpan). It does NOT have a row for `IAsyncEnumerable<T>` (the async-iterator surface introduced in C# 8) or `await foreach`.

`IAsyncEnumerable<T>` lowers to a state machine that combines async (await) + iterator (yield return). On sim-path, this should inherit both bans (async-banned + sim-path-iterator-semantics). On non-sim-path, the state machine has both async XGCRootSpan-over-captures and iterator yielded-value-XGCRootSpan concerns.

**Fix**: Rev 2 §4.1 adds:
- `IAsyncEnumerable<T>` declaration: BANNED on sim-path (`XIL2CPP044` extension); allowed on non-sim-path with state machine.
- `await foreach (var x in src)`: BANNED on sim-path; allowed on non-sim-path.

§5.9 adds an explicit subsection for IAsyncEnumerable<T> + await foreach emit (state machine combining the two patterns).

---

### FIX-A-MED-1 [severity: MEDIUM] — `[XObjectInternalConstructor]` attribute name conflicts with C++ identifier rules

**Location**: XIL2CPP.html §2.3 (line 278-279).

**Issue**: §2.3 introduces `[XObjectInternalConstructor]` attribute to suppress XIL2CPP001 on the `XObject.New<T>` factory. The attribute name uses spaces internally? No, looks fine. But: the attribute is C#-only (Roslyn attribute); does it have a C++-side equivalent that XHT emits? For interop, C++ code authored by Simgenics that needs to call the C++ NewObject directly (without going through XObject.New<T>) needs no attribute. The C# diagnostic only fires on C# `new Foo()`.

**Fix**: Document the attribute namespace explicitly. Suggested: `XPact.CoreXObject.XObjectInternalConstructorAttribute`. The corresponding XPact.CSharp.BCL reference DLL ships this attribute. Update §2.3 to specify the namespace.

---

### FIX-A-MED-2 [severity: MEDIUM] — `FName("Pickup_1")` runtime interning in transpiled output is non-deterministic

**Location**: XIL2CPP.html §5.3 (lines 1235, 1240), §5.5 (string interning).

**Issue**: §5.3 emit example uses `::FName("Pickup_1")` to construct an FName at runtime from a string literal. FName interning per XCore-4b §299 is XXH3-scalar-hash-based; the hash is bit-exact, but the FName's `Index` (the slot in the global name pool) depends on insertion order. If `Pickup_1` is the first time the name is encountered at runtime, it gets Index N; if a different module's static init also names `Pickup_1` first, it gets Index N' ≠ N.

For sim-path determinism: cross-arch replay requires the FName's Index to be identical bit-for-bit. Two arches that load DLLs in different orders will assign different Indices. **The X-DET gate requires FName Index to be cross-arch-consistent.**

Two strategies:

1. **Compile-time FName interning**: instead of runtime `FName("Pickup_1")`, emit `_FName_Pickup_1` where `_FName_Pickup_1` is a constexpr 8-byte handle whose Index is computed at compile time (via FName XXH3 hash, deterministic). The hash maps to a fixed slot in a per-module section table; the runtime loader resolves to the global pool. Requires XCore-4b extension.
2. **First-use canonicalization**: the runtime guarantees FName Index assignment is order-independent. Currently it isn't.

Neither is documented in Rev 1.

**Fix**: Rev 2 §5.5 specifies: C# string literals used to construct FName at compile time (literal arguments to `XObject.New<T>(outer, name: "Pickup_1", ...)`) emit at compile time as `_FName_Pickup_1` constexpr (compile-time-deterministic FName). Runtime-constructed FNames (from `FName(someString)` where someString is a runtime value) emit `::FName(someString)` and are subject to FName interning's ordering rules.

Add diagnostic `XIL2CPP049` — "Sim-path code constructs FName from a non-literal string; this is non-deterministic across arches due to interning ordering. Use a compile-time string literal."

Cross-reference: XCore-4b §299's two-hash split addresses byte-hash for lookup vs handle-hash for TMap-keying, but does not pin Index across arches.

---

### FIX-A-MED-3 [severity: MEDIUM] — `using` block exception path emits Dispose() twice

**Location**: XIL2CPP.html §5.15 (lines 1759-1771).

**Issue**: §5.15 lowers `using (var x = new D()) { ... }` to:

```cpp
{
    auto x = ::Disposable{};
    try {
        ...
    }
    catch (...) {
        x.Dispose();
        throw;
    }
    x.Dispose();
}
```

If the `try` body throws, `x.Dispose()` is called in the catch, then the throw re-propagates. If `x.Dispose()` itself throws, the catch handler completes anyway. **But after the catch block re-throws, control does NOT flow to the post-try `x.Dispose()` call** — so no double-call. OK so far.

But if the `try` body completes normally:
1. The post-try `x.Dispose()` runs.
2. RAII destruction of `x` itself (`~Disposable()`) runs at end of scope.

If `Disposable` has both a `Dispose()` method AND a non-trivial destructor, both run — the .NET semantic is *exactly* `Dispose()` once, and the C++ scope-end destructor is XPact's separate concern. **Verify**: for XObject-derived `using` target, `Dispose()` maps to `MarkForKill()` (per §5.15 line 1774). The destructor is suppressed (XObject is GC-managed; no scope-end destructor). For non-XObject `using` target, the destructor IS invoked at scope end on top of `Dispose()`. Two cleanup paths.

This isn't necessarily wrong (the destructor is for C++ RAII cleanup of internal state; Dispose is for explicit user cleanup). But the spec doesn't say.

**Fix**: Rev 2 §5.15 clarifies:

- For XObject-derived `using` targets: `Dispose()` lowers to `MarkForKill()`; destructor is NOT called at scope-end (XObject is GC-managed). The `using` block adds a single `MarkForKill` call at scope-exit (try/finally).
- For non-XObject `using` targets: `Dispose()` is a member function call; destructor is the C++ scope-end destructor. Both run. Document explicitly: "`Dispose()` runs first, then `~T()` at scope end. If `T::Dispose()` releases resources that the destructor would also release, the class author must ensure idempotence."

---

### FIX-A-MED-4 [severity: MEDIUM] — XGCRootSpan in `_Lambda_onClick` not destroyed before object move

**Location**: XIL2CPP.html §5.9 (line 1492-1521, lambda emit).

**Issue**: §5.9 emits the lambda closure with `_rootSpan = XGCRootSpan{...}; XGC_RegisterRootSpan(&_rootSpan);` in the constructor and `XGC_UnregisterRootSpan(&_rootSpan);` in the destructor. Copy is deleted; move is described as "transfers ownership atomically per Contract §3.4".

Per Contract §3.4 (lines 665-699), move-of-XGCRootSpan-containing-container: the move constructor must call `Unregister(&source)` and `Register(&dest)` atomically with respect to the mark phase. The lambda emit code doesn't show the move constructor body; line 1513 says "Move ops transfer ownership atomically per Contract §3.4" but no code.

If the lambda is constructed-and-moved (e.g., returned from a factory function):
```cpp
_Lambda_onClick MakeLambda(XActor* a) {
    return _Lambda_onClick{XPtr<XActor>(a)};
}
```
The `_Lambda_onClick` is constructed on the stack, registers its `_rootSpan`, then moves into the return value. The source's `_rootSpan` is at address `&temp._rootSpan`; the destination's is at `&result._rootSpan`. The mark phase, if it captured `&temp._rootSpan` before the move, will read from a destroyed slot after the move. The move constructor must update.

**Fix**: Rev 2 §5.9 spells out the move constructor body for the lambda closure (matching Contract §3.4's container move pattern):

```cpp
_Lambda_onClick(_Lambda_onClick&& other) noexcept
  : _captured_owner(std::move(other._captured_owner))
{
    // Atomic-with-mark-phase: unregister source, register dest
    ::XCore::Reflect::XGC_UnregisterRootSpan(&other._rootSpan);
    _rootSpan = ::XCore::Reflect::XGCRootSpan{
        reinterpret_cast<void**>(&_captured_owner),
        sizeof(::XCore::Reflect::XPtr<::XActor>),
        1,
        ::XCore::Reflect::XGCRootKind::Strong,
        0
    };
    ::XCore::Reflect::XGC_RegisterRootSpan(&_rootSpan);
}
```

Reference Contract §3.4 for the "atomic-with-mark-phase" lock; XCoreXObject's container code already has the snapshot mechanism. Verify the lambda closure pattern matches.

---

### FIX-A-MED-5 [severity: MEDIUM] — Async state machine on heap allocation site missing

**Location**: XIL2CPP.html §5.9 (lines 1525-1580, async state machine emit).

**Issue**: §5.9 emits `_AsyncSM_LoadAsset` as a struct. Where is it allocated? An async method's state machine in .NET is heap-allocated by the awaiter machinery. In transpiled C++, the state machine struct lives where?

- Option A: Stack-allocated in the caller's frame. Lifetime is caller's frame. But the state machine outlives the caller's frame (it persists across await suspensions). Stack-allocated state machine cannot survive caller return. **Wrong.**
- Option B: Heap-allocated via `new _AsyncSM_LoadAsset(path)`. The state machine's lifetime is the awaiter's. The awaiter's promise owns the state machine; when the promise completes, the state machine is freed. **Right pattern, missing from spec.**
- Option C: Allocated from a pooled allocator (per-thread pool of async state machines for size-class N). Performance optimization; orthogonal.

Rev 1 §5.9 doesn't say. The emit code shows `_AsyncSM_LoadAsset sm(path);` is constructed but the construction site / allocation strategy isn't specified.

**Fix**: Rev 2 §5.9 specifies: async state machines for non-sim-path methods are heap-allocated via `::FMemory::NewObject<_AsyncSM_*>` (per-tag attribution `EMemTag::AsyncStateMachine` per XCore-4a §x); the awaiter's `XCore::Promise::Promise<T>` owns the state machine and frees on completion. The state machine's XGCRootSpan stays registered as long as the heap allocation is live.

---

### FIX-A-MED-6 [severity: MEDIUM] — `XGCRootSpan` re-registration on TArray resize uses raw pointer arithmetic

**Location**: XIL2CPP.html §5.7 (TArray emit, lines 1397-1414).

**Issue**: §5.7's TArray::Add does:
```cpp
XPACT_GC_STORE(this, &m_data[m_count], item.Raw());  // store w/ barrier
m_count++;
XGC_UpdateRootSpan(&m_rootSpan, reinterpret_cast<void**>(m_data), m_count);
```

And GrowAndUpdateRootSpan calls `::FMemory::Realloc(m_data, ...)`.

Problem 1: `XPACT_GC_STORE(this, &m_data[m_count], item.Raw())` — the `this` is the TArray instance, not the parent XObject that owns the TArray. The card-table-mark fires against the TArray's address, not the owning XObject. If the TArray is owned by `someActor->inventory`, the card mark should be on `someActor`'s address (so the next minor collection scans `someActor`'s card). The current emit marks the TArray's own card.

Problem 2: `FMemory::Realloc` is non-XObject-aware; it doesn't know about pending writes. If a GC marker is concurrently reading `m_data[k]` while Realloc moves the buffer, undefined behavior. (XCoreXObject's mark phase is mostly-concurrent per §4.3; this race exists.)

Problem 3: `XGC_UpdateRootSpan` after the Realloc but before the m_count update — the span's count is briefly stale (mark phase sees the old count, reads beyond the new buffer's valid region). Race window.

**Fix**: Rev 2 coordinates with XCoreXObject Rev 5:

1. **Container parent inheritance**: TArray's barrier emit should use the **parent XObject** as the card-mark target. The parent is the XObject that holds the TArray member; pass it to the TArray constructor (`TArray(XObject* parent)`) and store as `m_parent`. The barrier becomes `XPACT_GC_STORE(m_parent, &m_data[k], val)`. Cost: 8 bytes per TArray. Necessary for correct generational scan-reduction.
2. **Realloc safety**: `FMemory::Realloc` MUST NOT move buffers that contain XObject* slots during GC mark. Either: (a) ban resize during mark (the mutator blocks at safe-point until mark completes); (b) use a copy-then-snapshot-update protocol where the old buffer remains valid for marker reads until the marker confirms the new buffer is published.

Document the container parent pattern in §5.7 + §6.2. This is a moderately deep change but the audit perspective flagged it as a known XGCRootSpan-vs-card-table gap.

---

### FIX-A-MED-7 [severity: MEDIUM] — Sim-path FP flags table inconsistent with Contract Rev 13.9 / XBT Rev 10

**Location**: XIL2CPP.html §7.2 (lines 2052-2058).

**Issue**: §7.2 lists sim-path flags:
- MSVC: `/fp:precise`; banned `/fp:fast`, `/fp:except`.

Contract Rev 13.9 §4.2 line 816-818 says the same plus the SimdLevel ceiling (`SSE42`). XIL2CPP §7.2 mentions SIMD constrained to ≤ SSE42 in the last bullet. OK.

- Clang: `-ffp-contract=off`, `-fno-fast-math`, `-fno-finite-math-only`, `-mno-fma`.

Contract Rev 13.9 §4.2 line 822-823 says the same. OK.

But Contract Rev 13.9 §4.2 line 824 (the Clang Linux row) explicitly mandates `-mno-fma` "regardless of SimdLevel" — XIL2CPP §7.2 only lists it as a sim-path flag. Inconsistency.

Also: XIL2CPP §7.2 doesn't mention `/fp:strict` (the IEEE-754-strict MSVC mode). Per Contract Rev 13.9, sim-path uses `/fp:precise` (NOT strict). Strict adds exception-handling on denormal operations which is slow. Precise is the correct choice. Document why.

The audit perspective specifically asked about `/fp:strict` — the answer is **NO, Rev 1 uses `/fp:precise`**, which is the Contract-locked choice. But the spec doesn't say why.

**Fix**: §7.2 adds a brief rationale: "MSVC's `/fp:strict` enables IEEE 754 exception handling on denormal operations which is unnecessary slow on sim-path. `/fp:precise` is sufficient: it disables `fma` contractions (banned per Contract §4.2) and disables `fast-math` rewrites (banned per Contract §4.2) while keeping IEEE-754 correctness for non-exception operations." Cross-reference Contract §4.2.

---

### FIX-A-MED-8 [severity: MEDIUM] — `unchecked` signed overflow on Win64 MSVC: provisional flag is wrong

**Location**: XIL2CPP.html §5.16 (lines 1798-1802), §14 (O-SIMPATH-SIGNED-OVERFLOW).

**Issue**: §5.16 says: "Sim-path TUs MUST compile with `-fwrapv` for Clang/GCC and the MSVC equivalent (`/d2overflow-` or equivalent flag; under research, may require the `integer-overflow` wrapper)."

`/d2overflow-` is an MSVC internal flag (the `d2` prefix is non-public). It doesn't have a documented effect. Searching the public web confirms it's not what you'd use.

The correct MSVC behavior for signed integer overflow is *defined as wrap by MSVC itself* — MSVC has historically wrapped signed overflow (unlike Clang/GCC which treat it as UB and may optimize aggressively). MSVC explicitly does NOT introduce UB for signed overflow as documented in [MSVC ABI / VS 2019+ release notes]. So no flag is needed on MSVC; Clang/GCC need `-fwrapv`.

But this is **not guaranteed to remain true** in future MSVC versions; the Itanium ABI commitment is that C++20 signed overflow is UB. MSVC may eventually optimize signed overflow as UB and break sim-path cross-arch determinism.

**Fix**: Rev 2 §5.16 specifies:

- Clang / GCC: sim-path TUs add `-fwrapv` mandatorily.
- MSVC: no flag needed for current versions (MSVC empirically wraps signed overflow); however, sim-path TUs MUST also include explicit `__builtin_add_overflow`-equivalent overflow detection on every signed-int arithmetic operation under a future-proofing pattern. Alternative: emit explicit modular arithmetic via casts to `uint32_t`, perform the op, cast back. Cost: ~1 extra cycle per op.

Recommend Rev 2 commit to explicit modular wraps via uint casts (the future-proof + cross-compiler answer; cost is minimal on sim-path which already pays for `/fp:precise`).

Update §14's O-SIMPATH-SIGNED-OVERFLOW to MEDIUM resolved.

---

### FIX-A-MED-9 [severity: MEDIUM] — Pattern matching on `object` boxes value types: emit-site allocator unclear

**Location**: XIL2CPP.html §5.6 (lines 1318-1346).

**Issue**: §5.6 line 1347 says: "XIL2CPP MVP rejects polymorphic boxing of value types via `object`; per Locked Commitment 1 there's no managed boxing. Sim-path code that switches on `object` with value-type patterns is a build-time error `XIL2CPP060`."

But §5.6 line 1333-1340 emits code that uses `Cast<Int32Box>` (boxing wrapper):
```cpp
if (auto* p = ::XCore::Reflect::Cast<::Int32Box>(o)) {
    int32_t n = p->Value;
    ...
}
```

Where does `Int32Box` come from? It's a heap-allocated wrapper. **Who allocates it?** When C# code says `object o = 5;`, the runtime needs to box. Per Locked Commitment 1 there's no managed boxing — so where does Int32Box come from?

Two interpretations:

1. **`Int32Box` is explicit on the C# side**: the author writes `object o = new Int32Box(5);` rather than `object o = 5;`. This is non-idiomatic C# but works.
2. **XIL2CPP emits a boxing call automatically**: `object o = 5;` lowers to `object o = ::FMemory::NewObject<Int32Box>(5);`. Heap allocation. The Int32Box is GC-rooted (it's an XObject? or pinned via XGCRoot::AddRoot?).

The spec is ambiguous. Sim-path forbids the boxing per XIL2CPP060 — but non-sim-path needs SOME mechanism.

**Fix**: Rev 2 §5.6 clarifies: non-sim-path `object o = 5;` is also BANNED in MVP. Add diagnostic `XIL2CPP062` — "Implicit boxing of value type to object is not supported in MVP. Use a typed discriminant union (record) or explicit Int32Box wrapper." This pushes implicit boxing out of MVP scope on all paths; post-MVP can add the wrapper allocator with a documented heap-allocation pattern.

---

### FIX-A-MED-10 [severity: MEDIUM] — `nameof(SomeMethod)` interning vs runtime-resolved name

**Location**: XIL2CPP.html §5.5 (line 1283-1294).

**Issue**: §5.5 says `nameof(X)` emits as interned `const FString*`. Verify:

```csharp
class MyActor {
    public void DoStuff() { var n = nameof(DoStuff); }  // n = "DoStuff"
}
```

At emit time, `nameof(DoStuff)` resolves to the literal "DoStuff". XIL2CPP emits `XCSharpString{&_String_DoStuff}` where `_String_DoStuff` is interned in `.rodata`. OK.

But the audit perspective asked about a more subtle case:

```csharp
class MyActor {
    public string GetName() { return nameof(this.SomeProp); }  // n = "SomeProp"
}
```

`nameof` on instance members (C# 12 feature): the expression resolves to the member name string at compile time. Same interning treatment. OK.

```csharp
class MyActor<T> {
    public string GetName<U>() { return nameof(T) + nameof(U); }  // T and U are type parameters; nameof resolves to their type-parameter names
}
```

In a generic context, `nameof(T)` resolves to the **type parameter name "T"**, not the closed type. So `nameof(T)` in the open generic context emits `_String_T` (literal "T"). At closed instantiation `MyActor<XPlayer>`, `nameof(T)` STILL emits `_String_T` — not `_String_XPlayer`. This is what C# requires.

But: what about `nameof(this.SomeProp)` where the property is inherited and the actual property name differs from the source-of-declaration name? `nameof` resolves to the literal member name at the source-of-the-nameof-expression — which is what C# does. OK.

**Fix**: §5.5 adds a brief paragraph confirming the C# 12 semantics: `nameof(X)` always evaluates at the source-of-expression site to the literal name as it appears in the source; XIL2CPP interns the literal. Generic type parameters resolve to the type-parameter's *declared* name, not the closed-instantiation name.

---

### FIX-A-MED-11 [severity: MEDIUM] — Properties-on-records: `with` expression backing-field write barrier

**Location**: XIL2CPP.html §5.1 (record emit), §6.3 (barrier).

**Issue**: C# 12 record (positional, immutable):

```csharp
public record ActorInfo(XActor Actor, int Score);

void Update(ActorInfo info) {
    var newInfo = info with { Actor = otherActor };  // creates a new ActorInfo with mutated field
}
```

§2.2 says `with` expression lowers to "copy constructor invocation followed by property setter calls". Specifically:
```cpp
ActorInfo newInfo = ActorInfo(info);  // copy ctor
newInfo.set_Actor(otherActor);        // setter call
```

The setter on `Actor` writes through the backing field. Per the new-object-init barrier omission rule (§6.3 rule 1), the barrier is omitted because the object is being constructed. But the `with` expression is NOT constructing a new object from scratch — it's COPYING an existing object and then mutating. By the time the setter fires, the new object IS reachable (it's the local `newInfo`).

If the setter fires WITHOUT a barrier (per the "construction is in progress" rule), the SATB queue misses the old value (which is `info.Actor`, not nullptr). The card-table may also miss the card mark.

**Fix**: Rev 2 §5.1 (or §6.3 rule 1) clarifies: the "new-object-init barrier omission" applies ONLY to writes within a constructor BEFORE the object is reachable by any caller. The `with` expression's setter calls fire AFTER the copy ctor returns; the new object IS reachable; the barrier IS required.

Update §6.3 rule (1) wording:

> The barrier is omitted ONLY when (a) the assignment is the in-constructor initialization of the object's own field, AND (b) the object's address has not been escape-published (no `this`-pointer leaks, no reference stored to a static, no return-to-caller). The `with` expression's setter calls fire AFTER the copy constructor returns and the new object is reachable; barriers MUST fire.

---

### FIX-A-MED-12 [severity: MEDIUM] — Object initializer barrier emission window unclear

**Location**: XIL2CPP.html §5.4 (object initializer), §6.3 rule 1.

**Issue**: Same as FIX-A-MED-11 in reverse. Object initializer:

```csharp
var newSlot = new Slot { Holder = someActor, Index = 5 };
```

§2.2 says object initializer lowers to constructor + setter calls. Per §6.3 rule (1) "construction is in progress", barrier omitted. But:

```csharp
var initializer = new Slot { Holder = someActor };
// Slot is now constructed and reachable as 'initializer'
SomeMethod(initializer);  // initializer's Holder is rooted
```

If the setter on `Holder` fires DURING construction (before `new Slot { ... }` returns), the barrier omission is correct: the object isn't reachable yet. After the return, any subsequent write needs a barrier.

But what if the object is XObject-derived (currently XIL2CPP001 emits the error)? OK, this case is banned.

For non-XObject reference types containing XObject refs: same rule applies. The initializer's setters are part of construction.

**Fix**: §5.4 clarifies the object-initializer setter emit:

- All setters within the object-initializer block are treated as in-construction writes; barriers omitted per §6.3 rule (1).
- Any setter call AFTER the object-initializer expression completes IS post-construction; barriers required.

Pass 3's enumeration walks AST: the object-initializer block is identified by `ObjectCreationExpressionSyntax.Initializer` (Roslyn API). Setters inside that block omit the barrier.

---

### FIX-A-MED-13 [severity: MEDIUM] — `lock(obj)` on sim-path uses identity hash that's also banned

**Location**: XIL2CPP.html §5.10 (line 1611-1619), §7.4 (banned API list).

**Issue**: As noted in the cross-system contradictions section, §5.10's non-sim-path `lock(obj)` mapping uses `FObjectMonitorRegistry::GetOrCreate(someObject)`. The registry presumably keys on `(XObject*)someObject`'s identity — and identity-hashing on an XObject is what `XObjectKey.GetHashCode()` does (per XCoreXObject §6.4).

`XObjectKey.GetHashCode()` is BANNED on sim-path per `XIL2CPP046` (§7.3 line 2080). Same mechanism. The contradiction: sim-path bans the very mechanism that non-sim-path `lock(obj)` would use.

This is a soft inconsistency because **lock is also banned on sim-path** (§5.10) — so the conflict never arises in source. But it suggests the registry's identity-hash story isn't fully thought through.

**Fix**: Rev 2 §5.10 specifies the registry uses `FXObjectArray` index-based identity (the `XObject::InternalIndex`) as the hash key, not `XObjectKey`. This avoids the `XObjectKey` ban on sim-path (which would still be irrelevant since lock is banned on sim-path) and aligns with the FXObjectArray-based identity model.

---

### FIX-A-MED-14 [severity: MEDIUM] — Lambda capture of `this` via implicit field reference: undocumented

**Location**: XIL2CPP.html §5.9 (lambda emit).

**Issue**: Implicit `this`-capture in a lambda inside an XObject-derived class:

```csharp
class MyActor : XObject {
    private XComponent component;
    
    public Action MakeUpdater() {
        return () => component.Update();  // implicitly captures `this`
    }
}
```

The lambda captures `this` (so it can access `component`). §5.9's emit shows explicit capture of `owner` but not implicit `this`. The lambda's closure struct must:
1. Capture `this` (XObject*) as a field.
2. Register `this` in the XGCRootSpan (so the actor isn't collected while a lambda referencing it lives).

§5.9's example happens to bind `owner = this;` then capture `owner` — the same effect. But typical C# code writes `() => component.Update()` and lets the compiler capture `this` implicitly. Rev 1's emit example shows only the explicit pattern.

**Fix**: Rev 2 §5.9 documents implicit `this`-capture explicitly: when a lambda body references any non-static member of an enclosing class, XIL2CPP captures `this` as a field in the closure struct, registers it in the XGCRootSpan, and dispatches through `this->component` style member access.

---

### FIX-A-MED-15 [severity: MEDIUM] — Multi-step expression `a.b.c.Field = x` re-rooting through intermediate temporary

**Location**: XIL2CPP.html §6.3 rule (3), §5.3.

**Issue**: §6.3 rule (3) says intermediate reads in `obj.a.b.c.Field = x` are bare loads; only the final assignment has the barrier. This assumes intermediate reads return non-temporary references (lvalues that are stable). For:

```csharp
class Outer { public Mid mid; }
class Mid { public Inner inner; }
class Inner { public XActor actor; }

var outer = ...;
outer.mid.inner.actor = newActor;  // chain through three field reads
```

Each intermediate field read is a bare pointer load. The final write `outer.mid.inner.actor = newActor;` has the barrier. parent_obj is `outer.mid.inner` (the directly-enclosing object). But the inner-pointer (the `actor`-holding object) is what gets card-marked, not `outer`. **The card-table mark is on `inner`, not `outer`** — which is correct, because the slot being mutated is `inner->actor`.

BUT: if any of the intermediate fields are XPtr<T> (managed pointer types), the read produces a temporary XPtr<T> value (by-value return). Writing through a temporary's `.Raw()->Field = x` writes to the *temporary's stored T* — the temporary is destroyed at end-of-statement; the write may go to dead memory.

§5.3 doesn't show the intermediate XPtr<T> case. The audit perspective specifically asked about it.

**Fix**: Rev 2 §5.3 (or §6.3) clarifies: intermediate XPtr<T> reads on a property chain are `XPtr<T>::Raw()`-promotions (returning a stable T*), not value-temporary returns. The chain `outer.mid.inner.actor = newActor` lowers to `outer.mid.Raw()->inner.Raw()->actor = newActor` (if mid and inner are XPtr<T> fields). The Raw() promotions are stable through the statement. Confirm XCore-4a/4b's XPtr semantics match.

If the XPtr return is by-value temporary, the spec must reject the chain at Pass 3: emit diagnostic XIL2CPP063 — "Chained write through XPtr<T> intermediate is undefined; assign to a stable local first."

---

### FIX-A-MED-16 [severity: MEDIUM] — Conservative XGCRootSpan slot validation: validation-order semantics undocumented

**Location**: XIL2CPP.html §6.6 (line 2015).

**Issue**: §6.6 says: "the collector treats every slot as a potential XObject* and validates at scan time (the slot is dereferenced as `XObject*` only if it's a valid pointer into the XObject heap)."

Specifically: how does the collector validate? Two strategies:

1. **Pointer-range check**: every slot value is compared against the FXObjectArray's known address range. If in range, treat as XObject*; if out of range, skip. Fast (~2 cycles per slot).
2. **Pointer-tag check**: a low-bit tag distinguishes XObject* from non-pointer values. Requires tagging discipline at every write site. Not currently in XCoreXObject's design.

Strategy 1 is what XCoreXObject implements (per Rev 4 §5.3 "Conservative validation order per FIX-A-MED-35"). XIL2CPP-emitted code for `List<object>` must produce slots whose values are either valid XObject* or known-safe-non-XObject-values (raw integers, dangling-zero patterns, etc.).

**Fix**: §6.6 cross-references XCoreXObject §5.3's validation protocol and clarifies what XIL2CPP must NOT emit: pointer values from non-XObject heap regions stored in a Conservative span will be misinterpreted as XObject*. If the C# author stores arbitrary unmanaged data through `List<object>`, the result is undefined. Diagnostic XIL2CPP070 should be elevated to error if the closed type's contents cannot be statically proven to be only-XObject-or-known-safe-non-XObject; warning is too weak for sim-path.

---

### FIX-A-MED-17 [severity: MEDIUM] — TArray's `Add` calls XPACT_GC_STORE with wrong parent

**Location**: XIL2CPP.html §5.7 (lines 1399-1402).

**Issue**: §5.7's TArray::Add emit uses `XPACT_GC_STORE(this, &m_data[m_count], item.Raw());` — same problem as FIX-A-MED-6: the `this` is the TArray instance, not the parent XObject. Linking to FIX-A-MED-6 for the resolution.

**Fix**: Same as FIX-A-MED-6. Pass the parent XObject to the TArray constructor; use `m_parent` in the barrier.

(This is a duplicate observation; mentioned here so a Rev 2 author searching for "Add" finds the cross-reference.)

---

### FIX-A-LOW-1 [severity: LOW] — §1.4 cross-system matrix omits XSafePoint / XGCSafepoint

**Location**: XIL2CPP.html §1.4 (cross-system integration matrix).

**Issue**: The matrix lists XCoreXObject as the integration point, but the specific safe-point subsystem (referenced as `XGCSafepoint` in §6.5) is not mentioned. The cross-tool integration for safe-point ordering is significant enough to merit its own row OR a sub-bullet in the XCoreXObject row.

**Fix**: §1.4 XCoreXObject row adds: "...; XGCSafepoint singleton, safe-point flag, `XPACT_SAFEPOINT_CHECK` macro (per FIX-A-CRIT-6 resolution)..."

---

### FIX-A-LOW-2 [severity: LOW] — §11.1 criterion (a) GC pause budget conflates barrier cost with safe-point cost

**Location**: XIL2CPP.html §11.1, criterion (a) row.

**Issue**: Criterion (a) row says: "Barrier emit cost ≤ 5 cycles; correct stack-map per function; safe-point check ≤ 5 cycles". The 5-cycle figures are correct, but per XCoreXObject Rev 4 §5.5 line 1307, the safe-point check is "~2 cycles in steady state" (TLS + branch). The "≤ 5 cycles" cap in XIL2CPP is acceptable as a budget but misleading.

**Fix**: §11.1 criterion (a) row updates: "barrier emit cost ≤ 5 cycles; correct stack-map per function; safe-point check ≤ 2-5 cycles (steady-state TLS read + branch)". Same precision as XCoreXObject Rev 4 §5.5.

---

### FIX-A-LOW-3 [severity: LOW] — §6.5 quotes "3-5 cycles" for safe-point check; actual XCoreXObject says "2 cycles"

**Location**: XIL2CPP.html §6.5 (line 2001).

**Issue**: §6.5 says: "The cost is ~3-5 cycles per call per XCoreXObject.html §5.5 line 1301." But §5.5 line 1307 says: "Hot-path cost in steady state: when no safe-point is requested, the flag check is one TLS-cached read + one branch (~2 cycles)."

The 3-5 cycles is for the WRITE BARRIER (line 1214), not the safe-point check.

**Fix**: Update §6.5 to cite the correct XCoreXObject section + line + cost. Should be "~2 cycles per call per XCoreXObject.html §5.5 line 1307."

---

### FIX-A-LOW-4 [severity: LOW] — `XPACT_BACKEDGE_SAFEPOINT_CHECK` is just an alias; document explicitly

**Location**: XIL2CPP.html §6.5 (line 1998).

**Issue**: `XPACT_BACKEDGE_SAFEPOINT_CHECK XPACT_SAFEPOINT_CHECK` — same macro, just aliased. The aliasing suggests they might diverge in future (e.g., back-edge check could be cheaper, polling a flag without TLS update). Document the alias rationale: currently identical, may differ post-MVP if back-edge polling can be made cheaper.

**Fix**: §6.5 adds a comment: "BACKEDGE alias is provisional; allows future divergence (e.g., back-edge polling at half-rate of function-entry polling for tight loops)."

---

### FIX-A-LOW-5 [severity: LOW] — §6.2 doesn't specify thread-safety of XGCRootSpan registration

**Location**: XIL2CPP.html §6.2.

**Issue**: §6.2 describes XGCRootSpan registration in constructors and unregistration in destructors. Multi-threaded scenarios: two threads concurrently construct two different TArray instances; both call `XGC_RegisterRootSpan`. The XCoreXObject registry must be thread-safe. Rev 1 doesn't say.

Per XCoreXObject Rev 4 §5.3 line 1242, registration uses an internal lock. OK. But for stress testing, the registration cost under contention is unknown. Per Contract §3.4, container constructors run during normal mutator execution.

**Fix**: §6.2 cross-references XCoreXObject Rev 4 §5.3 for the thread-safety contract. Add a note: "XGC_RegisterRootSpan is thread-safe; contention is low because container constructors are infrequent vs heap-allocated containers' lifetimes."

---

### FIX-A-LOW-6 [severity: LOW] — `XPACT_UNLIKELY` is invented; not in XCore

**Location**: XIL2CPP.html §6.5 (line 1992).

**Issue**: §6.5 uses `XPACT_UNLIKELY(...)` in the safe-point macro. Where is this defined? XPact doesn't have this macro documented in any of the seven existing docs (a grep across `Documents/` confirms). Standard alternative: `__builtin_expect(..., 0)` (Clang/GCC); MSVC has `[[unlikely]]` C++20 attribute.

**Fix**: §6.5 defines `XPACT_UNLIKELY` in XCoreXObject Rev 5 (or use the existing C++20 `[[unlikely]]` attribute directly). Cross-reference the definition.

---

### FIX-A-LOW-7 [severity: LOW] — `XCSharpStringBuilder` is invented; not in XCore-4a / XCore-4b

**Location**: XIL2CPP.html §5.5 (line 1308).

**Issue**: §5.5 uses `XCSharpStringBuilder`. The audit cannot find this type in XCore-4a or XCore-4b. The closest is `FString` (XCore-4a) and `FStringBuilder` (commonly assumed). XIL2CPP cannot use an undefined type.

**Fix**: Rev 2 either: (a) cross-reference XCore-4a's existing `FStringBuilder`-equivalent and rename `XCSharpStringBuilder` to that; (b) introduce `XCSharpStringBuilder` as a new XPact.CSharp.BCL type and add to XPact.CSharp.BCL NuGet ref-DLL. Document the choice.

---

### FIX-A-LOW-8 [severity: LOW] — §6.4 introduces `XObject.NewRoot<T>` factory not in XCoreXObject Rev 4

**Location**: XIL2CPP.html §6.4 (line 1981).

**Issue**: §6.4 says: "bona fide null-outer use cases (root objects of an ownership chain) must use the explicit `XObject.NewRoot<T>(name, flags)` factory which has no `outer` parameter and is restricted to internal engine use via an `[XObjectInternalConstructor]` attribute."

But `XObject.NewRoot<T>` is not in XCoreXObject Rev 4's §3.5 NewObject contract (which is `NewObject<T>(Outer, Name, Flags)` strict). XIL2CPP is introducing a new factory.

**Fix**: Rev 2 either coordinates with XCoreXObject Rev 5 to add `NewRootObject<T>(Name, Flags)` to the canonical surface, OR removes the §6.4 reference and routes root-object construction through a `null` outer with explicit override via internal attribute. The user has stated `new Foo()` for XObject is BANNED but root-object construction must have *some* path. Coordinate.

---

### FIX-A-NIT-1 [severity: NIT] — §1.4 cross-system matrix has `XCoreXObject` row but FIX-A-MIN-47 was renumbered

**Location**: XIL2CPP.html §1.4 (XCoreXObject row).

**Issue**: References "FIX-A-MIN-47" in XCoreXObject Rev 4. Verify the FIX numbers are stable (XCoreXObject Rev 4 §15 may have renumbered).

**Fix**: Verify cross-reference; update if FIX-A-MIN-47 moved.

---

### FIX-A-NIT-2 [severity: NIT] — §4.1 has two "Indexers" rows (duplicate)

**Location**: XIL2CPP.html §4.1, rows 16 and 18 (within Type-declaration block).

**Issue**: §4.1's table has "Indexers" listed twice — once at row 16 ("Indexers ... §5.4"), once at row 18 ("Indexers ... §5.4"). Duplicate row.

**Fix**: Remove one of the duplicate rows.

---

### FIX-A-NIT-3 [severity: NIT] — §4.1 mixes em-dash and en-dash inconsistently

**Location**: XIL2CPP.html §4.1.

**Issue**: Some rows use `&mdash;` and some use `-` directly. Minor consistency.

**Fix**: Normalize all em-dashes in §4.1 to `&mdash;`.

---

### FIX-A-MED-18 [severity: MEDIUM] — `[XFunction(NoThrow = true)]` on C# function with `throw`: enforcement

**Location**: XIL2CPP.html §3.3, §5.12.

**Issue**: §3.3 says `[XFunction(NoThrow = true)]` is an explicit opt-in; XIL2CPP refuses if it cannot satisfy the noexcept proof. §5.12 says "Tier 2 function attempting to throw" emits XIL2CPP032 error.

But the spec doesn't say: what if the C# function has a `[NoThrow]` annotation AND has a transitively reachable callee that is NOT NoThrow-annotated AND is in another module that hasn't been transpiled yet (so Pass 1 conservatively treats it as Tier 1)? Per the locked Tier 2 rule, this annotated function would be classified Tier 1 (annotation can't promote to Tier 2 without proof). Diagnostic should fire: "annotation NoThrow on function X cannot be satisfied; transitive callee Y is conservatively Tier 1."

Current XIL2CPP030 ("[XFunction(NoThrow = true)] proof failed") covers this. OK. But the diagnostic doesn't say *which* unproven callee. Improve diagnostic to enumerate the failing callee chain.

**Fix**: §12.1 diagnostic XIL2CPP030 emit text adds: "failed proof; unverified callees: <callee1>, <callee2>, ...". This aligns with the audit table O-NOTHROW-CROSSMODULE-CPP item in §14.

---

### FIX-A-MED-19 [severity: MEDIUM] — Generic constraint `where T : unmanaged` and XObject-related types

**Location**: XIL2CPP.html §5.8 (line 1448).

**Issue**: §5.8 lists `where T : unmanaged` as supported. `unmanaged` in C# means "no managed references; flat struct of primitives or other unmanageds". XObject IS a managed reference type (the C# side of XPact). So `T : unmanaged` is only satisfied by primitives and value-type structs with no XObject fields.

XIL2CPP must enforce this constraint at Pass 3: a generic instantiation `Foo<XActor>` with `where T : unmanaged` fails to satisfy the constraint. Diagnostic emit: XIL2CPP123 (depth) or a new one for constraint failure.

**Fix**: §5.8 adds: "Generic-constraint satisfaction is checked at Pass 3 closed-instantiation walk. A type that's `where T : unmanaged` and instantiated with an XObject-derived T emits diagnostic `XIL2CPP125 — generic constraint 'T : unmanaged' violated by closed type 'X' which is XObject-derived`."

---

### FIX-A-MED-20 [severity: MEDIUM] — `where T : new()` constraint and XObject's BANNED `new Foo()`

**Location**: XIL2CPP.html §5.8 (line 1448), §2.3 (Locked Commitment 3).

**Issue**: `where T : new()` in C# means "T has a public parameterless constructor". XObject-derived types CANNOT have a public parameterless ctor (Locked Commitment 3 forbids `new Foo()`). So `where T : new()` is unsatisfiable for any XObject-derived T.

§5.8 doesn't say this. A generic method:
```csharp
public T CreateDefault<T>() where T : new() {
    return new T();
}
```
…invoked as `CreateDefault<XActor>()` should fail at Pass 3 because `XActor` cannot satisfy `new()`.

But XIL2CPP §2.3 (line 246-247) declares `XObject.New<T>(...)` factory: `public static T New<T>(...) where T : XObject, new();`. **This is also unsatisfiable** — the factory itself has the `new()` constraint, but no XObject-derived type has a public parameterless ctor.

The actual mechanism: the C# `where T : new()` is a hint for the Roslyn analyzer; the runtime invocation goes through the FClass's ClassConstructorFn slot, not through reflection. Roslyn's static check is what fires on closed instantiation. But the constraint check on `XObject.New<T>(...)` itself should be satisfied — and it can't be, because no XObject has a public ctor.

**Fix**: Rev 2 §2.3 + §5.8 explain: `where T : new()` constraint on `XObject.New<T>` is a Roslyn-side hint that doesn't reflect the actual XObject construction model. The `where T : new()` constraint is **satisfied artificially** by treating XObject-derived types as "having a synthetic parameterless ctor" for the purposes of the constraint check; the actual construction goes through the FClass.

This is somewhat hacky; the cleaner alternative is to drop `where T : new()` from `XObject.New<T>` and rely on the runtime ClassConstructorFn check instead. Then `where T : new()` reverts to its normal C# semantics.

Recommend: drop the `where T : new()` from `XObject.New<T>` in §2.3 line 247 + 253.

---

### FIX-A-MED-21 [severity: MEDIUM] — `where T : XObject` constraint enforces nothing at C++ emit

**Location**: XIL2CPP.html §5.8.

**Issue**: `where T : XObject` is a C# constraint enforced by Roslyn. The transpiled C++ has `template<typename T>` with no constraint (C++20 concepts not assumed). If a non-XObject T is somehow instantiated (e.g., via cross-module breakage), the C++ code may compile but produce nonsense.

**Fix**: §5.8 specifies: every `where T : XObject` constraint in C# emits a C++ `static_assert(std::is_base_of_v<XObject, T>, ...)` in the template body. Same for other constraints (`struct`, `unmanaged`, `new()`, base classes, interfaces). The static_assert provides belt-and-suspenders at C++ compile time.

---

### FIX-A-MED-22 [severity: MEDIUM] — Implicit conversions and barrier emission

**Location**: XIL2CPP.html §5.1 (User-defined conversions row), §6.3.

**Issue**: C# allows user-defined implicit conversions:

```csharp
public class TaggedActor {
    public static implicit operator XActor(TaggedActor t) { return t.actor; }
}

// Usage:
TaggedActor t = ...;
this.myActorField = t;  // implicit conversion fires; result is XActor; assignment to ref field needs barrier
```

The assignment `this.myActorField = t` lowers to `this.myActorField = (XActor)t` lowers to `this.myActorField = TaggedActor.op_Implicit(t)`. The barrier emission rule (Pass 3 walks AssignmentExpressionSyntax with LHS = ref field) DOES fire on `this.myActorField = ...`. The implicit conversion call is a function call, the result is an XActor value, the assignment writes the XActor to the field. **OK.**

But: what if the implicit conversion returns a temporary XPtr<T>? The barrier writes through a stable T*. If the conversion's return is `XPtr<XActor>` by value, the write may use the temporary's Raw() pointer, which becomes dangling. Same concern as FIX-A-MED-15.

**Fix**: Same as FIX-A-MED-15. Verify XPtr<T> return semantics from user-defined conversions; emit a stable local first if needed.

---

### FIX-A-MED-23 [severity: MEDIUM] — Auto-property `set` with reference type: spec example shows raw load, no barrier

**Location**: XIL2CPP.html §5.2 (line 1138-1140).

**Issue**: §5.2 line 1138-1140:
```cpp
extern "C" void _v1..._Foo__set_Foo_P_R_Foo_V_int(::Foo* self, int32_t value) noexcept {
    self->_FooBackingField = value;
}
```

This is the property setter for an `int` property. No barrier. OK because int is not a reference.

But §5.2 line 1144 says: "If the property is on an XObject-derived type and the property's type is itself XObject-derived (e.g., `public XActor Owner { get; set; }`), the setter emits `XPACT_GC_STORE` per §6."

The audit must verify: for a `public XActor Owner { get; set; }` on an XObject-derived type, the emit is:
```cpp
extern "C" void _v1..._Foo__set_Owner_P_R_Foo_V_XActorPtr(::Foo* self, ::XActor* value) noexcept {
    XPACT_GC_STORE(self, &self->_OwnerBackingField, value);
}
```

This is consistent. But the spec example at line 1138-1140 (the integer case) doesn't show the reference case. The audit confirms reading §6.3 + §5.4 together gives the right answer; the spec is just terse.

**Fix**: Rev 2 §5.2 adds an explicit reference-property setter emit example showing the barrier. Cross-reference §6.3 in §5.2.

---

### FIX-A-MED-24 [severity: MEDIUM] — `init` accessor enforcement

**Location**: XIL2CPP.html §5.4 (init-only properties).

**Issue**: §5.4 says: "XIL2CPP emits the setter visible only to the constructor. Outside the constructor, the setter is mangled with a different suffix that the AST-normalization pass rejects."

This is the wrong mechanism. The C# `init` accessor is enforced by Roslyn at the C# source level: the compiler refuses to call the setter outside an init context. By the time XIL2CPP runs (Pass 1 parses the source), any `init`-accessor violation has already failed parsing. XIL2CPP doesn't need to mangle anything differently.

The C++ output: the `init` accessor body is identical to a `set` accessor body. The C++ side has no restriction; the restriction was enforced at the C# level.

**Fix**: §5.4 rewrites: "C# `init` accessor enforcement is at the Roslyn parse level; by Pass 3, all violations are already rejected. XIL2CPP emits the init accessor body identically to a set accessor body. The C++-side enforcement is absent because the C# enforcement was sufficient."

---

### FIX-A-MED-25 [severity: MEDIUM] — `default(T)` for reference types: zero-initialization vs nullable

**Location**: XIL2CPP.html §5.3.

**Issue**: `default(T)` in C# returns the default value: `null` for reference types, `0` for ints, etc. For XObject-derived T, `default(T)` is `null`. For `XPtr<T>`, the default should also be null-pointer.

XIL2CPP must emit `default(XActor)` as `(XActor*)nullptr` and `default(XPtr<XActor>)` as `XPtr<XActor>{}` (default-constructed empty XPtr).

If `default(T)` is assigned to a reference field, the assignment is a reference store; the barrier emits. The SATB pre-store captures the OLD value (which the GC needs to keep alive even though we're about to null the slot).

**Fix**: §5.3 adds: `default(T)` for any T resolves to the C++ value-initialization (`T{}` or `nullptr` for reference types). For XPtr<T>, value-initialization is empty XPtr. Barrier emission rules apply as for any reference store.

---

### FIX-A-MED-26 [severity: MEDIUM] — `goto` in async / iterator state machine: emit semantics

**Location**: XIL2CPP.html §4.1 (goto row line 638), §5.9.

**Issue**: §4.1 says `goto` "Supported (banned in async per dataflow)". `goto` inside an async method is illegal in C# because the state machine's state numbering doesn't accommodate arbitrary jumps. Roslyn rejects.

`goto` inside an iterator (yield return) is similarly restricted: `goto` can only jump within the same yield-block. Roslyn rejects.

For non-async / non-iterator code, `goto` lowers to a C++ `goto` (which is supported). OK.

**Fix**: §4.1 + §5.3 clarify: `goto` semantics inherit C#'s parse-time restrictions. XIL2CPP transpiles `goto` to C++ `goto` 1-to-1 for non-async / non-iterator contexts.

---

### FIX-A-MED-27 [severity: MEDIUM] — Method binding via delegate / Action / Func: post-MVP but design needs forward hook

**Location**: XIL2CPP.html §4.1 row "delegate declarations" (line 596), §5.14.

**Issue**: §4.1 lists delegate declarations as "Post-MVP (gated on Contract §1.1 amendment per Constraint §2.15)". §5.14 says when the Contract amendment lands, FXDelegate emits as `{InstanceXObject*, FunctionPointer}`.

The XObject* in FXDelegate is a reference; how is it rooted? Two options:
1. Per-delegate XGCRootSpan (32 bytes per delegate; cost-prohibitive for small delegates).
2. The delegate's owning container (TArray<FXDelegate> for multicast) has the XGCRootSpan; single delegates are stored in normal reference fields with the barrier.

§5.14 doesn't say. Forward-design hook.

**Fix**: Rev 2 §5.14 specifies: when delegates land post-MVP, multicast delegates emit `TArray<FXDelegate>` containers with strong XGCRootSpan over the InstanceXObject* fields. Single delegates (`Action`, `Func<T>`) emit as embedded fields whose containing object's schema vector (or XGCRootSpan) covers the InstanceXObject*.

---

### FIX-A-MED-28 [severity: MEDIUM] — Iteration over `IEnumerable<T>` interface: dispatch and rooting

**Location**: XIL2CPP.html §5.3 (foreach).

**Issue**: `foreach (var x in someEnumerable)` where someEnumerable is `IEnumerable<XActor>`: dispatches `GetEnumerator()` through interface vtable, returns `IEnumerator<XActor>` which itself is a reference type. The enumerator is reachable from the foreach body's stack; the FStackMapRecord must include it.

Per §5.3 line 1198 the foreach over List<T> uses `list.AsRangeView()` — a non-allocating range view. For `IEnumerable<T>` interface, the dispatched GetEnumerator() WILL allocate. On sim-path, this is forbidden allocation pressure.

**Fix**: §5.3 distinguishes: `foreach` over a known typed container (`List<T>`, `TArray<T>`, etc.) → range-view, no allocation; `foreach` over an interface (`IEnumerable<T>`) → BANNED on sim-path (allocation) via `XIL2CPP064`; allowed on non-sim-path with explicit enumerator allocation + FStackMapRecord entry.

---

### FIX-A-MED-29 [severity: MEDIUM] — Sim-path TU not allowed Conservative XGCRootSpan? Currently warning

**Location**: XIL2CPP.html §6.6.

**Issue**: §6.6 says Conservative XGCRootSpan on sim-path emits warning XIL2CPP070. The constraint inventory Q18 notes the warning is per-closed-type-instantiation. But "warning" doesn't enforce; a sim-path module can ship with Conservative spans which cross-arch determinism may diverge on (different pointer-range checks per arch).

Per Contract §4.5 line 885: `SimPathConservativeRootsAllowed` is a per-target setting. If false (default for sim-path strict), Conservative becomes an error.

Rev 1 §6.6 doesn't reference this setting. The audit suggests Rev 2 align with the Contract: if `SimPathConservativeRootsAllowed = false`, emit XIL2CPP070 as ERROR (not warning).

**Fix**: §6.6 cross-references `SimPathConservativeRootsAllowed`. Diagnostic XIL2CPP070 severity: error if `SimPathConservativeRootsAllowed = false` on sim-path TU; warning otherwise.

---

### FIX-A-MED-30 [severity: MEDIUM] — `_FStackMap_RegisterAtLoad` registration ordering

**Location**: XIL2CPP.html §6.1 (lines 1927-1933).

**Issue**: §6.1 shows:
```cpp
extern "C" int _StackMap_DoWork_RegisterAtLoad = [](){
    ::XCore::Reflect::XStackMapTable::Register(...);
    return 0;
}();
```

This is a global-init lambda. Module-level static init order is generally unspecified across TUs in C++. If `XStackMapTable::Register` is called BEFORE the table itself is initialized (XStackMapTable is in XCoreXObject), the registration fails.

Two solutions:
1. **Lazy init**: XStackMapTable lazily allocates on first Register call.
2. **Init priority**: XStackMapTable is in an init-priority 0 (or `__attribute__((init_priority(101)))`) section; the per-function registrations are in init-priority 200. Ordering guaranteed.

§6.1 doesn't say.

**Fix**: Rev 2 §6.1 specifies the init-ordering protocol. Recommend solution 1 (lazy init in XCoreXObject; cross-reference XCoreXObject Rev 5).

---

### FIX-A-MED-31 [severity: MEDIUM] — Tier 2 functions are not hot-reloadable; what about call sites?

**Location**: XIL2CPP.html §8.5 (lines 2194-2196), Contract §5.4.

**Issue**: §8.5 says Tier 2 functions are not individually hot-reloadable; they rebuild with their owning module. But what about a Tier 1 function that calls a Tier 2 function in the same module? When the Tier 2 function's owning module rebuilds (hot-reload triggers), the Tier 1 caller may keep its old binding (to the old Tier 2 function). After the rebuild, the new Tier 2 function has the same signature but possibly different body. The Tier 1 caller's direct-call instruction (not the RVA-rewrite trampoline) still points to the old code.

This is OK iff the same-module rebuild also rebuilds the Tier 1 caller. Hot-reload cascade rebuild is the mechanism. But the audit perspective wants to verify: a Tier 1 function in module A calling a Tier 2 function in module A — both rebuild together. **OK**.

A Tier 1 function in module A calling a Tier 2 function in module B — module B's Tier 2 function is NOT exported (Tier 2 has no extern "C" shim per §5.4). So cross-module Tier 2 calls are forbidden by the ABI. Diagnostic should fire if Pass 4 detects this.

**Fix**: §8.5 clarifies: cross-module callees in Pass 1 are always conservatively Tier 1 OR explicitly marked NoThrow Tier 2 with the extern "C" shim. There is no cross-module Tier 2 direct call. Document this in the Pass 4 classification.

---

### FIX-A-MED-32 [severity: MEDIUM] — `[XFunction(NoThrow)]` annotation lives in C# attribute namespace TBD

**Location**: XIL2CPP.html §3.3, §5.12.

**Issue**: `[XFunction(NoThrow = true)]` is referenced but the C# attribute's namespace is unspecified. Should be `XPact.CoreXObject.XFunctionAttribute` (or similar).

**Fix**: Rev 2 specifies the attribute namespace. Cross-reference XHT.html for the C# attribute declarations.

---

### FIX-A-MED-33 [severity: MEDIUM] — Memory-tag attribution for sim-path TUs not specified

**Location**: XIL2CPP.html §6.

**Issue**: XCore-4a defines `EMemTag` (per `FMemTag::XObject` for XObject allocations). XIL2CPP-emitted code calls `FMemory::Alloc`, `FMemory::Realloc`, `XCore::Reflect::NewObject` — what memory tag is attributed?

For sim-path TUs, memory allocations should be tagged distinctly so the leak tracker can identify sim-path-tier allocations vs editor-tier. Currently undocumented.

**Fix**: Rev 2 §6 specifies memory-tag attribution:
- XObject allocations: `EMemTag::XObject` (XCore-4a default).
- Sim-path container allocations: `EMemTag::SimPathContainer` (new tag, post-MVP coordination with XCore-4a Rev 5).
- Lambda / closure allocations: `EMemTag::Lambda` (new tag).
- Async state machine allocations: `EMemTag::AsyncStateMachine` (new tag).

---

## Summary of cross-references

| Finding | Cross-references | Affected docs to update |
|---|---|---|
| CRIT-1 | Contract Rev 13.9 §0 | XIL2CPP §5.1 + §9.7 |
| CRIT-2 | XCoreXObject §5.4 | XIL2CPP §6.1 (substantial rewrite); coordinate XCoreXObject Rev 5 |
| CRIT-3 | Contract §3, §5 | XIL2CPP §6.3 + §4.1 + new diagnostic XIL2CPP005 |
| CRIT-4 | Contract §10.2 line 1984 | XIL2CPP §7.2 |
| CRIT-5 | XCore-4b FProperty taxonomy | XIL2CPP §6.3 substantial rewrite |
| CRIT-6 | XCoreXObject §5.5 | XIL2CPP §6.5 + XCoreXObject Rev 5 contract addition |
| HIGH-1 | Inventory line 747 | XIL2CPP §5.1 / §10.2 / §10.4 internal consistency |
| HIGH-2 | Contract §6.4 | XIL2CPP §7.5 + new diagnostics |
| HIGH-3 | Contract §4.3 | XIL2CPP §5.7 / §7 (new mapping table) |
| HIGH-4 | XCore-4b §75 | XIL2CPP §4.1 + §7.4 |
| HIGH-5 | XCore-4b line 724 | XIL2CPP §5.8 + §6.1 |
| HIGH-6 | XCoreXObject §5.3 | XIL2CPP §5.4 + new diagnostic XIL2CPP054 |
| HIGH-7 | XCore-4b 28 subclasses | XIL2CPP §6.3 + §5.4 + new check |
| HIGH-8 | (none) | XIL2CPP §4.1 + §5.6 + §5.7 + diagnostic XIL2CPP061 |
| HIGH-9 | XCoreXObject §2.4 | XIL2CPP §5.10 + lifecycle hook |
| HIGH-10 | XCoreXObject §5.2 | XIL2CPP §6.3 + XCoreXObject Rev 5 |
| HIGH-11 | XCore-4a atomics | XIL2CPP §7.4 + new diagnostic XIL2CPP055 |
| HIGH-12 | (none) | XIL2CPP §6.7 + §6.1 |
| HIGH-13 | (none) | XIL2CPP §5.13 |
| HIGH-14 | (none) | XIL2CPP §4.1 + §5.9 |
| MED-1 to MED-33 | Various; see each finding | XIL2CPP various sections |
| NIT-1 to NIT-3 | (cosmetic) | XIL2CPP §1.4 / §4.1 |

## Audit close

The Rev 1 design has solid pipeline architecture, good tier-classification protocol, and correct Locked Commitment enforcement. But the GC-integration section (§6) and sim-path discipline (§7) contain **6 CRITICAL and 13 HIGH-severity issues** — particularly around the stack-map mechanism (FIX-A-CRIT-2), the write-barrier enumeration completeness (FIX-A-CRIT-5), ref/out parameter handling (FIX-A-CRIT-3), and ABI tag content (FIX-A-CRIT-1 + CRIT-4 + CRIT-6).

The audit recommends **multiple Rev rounds** before XIL2CPP can converge — at least:
- Rev 2: address all CRITICAL findings; substantial rewrite of §6 (GC integration) and §7 (sim-path).
- Rev 3: address all HIGH findings; coordinate XCoreXObject Rev 5 for the macro/contract additions (`XPACT_SAFEPOINT_CHECK`, memory-order tags, `XGC_WriteBarrier` parent-nullable semantics).
- Rev 4: address MEDIUM findings; converge.

Given the perspective B audit (running in parallel) will surface additional issues, total Rev rounds for XIL2CPP to converge is likely 3-4 (comparable to XCoreXObject's 4 rounds).

The audit perspective is satisfied that Rev 1 is on the *correct architectural path* — the right design choices are mostly being made; the issues are gaps in specification completeness, not gaps in vision. Per Prime Directive, the right answer is to spec the gaps correctly the first time rather than ship a half-spec'd Rev 2.

End of XIL2CPP Rev 1 Audit Perspective A.
