# XIL2CPP Rev 1 — Round 1 Audit C — Hot-Reload Integration, ABI Stability, and XBT Build-Pipeline Integration

> **Audit perspective.** Auditor C (Hot-reload integration + ABI stability + XBT build-pipeline integration). Read-only audit of `Documents/XIL2CPP.html` Rev 1 dated 2026-05-28.
> **Cross-references.** `Documents/XCoreXObject.html` Rev 4 §§1.4, 1.5, 3.6, 5, 6, 7, 9, 10.10.1, 11.5; `Documents/XBT.html` Rev 10 §§3, 5.1, 5.2, 6, 8, 10, 15; `Documents/XToolchainContract.html` Rev 13.9 §§1.4, 2, 5, 10, 13, 14; `Documents/XHT.html` Rev 6 §§17.2-17.4; `Documents/XIL2CPP-Constraints.md` Sections 6, 7, 8, 9.
> **Output format.** Each finding is an ID-prefixed entry with the offending Rev 1 quote (or absence-quote), the bug, the fix, and severity.
> **Severity scale.** CRITICAL = design-breaking (cannot ship as written); HIGH = correctness bug at emit time / latent runtime hazard; MEDIUM = author-facing ergonomics or missing feature coverage with workaround; MINOR = doc-prose / numbering / cross-ref hygiene.
> **Locked-commitment respect.** Pure transpile, C# 12 / .NET 8 MVP, and `XIL2CPP001` on bare `new Foo()` are NOT revisited.

---

## Executive Summary

- **Total findings: 52.**
  - CRITICAL: 4
  - HIGH: 19
  - MEDIUM: 19
  - MINOR: 9
  - LOW: 1
- **Top 3 most impactful findings.**
  1. **FIX-C-CRIT-01 — ABI-envelope tag content drift between XIL2CPP.html and Contract Rev 13.9.** §5.1 and §9.7 of XIL2CPP.html hard-code `XPACT_FCLASS_LAYOUT_TAG = "v6"` and `sizeof(FClass) == 240`, but Contract §14.1 (Rev 13.8 Stage B) freezes `XPACT_FCLASS_LAYOUT_TAG = "FClass-v4: ..."` (a long descriptive string, not `"v6"`) and `sizeof(FClass) == 224`. **Every `.cs.cpp` XIL2CPP emits would fail to compile** against the contract-locked runtime. Same drift for `XPACT_FSTRUCT_LAYOUT_TAG` (doc says `"v5"` + 120 bytes; Contract says `"FStruct-v4: ..."` + 112 bytes). The `static_assert`s in §5.1 example and §9.7 list also drift on `XPACT_GC_ROOT_ABI_TAG` (doc: `"Span-based v1"` matches Contract); `XPACT_EXCEPTION_ABI_TAG` (doc: `"Tier1-Shim/Tier2-Direct"` matches); `XPACT_MANGLING_SCHEME_TAG` (matches); but the 15-tag content list in Contract §14.1 is the canonical surface and XIL2CPP must emit each tag's full content string verbatim, not the abbreviated `"vN"` shorthand the doc invents.
  2. **FIX-C-CRIT-02 — Hot-reload listener registration breaks across module unload.** §8.4 emits `::XCore::Delegates::OnClassReplaced.AddRaw([](...){ ... });` inside a function-local static lambda. The lambda's function-pointer lives in the DLL's `.text` segment. When the DLL unloads (which IS supported per XCoreXObject §10.13 listener-deregistration discipline), the OnClassReplaced delegate now holds a dangling function pointer. The next cascade fires through the dangling pointer and crashes. **The doc never specifies the matching `RemoveRaw` / deregistration.** This is exactly the bug XCoreXObject Phase 5.k shipped `IXObjectCreateListener::Remove*` methods to prevent, and XIL2CPP-emitted code skips them.
  3. **FIX-C-CRIT-03 — In-process cancellation contract is unspecified for state-on-disk and not robust against XBT-itself hot-reload.** §3.5 says `CancellationToken` is honored "at every Pass boundary" with "no partial output." But §9.5 says XBT may itself be hot-reloaded mid-build via XLiveCoding (this is the explicit Foundation Prototype path). If XBT hot-reloads while in-process XIL2CPP is mid-Pass-6, the C# managed memory inside the XBT process is migrated, but the file-handle state for half-written `.cs.cpp.tmp.<pid>.<actionid>` files is not addressed. No "reset hook" exists; Roslyn workspace caching policy is undefined; the in-process entry point has no per-invocation reset contract.

- **Rev 1 structural soundness from the hot-reload/XBT lens.** Pipeline §3, action graph §9.1, and tier classification §3.3 are conceptually sound. The mangling rule §8.1 is correctly deterministic. The fundamental holes are in: (a) ABI-tag content drift (CRIT-01); (b) hot-reload listener lifetime (CRIT-02); (c) in-process mode resource lifecycle (CRIT-03); (d) the schema-vector ownership resolution in §10.4 forward-commits XHT to do work for which XHT Rev 6 has no design; (e) the ReferenceCompileCSharpAction §9.8 ordinal-14 extension is a unilateral extension to XBT's action enum without coordination with XBT Rev 10's slot reservation (slots 9-15 are reserved; 14 is `Reserved_Phase2_F`); (f) the cache-key list in §3.4 / §9.4 is incomplete vs. the actual sources of non-determinism.

- **Backwards-incompatibility risks not caught by the constraint inventory.** The ABI-tag content drift (CRIT-01) is invisible to the inventory because Stage B Rev 13.8 was the freeze; the doc was authored against the older Rev 12 tag scheme. The OnClassReplaced listener leak (CRIT-02) is invisible because XCoreXObject Phase 5.k shipped after the inventory; the doc never references Phase 5.k. The exit-code 24 collision (FIX-C-HIGH-04) is a real risk: §9.6 cites Phase 1 stub exit code 24 (`PluginNotFound`) as the placeholder before full XIL2CPP, but the Phase 1 stub continues to run in CI environments alongside builds that have full XIL2CPP — 24 is a non-fatal "stub" exit that the build system must distinguish from "XIL2CPP truly couldn't run".

---

## Section 1 — Symbol mangling stability across hot-reload (§8.1)

### FIX-C-CRIT-01 — ABI envelope tag content drift breaks every `.cs.cpp` compile

**Offending Rev 1 quote (§5.1 + §9.7):**

> ```
> static_assert(::XPactDetail::CompileTimeStrEq(XPACT_FCLASS_LAYOUT_TAG, "v6"),
>               "FClass layout tag mismatch; rebuild XCoreXObject");
> ```
> ```
> static_assert(::XPactDetail::CompileTimeStrEq(XPACT_FCLASS_LAYOUT_TAG,       "v6"));
> static_assert(::XPactDetail::CompileTimeStrEq(XPACT_FSTRUCT_LAYOUT_TAG,      "v5"));
> static_assert(sizeof(::XCore::Reflect::FClass)        == 240);
> static_assert(sizeof(::XCore::Reflect::FStruct)       == 120);
> ```

**Bug.** Contract Rev 13.8 §14.1 (the Stage B addendum that froze ABI layout) defines the actual content of each layout tag verbatim:

> `XPACT_FCLASS_LAYOUT_TAG` → `"FClass-v4: 112 FStruct base + 112 FClass-specific = 224 bytes; ClassReps is TArray<FRepRecord> ..."` (a multi-line descriptive string)
> `XPACT_FSTRUCT_LAYOUT_TAG` → `"FStruct-v4: 112 bytes; ObjectRefProperties TArray @ offset 56 = 24 bytes; SchemaHash @ 80; SerializeStructFn @ 104"`
> `sizeof(FClass)` → `224` (not 240)
> `sizeof(FStruct)` → `112` (not 120)

XIL2CPP's emitted code uses the abbreviated `"v6"` / `"v5"` strings and the wrong sizeof values. Every `.cs.cpp` would fail `static_assert` against the contract-locked runtime header. This is a build-breaking bug, not a runtime bug — but the build-breakage is *uniform across every module that has any C# source*. No XIL2CPP-emitted code can compile.

The §1 doc-header on line 26 also says: `FClass = 240 bytes` and `FStruct = 120 bytes` — both wrong against Contract Rev 13.8.

**Fix.** Replace every `"v6"` / `"v5"` / `"v1"` shorthand in §5.1, §9.7, and the doc-header line 26 with the *verbatim* tag content from Contract §14.1's 15-tag table. The sizeof values must match Contract §14.2's 14-pin table: `FName=8`, `FField=32`, `FFieldClass=48`, `FProperty=104`, `FStruct=112`, `FClass=224`, `FEnum=72`, `FInterface=64`. Also: the doc must consume the tag-content macros by reference, not hard-code them in the spec — Contract §14.1 is the single source of truth. Add a forward-commitment: "XIL2CPP MUST emit the tag content by including `XReflectionRuntime.h` and using the macro directly; the static_assert compares the included macro to itself, ensuring drift between contract and runtime is caught at runtime-header authoring time." Also add an XIL2CPP-side diagnostic `XIL2CPP143 — ABI envelope tag content mismatch between manifest and runtime header` for the case where a manifest declares a tag content the runtime header doesn't define.

**Severity.** CRITICAL — entire ship-of-XIL2CPP would be blocked by this on day 1 of integration.

---

### FIX-C-HIGH-01 — Mangling rule does not pin Roslyn version

**Offending Rev 1 quote (§3.4):**

> ```
> CacheKey = SHA256(
>   XIL2CPP_binary_content_hash,
>   XPact_Mangling_NuGet_version,
>   XPact_CSharp_BCL_NuGet_version,
>   XPact_CSharp_Roslyn_NuGet_version,
>   ...
> )
> ```

**Bug.** The cache key correctly includes the Roslyn NuGet version. BUT the symbol mangling rule (§8.1 + Contract §2.2) does not. The mangled name depends on the namespace-qualified name, the method name, and the parameter type names — and Roslyn versions differ in how they resolve some C# 12 corner cases (e.g., the `required` member's compile-generated backing field name, the primary-constructor parameter spelling, the partial-class merge order). Two Roslyn versions could resolve the same source to differently-named methods, breaking byte-identical mangling across CI machines that differ in Roslyn patch version. The doc claims `criterion (g)` symbol stability but does not require Roslyn version pinning at the manifest level.

The doc also does not include Roslyn version in the `static_assert` envelope; only the Contract-version tag is pinned.

**Fix.** Add a `roslyn_version` field to the manifest `TargetInfo` FBS schema (analogous to the existing `mangling_scheme` + `gc_root_abi` fields). XIL2CPP `static_assert`s the manifest's `roslyn_version` against its built-in Roslyn version at emit time, fails build with `XIL2CPP144 — Roslyn version mismatch between manifest and XIL2CPP binary`. Note: this also addresses the cache-key correctness but the cache-key already hashed the NuGet version; the missing piece is the static_assert in emitted code.

**Severity.** HIGH — silent CI machine divergence is the worst class of hot-reload bug.

---

### FIX-C-HIGH-02 — Mangling does not include nullable annotations; nullability changes silently break ABI

**Offending Rev 1 quote (§2.2 + §5.4 + §8.1):**

> §5.4 "Nullable reference types. `XActor? Owner` emits the same C++ member as `XActor Owner` (no extra storage); the FProperty descriptor (XHT side) carries the `CPF_NullableReferenceType` bit."
> §8.1 "The mangled name depends only on (a) the contract version hash, (b) the namespace-qualified type name, (c) the method name (CLR convention), (d) the generic-arg manglings (in source-declaration order), (e) the parameter manglings (in declaration order; reference / value / by-ref / in / out prefixes)."

**Bug.** Two methods with signatures `void Foo(XActor a)` and `void Foo(XActor? a)` differ ONLY in nullable-reference annotation. Per §5.4 the C++ emit is identical. Per §8.1 the mangled name is identical (the parameter mangling doesn't include the nullable bit). So both methods mangle to the same symbol — but only one of them survives compilation. This is fine for the C# author (Roslyn flags it as a duplicate method); BUT the cross-tool ABI surface (XHT-emitted FProperty descriptor with `CPF_NullableReferenceType`) treats the two as distinct properties for serialization / replication. Hot-reload that adds a `?` annotation to a field DOES change the FProperty descriptor's bit (the schema-hash will rotate) BUT does NOT change the mangled symbol of any accessor. The patched DLL's `Z_Construct_FClass_...` function emits the updated schema-hash, but the consuming code's call to the property getter is bound by mangled name to the same symbol. **Layout drift goes undetected at the symbol level** — the schema-hash drift will catch some cases, but the FProperty-bit only changes, not the layout.

The Prime-Directive-correct path is: include nullability in the mangling explicitly. Contract §2.2 line 491 specifies the param mangling discriminators `R` (reference), `V` (value), `B` (by-ref), `I` (in), `O` (out). Nullable would be a 6th discriminator (the Auditor B audit flagged the same gap for `ref readonly`).

**Fix.** Add Contract §2.2 mangling: `RN` (nullable reference), or `Q` as a new discriminator for nullability. Same applies to `int?` (nullable value): mangling letter `VN` or `?`. Update §8.1 to reference the extended mangling. Critically: this is a *Contract* change, not a XIL2CPP-only change. Round 1 audit flags it as a Contract amendment requirement.

**Severity.** HIGH — silent ABI break under nullability edits.

---

### FIX-C-HIGH-03 — Mangling does not address generic-constraint changes

**Offending Rev 1 quote (§5.8 + §8.1):**

> §5.8 "Generic constraints: `where T : XObject`, `struct`, `unmanaged`, `new()`, etc. Supported."

**Bug.** C# allows generic constraints to be added or removed mid-version (e.g., promote `void Foo<T>(T x)` to `void Foo<T>(T x) where T : IComparable<T>`). The constraint change affects overload resolution (the constraint can disambiguate two otherwise-identical method names) but the mangling rule (§8.1, Contract §2.2) doesn't include constraints. The constraint change is invisible to the linker; the FClass-side dispatch must use the bind-time constraint, but the bind-time constraint may differ between old and new modules.

Worse: removing a constraint is ABI-compatible (a method that was constrained becomes more accepting); adding one is ABI-breaking (a method that was unconstrained becomes more restrictive). The doc gives no guidance.

**Fix.** Specify: (a) adding a generic constraint mid-session is BANNED (XLiveCoding rejects the patch as a signature change — needs a hot-reload-validation rule in §8.2); (b) removing a constraint is also banned for the same reason (overload resolution is affected). The conservative path: any constraint change is a signature change and breaks hot-reload. Document this in §8.2. Also add diagnostic `XIL2CPP034 — generic constraint change on existing method 'Foo<T>'; signature breakage forbidden by hot-reload commitment` enforced by the Pass 2 cross-module check.

**Severity.** HIGH — common refactoring pattern that would silently break hot-reload.

---

### FIX-C-MED-01 — Mangling does not address `[Obsolete]` / `[Conditional]` annotations

**Offending Rev 1 quote:** None — the doc never addresses `[Obsolete]`, `[Conditional]`, or any compile-time-only attribute interaction with mangling.

**Bug.** Adding `[Obsolete]` to a method is a documentation-only change that should NOT affect mangling. Removing the attribute is also documentation-only. But the doc doesn't *say* this is true. If a future implementer naively includes ALL attributes in the mangling (a defensive approach), adding `[Obsolete]` becomes an ABI break. Same risk for `[Conditional("DEBUG")]`, `[CallerMemberName]`, `[CallerFilePath]`, `[CallerLineNumber]` — none of which should affect mangling.

**Fix.** Specify §8.1: "Compile-time-only attributes (`[Obsolete]`, `[Conditional]`, `[CallerMemberName]`, `[CallerFilePath]`, `[CallerLineNumber]`, `[Experimental]`, all attributes inheriting `System.Attribute` that are not flagged `[XAttribute]`) DO NOT participate in symbol mangling. The mangling rule is purely structural over signature, not annotation."

**Severity.** MEDIUM — silent regression risk if a future implementer chooses the conservative path.

---

### FIX-C-MED-02 — Method-body content is correctly excluded but the doc never says so explicitly

**Offending Rev 1 quote (§8.1 line 2141-2144):**

> "The mangled name depends only on (a) the contract version hash, (b) the namespace-qualified type name, (c) the method name (CLR convention), (d) the generic-arg manglings (in source-declaration order), (e) the parameter manglings (in declaration order; reference / value / by-ref / in / out prefixes)."

**Bug.** This list correctly EXCLUDES method-body content; if body changes affected the mangle, every edit would break hot-reload. The doc never explicitly says "body content does not affect mangling" — it's implicit from the absence in the list. Worth being explicit.

**Fix.** Add to §8.1: "Method-body content (statements, expressions, local variables, called functions) is NOT part of the mangling. This is load-bearing for hot-reload: editing the body of a Tier 1 function must produce the same mangled symbol so XLiveCoding's RVA-rewrite trampoline can find the patch target."

**Severity.** MEDIUM — defensive documentation that prevents misunderstanding.

---

### FIX-C-HIGH-04 — Mangling does not pin .NET SDK version

**Offending Rev 1 quote (§2.2 + §3.4):**

> "Runtime target pinned at .NET 8 BCL surface."
> Cache key includes "XPact_CSharp_BCL_NuGet_version" but not the .NET SDK proper.

**Bug.** Two .NET 8 SDK patch versions (e.g., 8.0.100 vs 8.0.200) can resolve the same C# 12 source to different IL — relevant for any caller that consumes the IL — but XIL2CPP transpiles SOURCE, not IL. So in principle .NET SDK version doesn't affect XIL2CPP. BUT the BCL reference assemblies (used for type resolution) live in the .NET SDK, and a patch version can change reference-assembly bindings (e.g., a new overload added to `System.String.Concat`). The C# source can resolve to different overloads across SDK versions — which CAN affect mangling if the resolved type is XPact-mapped (e.g., `string.Format` vs `string.Format` with new params).

**Fix.** Pin the .NET SDK version in the manifest's `TargetInfo` and include it in:
  - The cache-key SHA-256 in §3.4 (currently only `XPact_CSharp_BCL_NuGet_version` is hashed; that's the *XPact-published* BCL surface but not the underlying .NET SDK).
  - A static_assert in every emitted `.cs.cpp`: `static_assert(XPACT_DOTNET_SDK_VERSION_MAJOR == 8 && XPACT_DOTNET_SDK_VERSION_MINOR >= 0)` or analogous, so a runtime built against a different SDK fails the compile.

**Severity.** HIGH — silent overload resolution drift is hard to debug.

---

### FIX-C-MED-03 — Mangling does not address the C# source file path (it correctly excludes it but never says so)

**Offending Rev 1 quote (§8.1):**

> Lists the inputs to mangling; file path is absent.

**Bug.** File paths *would* break determinism across user checkout locations, so it's correct that they're excluded. But the absence in the doc could let a future implementer include them naively. Critically related to the Contract §1.4 file-ID scheme — that scheme correctly decouples ID from filesystem path (Contract §1.4 Rev 11 audit-finding #3). XIL2CPP's mangling per Contract §2.2 doesn't reference Contract §1.4 (file IDs are for XHT-emitted reflection scaffolding, not XIL2CPP-emitted symbols). The doc should explicitly note this.

**Fix.** Add to §8.1: "File path (the absolute or repo-relative path of the C# source file) does NOT participate in mangling. This decouples symbol identity from filesystem layout; moving a `.cs` file across folders does not change mangling, and two developers with different repo paths produce byte-identical symbols (provided their Roslyn / .NET SDK versions match)."

**Severity.** MEDIUM — defensive documentation.

---

## Section 2 — Vtable layout stability (§8.2)

### FIX-C-HIGH-05 — Vtable-slot stability claim is inconsistent with the FakeVTable / function-pointer-table pattern XCoreXObject uses

**Offending Rev 1 quote (§1.5 line 103 + §8.2 line 2153 + §5.2 line 1076):**

> §1.5 "No virtual methods on the FXObjectLifecycleTable surface (PostInitProperties / BeginDestroy / etc.); dispatch is through the function-pointer table per XCoreXObject Rev 4's FakeVTable pattern."
> §8.2 "Adding a new virtual method to an XClass mid-session is rejected by XLiveCoding's Phase 1 layout-drift gate (per FIX-A-HIGH-14 in XCoreXObject Rev 4)."
> §5.2 line 1076: "XPact uses an FClass-driven dispatch (per the FakeVTable pattern) rather than a C++ vtable."

**Bug.** The doc conflates two distinct dispatch mechanisms:
  1. The XObject **lifecycle-table dispatch** (PostInitProperties, BeginDestroy, etc.) — function-pointer table in `.rodata`, no C++ vtable. This IS the FakeVTable pattern.
  2. The C#-source-level **virtual method dispatch** (user-author's `public virtual void Tick(float dt) { ... }`) — §5.2 shows it emitted as a "per-method dispatcher (free function)" that does `cls->VirtualMethods[ActorTickSlotIndex]` lookup. That is ALSO a function-pointer table dispatch, but it's a *different* table.

The two tables have different layout-stability constraints:
  - Lifecycle table: fixed 8 slots per XCoreXObject §2.4; index assignment is by enum (Z_PostInitProperties_slot=0, Z_BeginDestroy_slot=1, etc.). Layout is locked at engine version.
  - User-virtual-method table: per-FClass, sized by the count of virtual methods declared in the class. Adding a virtual method changes the table's size and slot assignments — but the SLOT INDEX of any pre-existing method must be stable for hot-reload to work.

The doc never specifies how `ActorTickSlotIndex` is computed deterministically across hot-reload. If the slot indices are assigned by source-declaration order (the only deterministic option), adding a virtual method to a base class shifts every derived class's slot indices — instant ABI break. If they're assigned by mangled-name lexicographic order (the alternative), the same property holds.

**Fix.** Specify §8.2 explicitly:
  - The C#-source-level virtual method table per FClass is layout-frozen mid-session: adding, removing, or reordering virtual methods is rejected by XLiveCoding's Phase 1 layout-drift gate.
  - The slot index for each virtual method is computed at FClass construction time as: (a) walk the inheritance chain from the topmost XObject down; (b) for each class, enumerate the class's directly-declared virtual methods in source-declaration order; (c) assign sequential indices.
  - The slot indices are emitted by XIL2CPP as `static constexpr size_t ActorTickSlotIndex = N;` in the `.cs.h` for the declaring class; the dispatcher references this constant.
  - A patch that adds a new virtual method fails the layout-drift gate with diagnostic `XIL2CPP145 — vtable slot count changed; class layout drift forbidden`.

**Severity.** HIGH — without this, hot-reload of any class with virtual methods would silently break.

---

### FIX-C-HIGH-06 — Multi-interface implementation layout is undefined

**Offending Rev 1 quote (§5.1 line 919-937):**

> ```
> [XInterface]
> public interface IDamageable
> {
>     void TakeDamage(float amount);
>     bool IsAlive { get; }
> }
> 
> // C++ output:
> class IDamageable {
> public:
>     virtual void TakeDamage(float amount) = 0;
>     virtual bool IsAlive() const = 0;
>     virtual ~IDamageable() = default;
> };
> ```

**Bug.** The doc shows C# interfaces as C++ pure-virtual classes. C++ pure-virtual classes have their own C++ vtables. A C# class implementing two interfaces (e.g., `class Foo : XObject, IDamageable, ISerializable`) emits a C++ class with TWO vtables (one per interface). Layout-stability of multi-interface implementations is unaddressed.

C++ multiple-inheritance vtable layout differs between MSVC and Itanium ABI:
  - MSVC: each base class has its own subobject with its own vtable pointer.
  - Itanium (Clang/GCC): primary base's vtable is shared; secondary bases get adjusted vtables.

The doc claims cross-platform deterministic emit per §1.7 (criterion c) but never addresses multi-inheritance vtable layout drift across compilers.

Worse: §5.2 says "XPact uses an FClass-driven dispatch (per the FakeVTable pattern) rather than a C++ vtable." This contradicts §5.1's interface emit. Which is it?

**Fix.** Resolve the contradiction:
  - Option A: All dispatch is FakeVTable. Then C# interfaces emit as pure-data interfaces (no C++ virtual methods); the dispatcher is per-method, per-interface. Each interface has its own FInterface descriptor with a function-pointer table; a class implementing N interfaces has N FInterface↔method-pointer maps; dispatch is `cls->Interfaces[interfaceIdx]->Methods[methodIdx](self, args)`. This is consistent with the rest of the doc.
  - Option B: C# interfaces use C++ virtual methods; lifecycle uses FakeVTable. This is what §5.1 shows, but then §5.2's "no C++ vtable" claim is false, and multi-inheritance ABI cross-platform variance becomes a real concern.

Recommend Option A (consistent with FakeVTable pattern + cross-platform determinism). Update §5.1 interface emit to show the FInterface-dispatcher pattern. Specify the layout-drift rule: adding or removing an interface implementation is a class-layout change rejected by the hot-reload gate.

**Severity.** HIGH — design contradiction; one or the other must give.

---

### FIX-C-MED-04 — Sizeof / vtable size pins missing for FInterface

**Offending Rev 1 quote (§9.7):**

> Lists 15 layout-tag content pins + 14 sizeof pins from Contract §14.1-14.2, but the list as quoted in §9.7 is selective; it omits `XPACT_FINTERFACE_LAYOUT_TAG` and the matching sizeof pin.

**Bug.** Contract §14.1 includes `XPACT_FINTERFACE_LAYOUT_TAG` → `"FInterface-v4: 64 bytes; ..."` and §14.2 includes `sizeof(FInterface) == 64`. The XIL2CPP §9.7 list omits both. If XIL2CPP-emitted code emits a `static_assert` for `XPACT_FINTERFACE_LAYOUT_TAG` (which it should per the cross-tool symbol contract since C#-declared interfaces produce FInterface descriptors XIL2CPP consumes), the omission means runtime ABI drift on FInterface is undetected at emit time.

**Fix.** Update §9.7 to enumerate the FULL 15-tag + 14-sizeof list from Contract §14.1-14.2, not a curated subset. Reference Contract §14.1 + §14.2 by section number rather than restating; the spec should not maintain the list twice.

**Severity.** MEDIUM — partial drift detection at runtime ABI.

---

## Section 3 — Sub-pool rebind cooperation (§8.4)

### FIX-C-CRIT-02 — OnClassReplaced listener leak on module unload

**Offending Rev 1 quote (§8.4 line 2169-2189):**

> ```
> extern "C" const ::XCore::Reflect::FClass* Z_Construct_FClass_MyModule_XHealthPickup() noexcept
> {
>     static const ::XCore::Reflect::FClass* sCachedClass = []() noexcept {
>         const auto* cls = &XHealthPickup_Class;
>         ::XCore::Reflect::XReflectionRuntime::RegisterClass(cls);
> 
>         // Listener registration for OnClassReplaced:
>         ::XCore::Delegates::OnClassReplaced.AddRaw(
>             [](const ::XCore::Reflect::FClassReplacementContext& ctx) {
>                 if (ctx.OldClass == cls) {
>                     // Invalidate any cached FClass* in this TU's scope.
>                     // (For non-generic XClass, the static const FClass is .rodata-resident
>                     //  and not replaced; for generic instantiations, the cache is per-emit-site.)
>                 }
>             });
> 
>         return cls;
>     }();
>     return sCachedClass;
> }
> ```

**Bug.** Three independent bugs:

  1. **No `RemoveRaw` matching the `AddRaw`.** When the module DLL unloads (which IS supported, per XCoreXObject Rev 4 §10.13 listener lifecycle discipline), the captured `cls` pointer becomes invalid AND the lambda's code lives in the DLL's `.text` segment which is also unloaded. The OnClassReplaced delegate retains the dangling function-pointer + dangling capture. The next cascade fires through the dangling pointer and crashes.
  2. **`AddRaw` is a UE convention for non-checked function-pointer delegate registration**, which gives no automatic deregistration on module unload. The conservative XCoreXObject Rev 4 pattern (per §10.13 `IXObjectCreateListener` interface) is `Add(IListener*)` with explicit `Remove(IListener*)` at module unload. The doc uses the unsafe pattern.
  3. **The body of the lambda is empty (TODO).** It says "Invalidate any cached FClass* in this TU's scope" as a comment, but actually does nothing. The forward-commitment is unimplemented. If the FClass cache is unspecified, the cache invalidation cannot be tested.

**Fix.**
  - Replace `AddRaw` with the typed `IXObjectClassReplacedListener` interface (analogous to `IXObjectCreateListener` per XCoreXObject §10.13). The interface has a virtual `OnClassReplaced(const FClassReplacementContext&)` method.
  - Emit a per-module static listener object that implements the interface; the listener registers in the module's static initializer and deregisters in the module's module-shutdown hook (the same hook XCoreXObject uses for its allocator-deregistration).
  - Specify: the listener's lifetime is bounded by the module's DLL lifetime. XLiveCoding's quiesce protocol must ensure all listeners are deregistered before module unload AND re-registered after module load.
  - Specify what the listener actually does: enumerate the module's per-emit-site FClass* caches (the doc references `generic instantiation FClass cache` but never defines where the cache lives). The cache MUST be a known data structure XIL2CPP emits per-TU; the listener walks it and invalidates entries matching the replaced FClass.
  - Add diagnostic `XIL2CPP032 — module unload while OnClassReplaced listener still registered; potential dangling-pointer hazard` (this is a runtime check, not compile-time).

**Severity.** CRITICAL — this is the exact bug class that XCoreXObject Phase 5.k shipped Phase 5.k for; XIL2CPP cannot regress it.

---

### FIX-C-HIGH-07 — `RebindClassPool(OldClass, NewClass)` cooperation not specified for static-field references

**Offending Rev 1 quote (§8.4):**

> "Per-FClass sub-pool rebind cooperation: XIL2CPP-emitted Z_Construct_FClass_* functions register listeners for the per-FClass sub-pool rebind event (FIX-A-MIN-40 in XCoreXObject Rev 4) and do not assume the old FClass*-to-cell-pool mapping survives the patch."

**Bug.** §8.4 says the singleton-getter does not assume the old FClass*-to-cell-pool mapping survives. But what about *static field references* to instances of OldClass? E.g.:

```csharp
public static class GlobalRegistry {
    public static List<HealthPickup> ActivePickups = new();
}
```

When `HealthPickup` is replaced, the `ActivePickups` list contains pointers to instances whose FClass* is now NewClass — but the static field's emitted XGCRootSpan was registered with the *old* class's stride / layout (which is unchanged per the no-class-layout-change commitment, so this is actually safe), HOWEVER the FClass cache that the list's container uses internally to dispatch reflection-aware operations IS bound to the old FClass. The cache invalidation listener (FIX-C-CRIT-02) needs to know about static-field-held containers, not just per-emit-site FClass caches.

**Fix.** Specify §8.4: "Static field declarations of containers parameterized over `[XClass]`-attributed types MUST register the container's FClass cache invalidator with the OnClassReplaced delegate. The invalidator handle is emitted as a static initializer co-located with the field declaration. The handle's lifetime is the module's lifetime (per FIX-C-CRIT-02 deregistration discipline)."

**Severity.** HIGH — static-field-held containers are a common pattern; silent breakage.

---

### FIX-C-HIGH-08 — Generic instantiations cache does not specify the invalidation key

**Offending Rev 1 quote (§8.4):**

> "(For non-generic XClass, the static const FClass is .rodata-resident and not replaced; for generic instantiations, the cache is per-emit-site.)"

**Bug.** "Per-emit-site" is hand-wave. Specifically: the closed-instantiation walk at Pass 3 produces N closed instantiations (e.g., `List<XActor>`, `List<XHealthPickup>`, `Dictionary<FName, XActor>`). Each closed instantiation has its own FClass emitted somewhere (per Contract §2.14, XIL2CPP emits one FClass per closed instantiation). When `XHealthPickup` is replaced:
  - The `List<XHealthPickup>` FClass should be invalidated AND re-instantiated against NewClass.
  - The `Dictionary<FName, XHealthPickup>` FClass should be invalidated AND re-instantiated.

What does "invalidation" mean here? The doc says the `.rodata`-resident FClass for non-generic XClass is "not replaced". But the closed-generic FClass might also be `.rodata`-resident — if so, the same logic applies (not replaced). If the closed-generic FClass is dynamically allocated (heap-resident), then it IS replaced. The doc never says which.

If `.rodata`-resident: nothing to invalidate; the FClass's internals (e.g., its `Inner` FProperty for `List<XHealthPickup>`) point at the OldClass FClass and need to be migrated.

**Fix.** Specify §8.4:
  - Closed-generic FClass instances are `.rodata`-resident (in the per-emit-site translation unit).
  - The FClass's internal references to other FClass instances (e.g., `Inner` FProperty for `List<T>` pointing at `T`'s FClass) are pointers, not stable across replacement.
  - On `OnClassReplaced(OldClass, NewClass)`, every closed-generic FClass that has `OldClass` somewhere in its argument tree must rewrite its internal pointers atomically (using `std::atomic_ref` overlay, analogous to XCoreXObject Phase 5.j's ClassPrivate rebind).
  - XIL2CPP emits per-closed-instantiation invalidation hooks at module load (registered with OnClassReplaced) that walk the closed instantiation's FProperty tree and update pointers.

**Severity.** HIGH — generic-collection-heavy gameplay code (every entity manager) breaks otherwise.

---

### FIX-C-MED-05 — Static field with XObject reference: lifecycle on module unload not specified

**Offending Rev 1 quote:** None — the doc never addresses static C# fields holding XObject references.

**Bug.** C# allows `public static SomeXObject MyStaticRef;`. When the declaring module unloads, the FClass for the static field's type goes away — but the XObject instance is still in the FXObjectArray, still marked, and may still be referenced from other modules. The static field's value (an XObject*) becomes a dangling reference from the perspective of the C# code (no code can read it once the module unloads), but the underlying XObject is still alive in the heap until the GC reaps it.

The doc never specifies the unload protocol for static XObject references. Does the static field's destructor (which would mark the object for kill, releasing the reference) fire on module unload? Does the field's XGCRootSpan unregister on module unload?

**Fix.** Specify §6 + §8: "Module unload protocol for static XObject references: every static C# field of XObject-derived type emits an unregistration hook that fires on module-unload. The hook unregisters the field's XGCRootSpan (if any) from the GC and releases the reference (the XObject becomes GC-collectable on next sweep). Module unload happens *after* the OnClassReplaced delegate cycle completes, not during."

**Severity.** MEDIUM — silent reference leak on module unload.

---

## Section 4 — OnClassReplaced delegate listener registration (§8.4)

### FIX-C-HIGH-09 — Listener registration uses wrong canonical accessor

**Offending Rev 1 quote (§8.4):**

> "::XCore::Delegates::OnClassReplaced.AddRaw(...)"

**Bug.** Per XCoreXObject Rev 4 §10.10.1 and §1.4, the canonical accessor is via XCoreDelegates (the system-level delegate registry), not a bare `::XCore::Delegates::OnClassReplaced` symbol. The actual accessor (per the production code in `Engine/Source/Runtime/XCore/Private/XObject/XCoreDelegates_OnClassReplaced.cpp`) is something like `::XCore::CoreDelegates::GetOnClassReplaced()` (returns the multicast delegate reference). The doc invents a symbol that doesn't exist.

**Fix.** Use the canonical accessor: `::XCore::CoreDelegates::GetOnClassReplaced().Add(...)` (or whatever the actual production-code shape is — Round 1 audit cannot finalize until XCoreDelegates Rev N+1 ships, but the doc should reference the actual accessor, not invent one). Add a forward commitment to update the doc when XCoreDelegates lands.

**Severity.** HIGH — code as written references a non-existent symbol.

---

### FIX-C-MED-06 — `[XOnClassReplaced]` attribute auto-registration not specified

**Offending Rev 1 quote:** None — the doc never mentions an attribute-driven hook for user-author OnClassReplaced listeners.

**Bug.** UE has a pattern: developers can mark a method `[UFUNCTION(CallInEditor, ...)]` and the framework auto-registers it. XPact should support an analogous pattern: a user-author can mark a method `[XOnClassReplaced]` and XIL2CPP auto-registers it as a listener of the OnClassReplaced delegate, scoped to the declaring class.

The audit-prompt explicitly asks: "If a class has `[XOnClassReplaced]` attribute on a method, does XIL2CPP auto-register? Auto-deregister on module unload?" — the doc has no answer.

**Fix.** Specify §8.4: 
  - `[XOnClassReplaced]` attribute on a static or instance method auto-registers it as an OnClassReplaced listener at module load.
  - The method signature MUST be `void M(const FClassReplacementContext& ctx)` or compile-time error `XIL2CPP033`.
  - The registration uses the per-module typed listener (per FIX-C-CRIT-02) that owns lifetime; the user-author's method becomes one of N callbacks the listener dispatches.
  - Auto-deregister on module unload (handled by the per-module listener's destructor).

**Severity.** MEDIUM — user-facing ergonomic feature; not strictly required for MVP but commonly needed for hot-reload-aware classes.

---

## Section 5 — XBT pipeline integration (§9)

### FIX-C-CRIT-03 — In-process mode resource lifecycle is unspecified

**Offending Rev 1 quote (§3.5 + §9.5):**

> §3.5 "The `CancellationToken` is honored at every Pass boundary; if cancellation is requested mid-Pass-6, XIL2CPP cancels and emits no partial output (so the next invocation doesn't see half-written files)."
> §9.5 "Per Constraint §7.10 + Q27 resolution: XBT may invoke XIL2CPP in-process via `IXIL2CPPInProcess.TranspileModuleAsync` (signature in §3.5). The cancellation token is honored at every Pass boundary."

**Bug.** Three unspecified behaviors:
  1. **Mid-build XBT hot-reload (if Foundation Prototype criterion d ships XBT hot-reload too).** If XBT itself is the target of a hot-reload mid-build, the in-process XIL2CPP's static state (Roslyn workspace, BCL reference DLLs) is in the XBT process's heap. The hot-reload migration protocol doesn't address this.
  2. **Roslyn workspace caching.** The doc says nothing about whether the Roslyn workspace is per-call, per-process, or shared across calls. Per-call is correct (deterministic cache key) but expensive (5+ seconds to build a workspace); per-process is fast but introduces invalidation hazards if a dependency module's `.refonly.dll` changes between calls. The doc must commit to one.
  3. **In-process memory leaks.** Multi-invocation in-process mode requires explicit reset between invocations. The signature `TranspileModuleAsync(...)` is async but never specifies the reset hook. If the implementation accumulates per-call state (e.g., a per-call Roslyn syntax tree allocator that wasn't released), the XBT process's memory grows monotonically.

**Fix.** Specify §3.5 + §9.5:
  - Roslyn workspace: **per-call**, cached for the duration of the call only. Disposal at call boundary. Per-call cost is ~50-200 ms for a small module; acceptable.
  - Add a `ResetAsync()` method to `IXIL2CPPInProcess`. The method releases all per-call state, frees all referenced reference-DLLs, and resets the per-process invocation counter. XBT MUST call this between every pair of invocations.
  - Specify: XBT itself MUST NOT hot-reload mid-XIL2CPP-call. The XBT hot-reload bracket (BeginXBTHotReloadQuiesce / FinishXBTHotReloadCascade) drains all in-flight XIL2CPP calls first; XIL2CPP calls block at the entry-point if a quiesce is in progress.
  - Cancellation: specify that cancellation between Pass boundaries leaves a fully-cleaned state (no partial outputs, no dangling Roslyn workspace, no leaked memory). The CancellationToken's `IsCancellationRequested` is polled at every back-edge in Pass 6 (not "at every Pass boundary" — that's too coarse for a per-module call that may take minutes).

**Severity.** CRITICAL — in-process mode is the prefered MVP path; resource lifecycle is the single biggest correctness-and-perf concern.

---

### FIX-C-CRIT-04 — `ReferenceCompileCSharpAction` ordinal 14 conflicts with XBT Rev 10 reserved slot

**Offending Rev 1 quote (§9.8):**

> "Rev 1 specifies a new XBT action: **ReferenceCompileCSharpAction** (XActionType ordinal 14, post-Rev-13.9 contract extension)."

**Bug.** XBT Rev 10 §5.1 explicitly defines slot 14 as `Reserved_Phase2_F` (reserved for unallocated Phase 2 use). Allocating ordinal 14 to ReferenceCompileCSharpAction unilaterally from XIL2CPP's spec, without coordinating with XBT's Rev 11+ action enum, would:
  1. Conflict with `Reserved_Phase2_F` (currently empty but the slot is reserved for future XBT-side allocation).
  2. Break the `CommandVersion` schema-digest determinism XBT relies on (XBT §5.1: "adding a new value to the enum changes the hash, which would invalidate ActionHistory across the entire engine"). XIL2CPP can't unilaterally add a value.
  3. Tier2WholeProgramPass already occupies slot 13 (per XBT Rev 10 §5.1 Rev 4 line 781). Slot 14 is the next un-allocated; slot 15 is `Reserved_Phase2_G`. The XIL2CPP doc claims slot 14 without coordination.

**Fix.** Move ReferenceCompileCSharpAction to slot 14 by AMENDING XBT's spec — this is a Contract change requiring XBT Rev 11. The amendment must:
  - Reserve a slot for ReferenceCompileCSharpAction in XBT §5.1.
  - Document the prerequisite chain: ReferenceCompileCSharpAction(M_dep) must complete BEFORE XIL2CPPAction(M) for any M that depends on M_dep.
  - Add the cache-key components (the `.refonly.dll` produced by ReferenceCompileCSharpAction is an input to XIL2CPPAction's cache key — currently §3.4 doesn't list it).
  - Add a §14 audit-item: "Q-REFCOMPILE — confirm XBT slot allocation".

Alternative: defer ReferenceCompileCSharpAction to post-MVP and have Phase 1 MVP parse the dependency module's `.cs` files directly (lazy parse, with caching). The trade-off is per-call parse overhead vs. inter-tool coordination cost. Phase 1 simplicity favors the latter.

**Severity.** CRITICAL — unilateral cross-tool ABI break.

---

### FIX-C-HIGH-10 — Cache key omits compiler flags

**Offending Rev 1 quote (§3.4 line 508-531):**

> Cache key includes ContractVersion, Module_name, sim_path flag, simd_level, pch_usage, per-target settings JSON, but NOT compiler flags.

**Bug.** XIL2CPP emits `.cs.cpp` files that the C++ compiler consumes. The C++ compiler's flags (e.g., `-O2`, `-fno-rtti`, `-fPIC`, `--target=aarch64-linux-android`) affect the compiled output but not the emitted `.cs.cpp` content. So the cache key shouldn't depend on compiler flags. BUT:
  - XIL2CPP-emitted `static_assert`s might check macros that are compiler-flag-dependent (e.g., the `XPACT_FP_SEMANTICS_TAG` macro is defined by the per-target settings).
  - The reproducibility forwarding from XBT (§9.9: "predicating `#line` directives so cross-machine line-number consistency holds") DOES depend on platform flags.

So the per-target settings macro propagation IS part of the cache key (via "per-target settings JSON"). But is the full reproducibility envelope flag set? The doc says "per-target settings struct serialized JSON" but doesn't enumerate.

**Fix.** Expand §3.4:
  - Enumerate every per-target setting hashed: `SimPathConservativeRootsAllowed`, `FipsMode`, `ContractVersion`, `Platform`, `SimdLevel`, `FpSemantics`, every reproducibility flag (`/Brepro`, `-fno-ident`, etc.), the `pathmap` / `--remap-file` setting.
  - Address the question: if the C++ compiler's flags change but XIL2CPP's output doesn't, is a cache hit still safe? Yes (the output is byte-identical) but the doc should say so explicitly.

**Severity.** HIGH — cache poisoning under flag changes.

---

### FIX-C-HIGH-11 — Pass 1 / Pass 2 schema outputs not differentiated

**Offending Rev 1 quote (§3.3):**

> Pass 1 emits `TierTable.partial.<Module>.json` (schema shown). Pass 2 emits "merged `TierTable.json`". The two schemas are presumed identical, but the merged schema must capture the cross-module promotions.

**Bug.** The Pass 1 schema includes per-function `tier`, `reason`, `calleesUnverified[]`. The merged Pass 2 schema must additionally capture the *promotion path* (which Pass 2 callees were proved NoThrow). The doc doesn't show the merged schema; if the merged schema is identical to Pass 1 (no `promotedFrom`, no `proofChain`), then debugging Pass 2 is impossible.

Also: Pass 1 schema has `"schemaVersion": 1` — Pass 2 should have its own version, or the schema must be union-compatible (a Pass 2 file should be readable as a Pass 1 file if needed).

**Fix.** Specify the Pass 2 merged-table schema:
```json
{
  "$schema": "https://xpact.dev/schemas/TierTable.merged.v1.json",
  "schemaVersion": 1,
  "contractVersion": "...",
  "modules": ["..."],
  "functions": [
    {
      "stableId": "...",
      "tier": "Tier2",
      "reason": "Pass 2 promotion: cross-module callee Simgenics.X.RecordY proved NoThrow",
      "pass1Tier": "Tier1",   // For debugging
      "promotedBy": "Pass 2 cross-module proof"
    }
  ]
}
```
The merged schema bumps `schemaVersion` if it adds new top-level fields beyond Pass 1.

**Severity.** HIGH — Pass 2 debugging is impossible without the differentiation.

---

### FIX-C-MED-07 — Cancellation token signature unspecified for the actual entry-point shape

**Offending Rev 1 quote (§3.5):**

> ```
> Task<XIL2CPPResult> TranspileModuleAsync(
>     ManifestRef manifest,
>     FlatBuffersBuffer manifestBin,
>     string moduleName,
>     XIL2CPPMode mode,
>     TierTableMerged? mergedTierTable,
>     CancellationToken cancellation);
> ```

**Bug.** The signature is incomplete:
  - `ManifestRef` and `FlatBuffersBuffer` types are undefined elsewhere in the doc.
  - The return is a `Task<XIL2CPPResult>` (async); but XIL2CPP's transpilation is purely CPU-bound (no I/O outside of file writes). Should it be `Task` (truly async) or should it be `ValueTask` (synchronous when possible)?
  - `XIL2CPPResult` is described later (§3.5) but never lists where output files are written; only that `ExitCode` is returned. If the in-process call wrote files to disk, the result must report the file paths — and §3.5 shows `Outputs: IReadOnlyList<TranspilationOutput>` does this. OK.
  - No `IServiceProvider` or `ILogger` is plumbed through; tools that integrate XIL2CPP in-process need to capture log output. The signature must support this.

**Fix.** Tighten §3.5:
  - Add types for `ManifestRef` (presumably a wrapper around the path + cached parse?) and `FlatBuffersBuffer` (a span over the binary sidecar's bytes).
  - Add an `ILogger` parameter (or include in `IXIL2CPPInProcess` as a property settable per-call).
  - Specify the cancellation token's polling cadence: "every back-edge in Pass 6 and every call-graph-edge in Pass 4 + Pass 5".
  - Add an explicit `ValueTask` vs `Task` decision; `Task` is fine but the doc should commit.

**Severity.** MEDIUM — interface contract is fuzzy.

---

### FIX-C-HIGH-12 — Exit code 24 collision risk

**Offending Rev 1 quote (§9.6):**

> "Per `XBT.html` §3.2 line 258: Phase 1 stub returns exit code 24 (`PluginNotFound`); Phase 6 (full XIL2CPP) returns the codes above."

**Bug.** Per Contract §13.1, exit code 24 is `PluginNotFound` — a generic XBT code that can also be emitted by XBT itself for legitimate plugin-not-found scenarios. The Phase 1 stub emitting 24 is a deliberate placeholder, but it COLLIDES semantically with the legitimate XBT use. In CI, a build can fail with exit 24 from EITHER:
  - XIL2CPP stub (Phase 1 placeholder; XIL2CPP not implemented yet) — benign, expected.
  - XBT itself finding a missing plugin — real failure.

The build system can't distinguish them.

**Fix.** Per Contract §13.3 ("XBT, XHT, and XIL2CPP each emit only the codes their column allows"), exit 24 is allocated to XBT only. XIL2CPP must emit only 61, 63. The Phase 1 stub should emit 61 with a custom message "XIL2CPP Phase 1 stub: full implementation not yet shipped". XBT-side aggregation: the stub's 61 + Phase 1-stub-marker is treated as a "deferred build" not "build failure". The doc must commit to this.

Alternative: introduce a new exit code 25 = `Phase1StubReturn`, allocated to all Phase-1-stubs. This requires Contract §13 amendment.

**Severity.** HIGH — silent CI false-positive risk during the Phase 1 → Phase 6 transition.

---

### FIX-C-MED-08 — Phase 2 mode invocation doesn't specify cache-key isolation

**Offending Rev 1 quote (§3.3 + §3.4):**

> Pass 2 mode re-invokes XIL2CPP per module with `-WholeProgramTier2` flag + merged tier table input. Cache key adds the merged tier table hash.

**Bug.** What happens if Pass 1 and Pass 2 emit different cache keys for the SAME module's outputs? The doc says Pass 2 only re-emits affected functions (deltas) — but the per-module action graph has ONE action per module per mode. Both modes would produce the same output file paths. Without disambiguation, the action cache could conflate Pass 1 and Pass 2 outputs.

**Fix.** Specify: Pass 1 mode and Pass 2 mode produce outputs in different directories:
  - Pass 1: `Intermediate/Build/<Target>/<Configuration>/<Module>/Transpiled/Pass1/`
  - Pass 2: `Intermediate/Build/<Target>/<Configuration>/<Module>/Transpiled/Pass2/`

The compiled C++ for the final binary uses Pass 2 outputs when Pass 2 mode is active; otherwise Pass 1. XBT's CompileCppAction prerequisite list selects the correct path.

**Severity.** MEDIUM — silent build-cache corruption.

---

### FIX-C-MED-09 — Pass 2 NEVER demotes Tier 2 — but what about a previously-Tier-2 function becoming a [CanThrow] in the consuming module?

**Offending Rev 1 quote (§3.3):**

> "Critical guarantee. Pass 2 NEVER demotes a function from Tier 2 to Tier 1. Demotion would break ABI (the function's signature would change). Pass 2 only promotes."

**Bug.** What if a function's `[XFunction(NoThrow = true)]` annotation is REMOVED in a hot-reload? The function was previously Tier 2 (Pass 1 + Pass 2 promotion); now without the annotation, the noexcept proof fails. Pass 1 of the next build would demote it to Tier 1. But the running game has the Tier 2 form linked. The hot-reload would fail at patch validation.

The "Pass 2 only promotes" rule applies only within a single build; across builds (hot-reload), demotion-via-removed-annotation is possible and breaks hot-reload.

**Fix.** Specify §3.3 + §8: 
  - Removing `[XFunction(NoThrow = true)]` from a Tier 2 function is a SIGNATURE change (the function's tier changes). It is rejected by XLiveCoding's Phase 1 layout-drift gate.
  - Add diagnostic `XIL2CPP035 — Tier 2 → Tier 1 demotion across hot-reload; function 'Foo' was Tier 2 in baseline build, Tier 1 after annotation removal`.
  - The diagnostic is emitted by XLiveCoding (not XIL2CPP directly), but XIL2CPP must emit a `.tier_baseline` per-build sidecar that XLiveCoding consults at validation time.

**Severity.** MEDIUM — corner-case hot-reload break.

---

## Section 6 — Module manifest contract — backwards compatibility (§9.7)

### FIX-C-HIGH-13 — Manifest schema-version field placement not specified

**Offending Rev 1 quote (§9.7):**

> Lists the 15 layout-tag content pins + sizeof pins; uses them as `static_assert`s.

**Bug.** The doc never specifies WHERE in the manifest schema the schema version lives. Per Contract §10.2 line 1907-1922 (`TargetInfo` table), the FBS schema includes `gc_root_abi`, `exception_abi`, `mangling_scheme` strings. The schema-version of the manifest itself (the FBS schema version, NOT the contract version) is implicit in the `file_identifier "XMFT"` but isn't a numeric field.

When the manifest schema rolls (e.g., a new field added to `TargetInfo`), XIL2CPP must reject manifests from older schemas:
  - Either loudly (with a diagnostic) — preferred.
  - Or silently produce wrong output — current behavior, since the doc doesn't say.

The doc has no specified rejection rule.

**Fix.** Specify §9.7:
  - Manifest schema version is a `schema_version: uint32` field on the top-level `Manifest` table, separately from `contract_version: string`.
  - XIL2CPP compares the manifest's `schema_version` against its supported set (a const list in XIL2CPP's binary). Mismatch emits `XIL2CPP141 — Manifest schema version V is older than XIL2CPP supports (minimum: W); rebuild XBT with the matching schema`. Build exit 41.

**Severity.** HIGH — silent data corruption when schema rolls.

---

### FIX-C-MED-10 — Supported-set for each ABI tag not enumerated

**Offending Rev 1 quote (§9.7):**

> Static-asserts the tag CONTENT (e.g., `XPACT_GC_ROOT_ABI_TAG = "Span-based v1"`), but doesn't enumerate the supported set for each tag.

**Bug.** When a future contract revision rolls (e.g., adds a `"v2"` for `XPACT_GC_ROOT_ABI_TAG`), XIL2CPP must know which versions it supports. Per Contract §13.2, exit codes are stable across versions; ABI tags presumably follow the same discipline but the doc doesn't say. If a manifest declares `gc_root_abi = "v2"` and XIL2CPP only knows v1, what happens?

**Fix.** Specify §9.7 + a XIL2CPP-side supported-set table:
  - `gc_root_abi` supported set: {"Span-based v1"} (Phase 1).
  - `exception_abi` supported set: {"Tier1-Shim/Tier2-Direct"}.
  - `mangling_scheme` supported set: {"Itanium-LengthPrefixed-v1"}.
  - And so on for the 15 layout tags.
  
  A manifest declaring a tag value not in XIL2CPP's supported set emits `XIL2CPP140 — Manifest declares unknown ABI envelope tag value '<tag>=<value>'; XIL2CPP supported: <list>`. Build exit 41.

**Severity.** MEDIUM — version skew handling is currently silent.

---

### FIX-C-MED-11 — Stage A vs Stage B layout drift on FProperty descriptor

**Offending Rev 1 quote (§10.2):**

> Lists XHT-emitted metadata XIL2CPP reads: FClass descriptors, FProperty descriptors, NoThrow annotation, FXObjectRefSchemaOp, FXObjectLifecycleTable.

**Bug.** Per XHT.html §17.3 line 1584 + Contract §7.1 line 1162: "STAGE B: the exact byte layout of the cross-language reflection metadata ... freezes at Step 4 with the rest of the FProperty surface. Phase 1 emits provisional layouts; XIL2CPP's transpiler honours the provisional shape via the schema-version field in the descriptor."

XIL2CPP.html doesn't address how XIL2CPP handles the Stage A vs Stage B transition for the FProperty descriptor it consumes. If XHT emits a Phase 1 (provisional) FProperty descriptor, XIL2CPP must NOT hard-code its byte layout — it must consume via the schema-version field.

The Rev 13.8 contract has now frozen layouts (the Stage B addendum landed). So actually the question is: is XIL2CPP authored against the frozen layouts (Stage B) or the provisional ones (Stage A)? The doc's `static_assert`s in §9.7 imply Stage B.

**Fix.** Add a note in §10 that XIL2CPP at Rev 1 is authored against Stage B (Contract Rev 13.8) and the schema-version field in the FProperty descriptor is implicitly fixed at the Stage B value. The diagnostic `XIL2CPP146 — XHT-emitted FProperty descriptor schema version V mismatches XIL2CPP's supported value` catches inter-tool drift.

**Severity.** MEDIUM — version skew handling.

---

## Section 7 — NoThrow propagation (§3.3 + §3.5)

### FIX-C-HIGH-14 — Cross-module NoThrow lookup requires XHT-emitted reflection metadata to be available at XIL2CPP parse time, but the action graph doesn't guarantee this ordering

**Offending Rev 1 quote (§3.2 + §3.3):**

> §3.2 "Cross-module callee NoThrow lookup (Pass 1 mode). Every method call's resolved target is checked: if the target is in another module and the module's XHT-emitted reflection metadata carries `[XFunction(NoThrow = true)]`, the call is potentially Tier 2..."
> §3.3 "Cross-module callees: conservatively Tier 1 UNLESS the callee carries `[XFunction(NoThrow = true)]` annotation in its declaring module's XHT-emitted reflection metadata (which XIL2CPP can read at parse time, since XHT runs concurrent with XIL2CPP and the manifest references both module's outputs)."

**Bug.** §1.4 says XHT and XIL2CPP are concurrent peers in the action graph (Contract §10.1 line 1704-1713: "neither is prerequisite to the other"). So XIL2CPP(M) starts at the same time as XHTAction(M). For XIL2CPP(M)'s Pass 1 tier classification to read the *dependency module's* XHT-emitted NoThrow annotations, XHTAction(M_dep) must have completed BEFORE XIL2CPP(M) runs.

The doc claims this works because "XHT runs concurrent with XIL2CPP and the manifest references both module's outputs" — but for cross-module NoThrow, XIL2CPP(M) depends on XHTAction(M_dep) finishing, not XHTAction(M). This is a different ordering than peer concurrency.

Specifically: XIL2CPPAction(M)'s prerequisites must include EmitReflectionAction(M_dep) for every M_dep in M's module_dependencies. The doc §9.3 inputs list says XIL2CPP consumes "every dependency module's XHT-emitted reflection metadata" — but the action graph in §9.1 says only "concurrent with XHTAction / EmitReflectionAction for the same module". The dependency ordering is implicit, not specified.

**Fix.** Specify §9.1:
  - XIL2CPPAction(M)'s prerequisites:
    - WriteManifestAction (shared with all actions).
    - EmitReflectionAction(M_dep) for every M_dep in M's `module_dependencies`.
    - (NEW) ReferenceCompileCSharpAction(M_dep) for every M_dep with C# sources (per §9.8) — but see FIX-C-CRIT-04 about slot allocation.
  - XHTAction(M) and XIL2CPPAction(M) ARE peers for the same M; the prerequisite chain ensures cross-module knowledge is available.

This is also a documentation drift with XBT.html §10.6 ("the two run concurrently") — which is true for same-module peer relation only. Update both docs to clarify.

**Severity.** HIGH — without this, Pass 1 cross-module NoThrow lookup would silently fail (the dependency's XHT output isn't ready when XIL2CPP needs it).

---

### FIX-C-HIGH-15 — C++-implemented NoThrow functions: XIL2CPP reads via XHT-emitted metadata, but the discovery path is unspecified

**Offending Rev 1 quote (§3.2):**

> "Cross-module callee NoThrow lookup (Pass 1 mode). Every method call's resolved target is checked: if the target is in another module and the module's XHT-emitted reflection metadata carries `[XFunction(NoThrow = true)]`..."

**Bug.** The wording assumes the callee is in *another C# module*. What about a C# call to a C++ function (e.g., `XCore.HAL.Print("hello")` calling a C++-implemented function)? The C++ side has its own NoThrow annotation (per Contract §1.2: `XFUNCTION(NoThrow = true)`), emitted by XHT. XIL2CPP must consult the same NoThrow annotation table for cross-language callees.

The doc doesn't specify how the C++ NoThrow annotations are surfaced to XIL2CPP. Constraint inventory Q19 explicitly flags this; the doc references the Constraint inventory but doesn't resolve Q19.

**Fix.** Specify §3.3 + §10.2:
  - XHT emits NoThrow annotations for both C++-declared and C#-declared functions in the same per-module reflection metadata file.
  - XIL2CPP reads the metadata via the same accessor for both cases.
  - The metadata format: per-function entry in `<Module>.gen.manifest` includes `{symbol: string, no_throw: bool}` for every reflected function.
  - Diagnostic `XIL2CPP036 — NoThrow lookup failed; function 'Foo' has no XHT-emitted reflection entry` for unresolved cross-module callees.

**Severity.** HIGH — silent Tier 1 misclassification for C# → C++ calls.

---

### FIX-C-MED-12 — Transitive NoThrow proof failure: no compile error specified

**Offending Rev 1 quote (§3.2 + §3.3):**

> "f is Tier 2 iff ... (3) Every callee of f is itself Tier 2 (transitive); (4) f's body contains no `throw` expression; (5) f does not invoke a C++ function declared without `noexcept` that XIL2CPP cannot prove safe."

**Bug.** What if a `[XFunction(NoThrow = true)]`-annotated function transitively calls a non-NoThrow function? Per Contract §5.2: "XIL2CPP refuses if it cannot satisfy the noexcept proof." This is a compile error. The doc says §3.2 + §3.3 cover the case, but the diagnostic is `XIL2CPP030 — [XFunction(NoThrow = true)] proof failed` (per §12).

The diagnostic is correct, but the doc never specifies the *transitive proof chain*. If `A → B → C` where A is `[NoThrow]` and C throws, the diagnostic must point at the chain (`A's proof fails because A calls B; B's proof fails because B calls C which throws`). Without the chain in the diagnostic message, debugging is impossible.

**Fix.** Augment XIL2CPP030 emit:
  - Include the failure chain: `A's noexcept proof failed: A calls B (no NoThrow annotation; cross-module to module X). Resolving... B calls C (body contains throw statement at file:line:col).`
  - The chain is computed at Pass 4's fixpoint by the same call-graph the tier classification uses.

**Severity.** MEDIUM — usability for diagnostics.

---

## Section 8 — Conservative root span dedup cache (§6.6 + §14)

### FIX-C-MED-13 — Conservative dedup cache layout not specified

**Offending Rev 1 quote (§6.6 + §14 line 2531):**

> "Sim-path TUs emit warning `XIL2CPP070 — Conservative XGCRootSpan used; cross-arch determinism may be affected. Consider replacing object with a typed reference`. The warning is per-closed-type-instantiation, deduplicated via the XIL2CPP build cache."
> §14: "O-CONSERVATIVE-WARNING-DEDUP: Specify the dedup cache layout; verify it's stable across rebuilds."

**Bug.** The doc flags the dedup cache as an audit item but does not specify the design. Concretely:
  - Where does the cache live? Per-module? Per-build?
  - When does it invalidate? On any tier-table change? On any C# source edit?
  - Multi-module parallel build: if two `XIL2CPPAction(M1)` and `XIL2CPPAction(M2)` both encounter the same closed-type instantiation (`List<XActor>`), do they each emit the warning, or does one deduplicate against the other?

**Fix.** Specify §6.6:
  - The dedup cache is **per-module**, written to `Intermediate/Build/<Target>/<Configuration>/<Module>/Transpiled/ConservativeWarnings.json`.
  - The cache key is the **closed-type-instantiation full name** (e.g., `List<object>`, `Dictionary<FName, IDamageable>`).
  - The cache is consulted at warning emit time; if the key is already in the cache (from a previous run of the same module's XIL2CPPAction in this build), the warning is suppressed.
  - Across modules: NO dedup. The same `List<object>` in M1 and M2 emits twice — once per module — which is correct because the warning is per-module-of-occurrence.
  - On invalidation: the cache invalidates when the module's `.cs` sources change (the same trigger as the action cache key). Standard XBT cache discipline.

**Severity.** MEDIUM — currently flagged audit item; this resolution makes it concrete.

---

## Section 9 — In-process XIL2CPP mode (§3.5 + §9.5)

### FIX-C-HIGH-16 — DLL hot-swap during in-process XIL2CPP call

**Offending Rev 1 quote (§3.5 + §9.5):**

> Neither section addresses XBT-itself hot-reload mid-XIL2CPP-call.

**Bug.** The audit prompt explicitly asks: "if XBT itself is hot-reloaded via XLiveCoding mid-build, what happens?"

XBT runs as a long-lived process (some builds take minutes-to-hours). XLiveCoding's foundation-prototype criterion (d) lets XBT be hot-reloaded mid-build (the same way game-code is). If XBT-itself is mid-reload while in-process XIL2CPP is executing Pass 6, the XBT-process's heap is being migrated, but in-process XIL2CPP's static state lives in that heap.

**Fix.** Specify §9.5:
  - In-process XIL2CPP calls run inside an XBT-quiesce-window. XBT hot-reload requests are blocked until all in-flight in-process XIL2CPP calls complete or are cancelled.
  - If XBT must hot-reload mid-build, the orchestrator:
    1. Calls `CancellationToken.Cancel()` on all in-flight XIL2CPP calls.
    2. Awaits their completion (via `Task.WhenAll`).
    3. Calls `ResetAsync()` on each XIL2CPP instance.
    4. Initiates XBT hot-reload.
    5. After XBT hot-reload, recreates the XIL2CPP instances and reschedules cancelled actions.

**Severity.** HIGH — currently unspecified; would cause unpredictable crashes in production CI.

---

### FIX-C-MED-14 — XIL2CPP's own GC pressure on the XBT process

**Offending Rev 1 quote (§3.5):**

> In-process mode is preferred for performance.

**Bug.** In-process mode keeps XIL2CPP's parse trees + Roslyn workspace + semantic models alive in the XBT process's managed heap. For a large project (1000+ modules), this can accumulate to gigabytes of managed memory. The XBT process's GC pauses to compact this memory; long-pause GC blocks the action scheduler.

**Fix.** Specify §3.5:
  - In-process XIL2CPP calls MUST `Dispose()` the Roslyn workspace at call exit (via the `ResetAsync()` introduced in FIX-C-CRIT-03).
  - The reset MUST also invoke `GC.Collect(generation: 2, mode: GCCollectionMode.Forced)` to compact the freed memory before returning.
  - Cost: ~10-50ms per reset; acceptable.

**Severity.** MEDIUM — perf regression under sustained in-process use.

---

## Section 10 — Diagnostic code allocations (§12)

### FIX-C-MED-15 — Diagnostic codes 100-119 + 110-119 are unused — non-contiguous gaps

**Offending Rev 1 quote (§12):**

> 100-109: LINQ; 120-129: Generic instantiation; 140-149: ABI / manifest; 150-159: Cross-system contract.

**Bug.** Bands 110-119 and 130-139 are unused; bands 100-109 only has XIL2CPP100. The doc references "XIL2CPP123-124" (in §5.8) which lives in the 120-129 band ALONG with other generic-instantiation codes — but the band reserves 10 slots and uses 2. Bands should either be sized to expected growth or numbered contiguously.

**Fix.** Either:
  - Renumber to make contiguous (small change; risk: code references in docs need updating).
  - Justify the bands: 110-119 = "Pattern matching errors" (currently empty; reserved for future expansion); 130-139 = "Generic-runtime errors" (currently empty).

Recommend the second (reserved bands), since renumbering breaks the inventory of codes already in the wild.

**Severity.** MEDIUM — banding hygiene; allocations are future-extensible if reserved-bands are documented.

---

### FIX-C-LOW-01 — XIL2CPP032, 033, 034 conflict potential

**Offending Rev 1 quote (§12):**

> 030-039: Tier classification. Specifically:
> - XIL2CPP030 — [XFunction(NoThrow = true)] proof failed.
> - XIL2CPP031 — Function conservatively Tier 1 due to unresolved cross-module callee.
> - XIL2CPP032 — Tier 2 function attempting to throw.
> - XIL2CPP033 — Two-pass tier classification mismatch between Pass 1 partial and Pass 2 merged tables.

**Bug.** This audit proposes 4 new diagnostic codes in this band: XIL2CPP032 (renamed by this audit to module-unload-listener-leak), XIL2CPP033 ([XOnClassReplaced] signature mismatch), XIL2CPP034 (generic constraint change), XIL2CPP035 (Tier 2 → Tier 1 hot-reload demotion), XIL2CPP036 (NoThrow cross-module lookup failure). The doc already uses 032, 033 for tier-classification cases.

**Fix.** Reallocate:
  - The original XIL2CPP032 + XIL2CPP033 (tier classification) stay.
  - The new audit-proposed codes go into newly-allocated slots: XIL2CPP037 (Tier 2 → Tier 1 demotion), XIL2CPP038 (NoThrow cross-module lookup), XIL2CPP039 (NoThrow chain proof failure detail).
  - The hot-reload / module-unload codes go into a new band: 200-209 (currently unused).

**Severity.** MINOR — numbering hygiene.

---

### FIX-C-MED-16 — No collision with XBT, XHT, or XCoreXObject codes — verified

**Verification of audit checklist item #10:**

  - **XBT codes:** XBT001 to ~XBT999 per `XBT.html` §16+. XBT040 (copyright header missing), XBT021 (tier violation), XBT022 (circular dep), XBT023 (engine-version mismatch), XBT070 (compile failed), XBT071 (link failed). All XBT codes use the `XBT<NNN>` prefix.
  - **XHT codes:** XHT001-XHT999 per `XHT.html` §15. XHT040 (missing XGENERATED_BODY), XHT051 (specifier conflict), XHT062 (cross-language ref), XHT123 (Action<T>/Func<T> requires [XDelegate]), XHT900 (ICE). All XHT codes use the `XHT<NNN>` prefix.
  - **XCoreXObject codes:** XCoreXObject001 referenced in XCoreXObject Rev 4. Different prefix entirely.

**No XIL2CPP code collides with XBT, XHT, or XCoreXObject codes** — they use different prefixes. The bands 1-151 in XIL2CPP are entirely within XIL2CPP's namespace.

**Severity.** N/A — verification, not a finding.

---

## Section 11 — Cross-platform ABI consistency (§9.7 + general)

### FIX-C-HIGH-17 — Per-target calling convention emit not specified

**Offending Rev 1 quote (§6.1 line 1882-1887):**

> "**Win64.** Microsoft x64 calling convention. ... **Quest 3 ARM64.** AAPCS64 calling convention. ... XObject* in registers requires register-spill at every safe-point..."

**Bug.** The stack-map record's `liveRefOffsets` are computed per-platform (different calling conventions => different register sets => different spill slots). The doc shows the high-level approach but never specifies the per-platform codegen tables. Specifically:
  - Win64: 4 argument registers (RCX/RDX/R8/R9) for XObject* args; remaining spill to `[RSP+0x28]`+.
  - ARM64: 8 argument registers (X0-X7) for XObject* args; remaining spill to `[SP+0]`+.
  - Float registers: Win64 XMM0-XMM3 for first 4 float args; ARM64 V0-V7 for first 8.

The codegen must emit different stack-map records for each platform. The doc doesn't show how.

**Fix.** Specify §6.1:
  - Per-platform codegen table: Win64 stack-map layout vs ARM64 stack-map layout.
  - XIL2CPP emits TWO stack-map records per function (one per platform target the build is producing). The collector at runtime selects the right one based on `__ImageBase` / `_PE_load_address`.
  - Cost: 2x stack-map storage. Acceptable: ~60 bytes / function * 10k functions = 600 KB.
  - Alternative: single normalized stack-map format with platform-conditional offset computation at runtime. More compact but slower; doc should commit.

**Severity.** HIGH — silent cross-platform divergence.

---

### FIX-C-MED-17 — Bitfield ordering for XObject layout not addressed

**Offending Rev 1 quote:** None — the doc never addresses bitfield ordering.

**Bug.** C# fields with `[MarshalAs(UnmanagedType.U1)] bool` etc. are uncommon, but C# 12 records with primitive fields lower to C++ structs whose bitfield ordering is platform-dependent:
  - x86_64: LSB-first.
  - ARM64: implementation-defined; usually LSB-first but can be MSB-first depending on toolchain.

If XPact's bit-packed FProperty flags (e.g., the 32-bit `EPropertyFlags`) cross the bitfield boundary, the on-disk schema bytes could differ. The doc doesn't lock this.

**Fix.** Specify §6 or §10:
  - XIL2CPP-emitted bitfield-using types (records, structs) use explicit `uint32_t` flags with manual bit operations, NOT C++ `unsigned int : N` bitfields.
  - The codegen never emits C++ bitfields directly.
  - Add a static check at module init: `static_assert(IsLittleEndian())` per target.

**Severity.** MEDIUM — corner case but a real cross-arch divergence vector.

---

### FIX-C-MED-18 — ARM64 alignment requirement for unaligned access not addressed

**Offending Rev 1 quote:** None — the doc never addresses ARM64 alignment.

**Bug.** ARM64 traps unaligned access for certain instructions (LDR/STR variants require natural alignment; unaligned loads use the slower LDP/STP). C# allows `[StructLayout(LayoutKind.Explicit, Pack = 1)]` for byte-packed structs. XIL2CPP doesn't say whether such packed structs:
  - Map to C++ `#pragma pack(1)` (which would generate unaligned access on ARM64).
  - Are rejected at parse time with a diagnostic.
  - Are silently re-aligned (which would silently break native interop).

**Fix.** Specify §5.4 or §6:
  - `[StructLayout(Pack = N)]` where `N < natural alignment of fields` emits diagnostic `XIL2CPP093 — Packed struct on ARM64 requires unaligned access; pack-attribute is BANNED for non-1-byte fields on ARM64`. Build exit 41.
  - Alternative: emit unaligned access intrinsics and accept the performance cost; doc commits.

**Severity.** MEDIUM — silent performance / correctness divergence on Quest 3.

---

## Section 12 — Live coding / hot-patch entry point coordination (§8.5)

### FIX-C-HIGH-18 — Single-method hot-patch emit: no per-method `.obj` granularity specified

**Offending Rev 1 quote (§8.5):**

> "Tier 1 shim functions (the `extern "C"` entry points with the `_Shim` suffix) are RVA-rewrite-target-eligible."

**Bug.** Per the audit prompt criterion (d): single-method hot-patch must complete in < 30 s. XLiveCoding's RVA-rewrite trampoline patches one function at a time. The C++ compiler's `.obj` granularity matters: if all transpiled functions for a single `.cs` file go into one `.obj`, then a single-method patch requires re-linking the whole `.obj` (multi-second). If each function is its own `.obj`, re-link is microseconds.

The doc doesn't specify the `.obj` granularity.

**Fix.** Specify §6 or §8.5:
  - XIL2CPP-emitted `.cs.cpp` files declare each function in a `.unique_section` (linker pragma) so the linker can place each in its own RVA range.
  - The linker is invoked with `/SAFESEH /ALIGN:1` (MSVC) or `-Wl,--gc-sections` (Clang) to enable per-function granularity.
  - Add a §6.x "Per-function linker section" subsection.

**Severity.** HIGH — without this, criterion (d) is unachievable.

---

### FIX-C-HIGH-19 — Cascade hot-patch: incremental rebuild of dependents not specified

**Offending Rev 1 quote (§8.6):**

> "(e) Cascade hot-patch < 120 s nominal / < 150 s timeout per Contract line 2306: validates XIL2CPP container layout-freeze + sub-pool rebind cooperation + XLiveCoding cascade orchestration."

**Bug.** Cascade hot-patch (criterion e) means a base class change forces dependents to rebuild. The doc lists what XIL2CPP must do (layout-freeze + sub-pool rebind) but doesn't specify the incremental rebuild path:
  - Which modules are dependents?
  - Are dependents re-transpiled, or just re-compiled?
  - Does the cache key correctly invalidate?

§3.4 says the cache key includes "every dependency module's TierTable.partial.json" — so a dependency tier change invalidates dependent's cache. But what about dependency *schema* changes (e.g., a base class adds a property)? The cache key includes "every dependency module's XHT-emitted reflection manifest" — so yes, this invalidates dependents. Good.

But timing: re-transpilation for a cascade can take many seconds per module if the module is large. 10 dependent modules at 5s each = 50s. With XHT-side regeneration + compile-link, the total could exceed 120s. The doc doesn't validate the timing.

**Fix.** Add a benchmark gate:
  - X-IL2CPP-CASCADE-INCREMENTAL: cascade-driven re-transpilation of 10 dependent modules of 10k LoC each completes in < 60 s (leaving 60 s for compile-link).
  - Specify how the cache benefits: per-function cache (not whole-module cache) lets the unchanged functions skip re-emit. Add `function_cache_hit_rate` to the XInsights telemetry.

**Severity.** HIGH — without per-function cache, criterion (e) is unachievable.

---

### FIX-C-MED-19 — Patch-DLL discovery mechanism not specified

**Offending Rev 1 quote:** None — the doc never specifies how XLiveCoding discovers patch DLLs.

**Bug.** XLiveCoding's RVA-rewrite trampoline targets specific symbols in specific DLLs. For XIL2CPP-emitted code, the symbol-to-DLL mapping is implicit (every C#-declared function lives in its module's DLL). The doc doesn't say where XLiveCoding gets the mapping table.

**Fix.** Specify §8.5:
  - XIL2CPP emits a per-module `LiveCoding.manifest.json` listing every Tier 1 symbol the module exports, with its symbol name + RVA placeholder + the source `.cs` file + the function's stable ID.
  - XLiveCoding loads this manifest at build time + on each patch attempt.
  - The manifest is published to `Intermediate/Build/<Target>/<Configuration>/<Module>/Transpiled/LiveCoding.manifest.json`.

**Severity.** MEDIUM — currently unspecified; cross-tool integration risk.

---

## Section 13 — Doc-prose / numbering hygiene

### FIX-C-MIN-01 — §3.5 references "Q27 resolution" but the question number isn't in this doc

**Offending Rev 1 quote (§3.5):**

> "Per Constraint §7.10 + `XBT.html` §6.4 line 2053: XBT may invoke XIL2CPP in-process (without spawning a subprocess) for transpile throughput."

**Bug.** The doc uses `§3.5` for "In-process mode" but the body says "Per Constraint §7.10 + Q27 resolution". Q27 is in `XIL2CPP-Constraints.md` (the inventory) but the section number §3.5 is local to this doc. The cross-reference is correct but tangled.

**Fix.** Tighten cross-references: replace "Q27 resolution" with "Constraint §7.10 + Q27" so the reader knows it's from the inventory.

**Severity.** MINOR — readability.

---

### FIX-C-MIN-02 — Doc-header line 26: FClass size claim wrong

**Offending Rev 1 quote (line 26):**

> "<strong>XCore-4b reference:</strong> `XCore-4b.html` Rev 4 (FProperty family of 28 subclasses, FClass = 240 bytes, FStruct = 120 bytes, ..."

**Bug.** Per Contract §14.2 (Rev 13.8), `sizeof(FClass) = 224` and `sizeof(FStruct) = 112`. The doc-header line 26 says 240 and 120. Either the XCore-4b reference is out of date, or the Contract version is out of date. Round 1 audit should check XCore-4b.html Rev 4 directly.

(Note: this duplicates FIX-C-CRIT-01 from §1 but in the doc header.)

**Fix.** Update line 26 to reference the canonical Contract §14 sizes (224 and 112) and update the XCore-4b doc reference text if that doc also drifts.

**Severity.** MINOR — duplicates CRIT-01 but in a different location.

---

### FIX-C-MIN-03 — §1.4 row "XLiveCoding" is the only row that references "Foundation Prototype criterion (d/e)"

**Offending Rev 1 quote (§1.4 table, XLiveCoding row):**

> "Foundation Prototype criterion (d) single-method hot-patch < 30 s; criterion (e) cascade hot-patch < 120 s nominal / 150 s timeout..."

**Bug.** The row mixes XLiveCoding's responsibilities with XIL2CPP's load-bearing commitments. The criteria belong to the Foundation Prototype-level cross-system view, not in the per-system cross-system integration matrix.

**Fix.** Move the criteria references to §11 (Acceptance Criteria mapping table). Keep the row's Load-bearing commitment column lean.

**Severity.** MINOR — table hygiene.

---

### FIX-C-MIN-04 — §8.3 references exit codes 90-99 but Contract §13 says they're reserved

**Offending Rev 1 quote (§8.3):**

> "XLiveCoding's patch-validation pass compares field-offset list field-by-field; mismatch fails patch with exit codes 90-99 (Live Coding domain)."

**Bug.** Per Contract §13.1, codes 90-99 are "Reserved for Live Coding domain failures (Phase 2; populated when XLiveCoding ships)" — currently empty. XIL2CPP's §8.3 says XLiveCoding emits 90-99 codes; this is forward-looking (XLiveCoding hasn't shipped) but the doc speaks in present tense.

**Fix.** Reword §8.3: "XLiveCoding's patch-validation pass WILL fail patch with exit codes 90-99 (Live Coding domain; codes allocated per Contract §13.1; specific code assignments TBD by XLiveCoding's spec)".

**Severity.** MINOR — tense / accuracy.

---

### FIX-C-MIN-05 — §1.5 line 105: cite for FIX-A-MIN-40

**Offending Rev 1 quote (§1.5 last bullet):**

> "Per-FClass sub-pool rebind cooperation: XIL2CPP-emitted `Z_Construct_FClass_*` functions register listeners for the per-FClass sub-pool rebind event (FIX-A-MIN-40 in XCoreXObject Rev 4)..."

**Bug.** FIX-A-MIN-40 is in XCoreXObject Rev 4 §3.6 (per the production code's `FXObjectAllocator::RebindClassPool` doc-comment). The cite is correct but could include the section number.

**Fix.** Add "(FIX-A-MIN-40 in XCoreXObject Rev 4 §3.6)" for clarity.

**Severity.** MINOR — cite hygiene.

---

### FIX-C-MIN-06 — §3.5 says cancellation is honored "at every Pass boundary" — too coarse

**Offending Rev 1 quote (§3.5):**

> "The CancellationToken is honored at every Pass boundary; if cancellation is requested mid-Pass-6, XIL2CPP cancels..."

**Bug.** Pass 6 (C++ emit) is the longest pass — easily multi-second for a large module. "Pass boundary" cancellation means a user pressing Ctrl-C during Pass 6 must wait until Pass 6 completes (or never finishes if Pass 6 is itself stuck). Cancellation must be more frequent.

**Fix.** Tighten §3.5: "The CancellationToken is honored at every back-edge in every Pass: in Pass 6 at every function-emit boundary; in Pass 4 at every fixpoint iteration; in Pass 5 at every mangled-name emit. The cancellation cost is ≤ 100 ms (the time from token-cancelled to XIL2CPP returns)."

(Duplicates the cancellation point in FIX-C-CRIT-03 but at a different location.)

**Severity.** MINOR — duplicates CRIT-03.

---

### FIX-C-MIN-07 — §5.1 line 738 cite "HealthPickup.cs" — should be a relative path or descriptive name

**Offending Rev 1 quote (§5.1 line 738):**

> "// File: Source/Game/HealthPickup.cs  ->  HealthPickup.cs.h + HealthPickup.cs.cpp"

**Bug.** "Source/Game/HealthPickup.cs" is an absolute-style path that doesn't match the XPact module-relative path convention. Per Contract §1.4, paths are logical paths within the plugin, not absolute filesystem paths. The example is misleading.

**Fix.** Reword to `Module: XGameFramework / LogicalPath: GameFramework/Pickups/HealthPickup` or use a placeholder `<Module>/<LogicalPath>/HealthPickup.cs`.

**Severity.** MINOR — doc clarity.

---

### FIX-C-MIN-08 — §11.2 X-IL2CPP-SIZE gate (4x C# LoC ratio) — should reference test corpus

**Offending Rev 1 quote (§11.2):**

> "X-IL2CPP-SIZE: Transpiled .cs.cpp size ratio: ≤ 4x the input C# .cs LoC. Target: 1k LoC C# produces ≤ 4k LoC C++ on average."

**Bug.** The X-IL2CPP-SIZE gate cites a 4x ratio but no test corpus to measure against. The §14 audit item O-OUTPUT-SIZE flags this; the doc doesn't resolve it.

**Fix.** Reference the §13 Foundation Prototype harness corpus (SyntheticActorCorpus + SyntheticContainerCorpus). Specify: "Measured against the §13 Foundation Prototype corpus; mean ratio ≤ 4x; per-file 95th percentile ≤ 6x."

**Severity.** MINOR — measurement methodology.

---

### FIX-C-MIN-09 — §10.4 "Round 1 audit MUST surface this to the XHT design owner" — this audit IS doing that, but the requirement isn't actionable per se

**Offending Rev 1 quote (§10.4):**

> "Round 1 audit MUST surface this to the XHT design owner."

**Bug.** The doc commits the audit (this one) to surfacing the Q14 resolution. Done — this audit has done that, but the doc's wording is meta-commentary, not actionable.

**Fix.** Reword as: "XHT Rev 7+ must reflect this Q14 resolution. The cross-tool symbol contract (Contract §10.2 line 1818) must be amended accordingly. The XHT and Contract design owners are notified via the audit-trail file." This is the same audit-item that goes into §14 as O-Q14-RESOLUTION.

**Severity.** MINOR — meta-commentary.

---

## Section 14 — Final synthesis

The XIL2CPP Rev 1 design is **structurally sound from the hot-reload / XBT-integration lens** in its high-level architecture: the 7-pass pipeline, two-pass tier classification, action-graph placement, in-process / subprocess parity, and OnClassReplaced delegate hook are all the right ideas. The **fundamental design** of XIL2CPP — pure transpilation; deterministic mangling; per-function FStackMapRecord; container XGCRootSpan; FakeVTable dispatch — is coherent and well-motivated.

**The shortfalls are in specifics**, not design. The three CRITICAL findings (ABI tag drift, listener lifetime, in-process resource lifecycle) are all fixable with documentation + small code additions; none require redesigning the system. The HIGH findings (mangling completeness, vtable layout, multi-interface emit, exit-code collision, cross-platform codegen tables, per-function `.obj` granularity, cascade incremental rebuild) are predictable gaps in a system this complex; the doc has flagged most of them as audit items in §14.

**Backwards-compatibility risks not caught by the constraint inventory:**

1. **CRIT-01: ABI tag content drift between XIL2CPP.html and Contract Rev 13.8.** The inventory was authored against Contract Rev 13.7 or earlier; the Stage B addendum in Rev 13.8 (XCore-4b Phase 4b.7 landing) reset the layout-tag content discipline. XIL2CPP's §5.1 / §9.7 invented `"vN"` shorthand strings that don't match Contract §14.1's verbatim content. This is the largest single backwards-incompatibility risk: every `.cs.cpp` would fail compile against the Stage B runtime.

2. **CRIT-02: OnClassReplaced listener leak.** The pattern of registering raw lambdas with `AddRaw` was never the canonical XCoreXObject pattern; XCoreXObject Phase 5.k shipped `IXObjectCreateListener` / `IXObjectDeleteListener` interfaces specifically to prevent this. XIL2CPP's design reverts to the dangerous pattern, undoing the Phase 5.k discipline.

3. **CRIT-04: ReferenceCompileCSharpAction ordinal 14 collision.** XBT's slot reservation discipline (slots 9-15 reserved Phase 2; CommandVersion auto-derived from the enum's field-set hash) means XIL2CPP CANNOT unilaterally allocate slot 14. The slot allocation must be coordinated with XBT Rev 11+ amendment.

4. **HIGH-04: Exit code 24 (PluginNotFound) collision.** Phase 1 stub returning 24 collides semantically with XBT's legitimate use of 24. The doc must commit to a new code (or use 61) for the stub.

**Recommended priorities for Rev 2:**

- **Must-fix before Rev 2 ships:** all CRITICAL findings (CRIT-01 ABI tags, CRIT-02 listener leak, CRIT-03 in-process resource lifecycle, CRIT-04 action ordinal).
- **Should-fix before Rev 2 ships:** HIGH-01 through HIGH-19 (mangling completeness, vtable layout, cross-platform codegen, cross-module callee discovery, cascade incremental rebuild).
- **Defer to Rev 3 or Phase 6 implementation:** MEDIUM and MINOR findings, except those with clear test corpus implications.

**End of XIL2CPP Rev 1 Round 1 Audit C — Hot-Reload Integration, ABI Stability, and XBT Build-Pipeline Integration.**
