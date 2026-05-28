# XIL2CPP Constraint Inventory (Research / Pre-Rev-1 Harvest)

> **Document status.** This is not a system spec. It is the comprehensive constraint inventory the Rev 1 XIL2CPP design author will write against. Every entry is sourced verbatim from the seven existing XPact design docs; cite-backs are inline. Per-entry quotes use the doc's own wording where load-bearing.
>
> **Format note (repository convention).** `Documents/README.html` states "one HTML file per system. No markdown is permitted anywhere in this repository." This file is Markdown by the user's explicit instruction to distinguish it from a Rev 1 design doc. The Rev 1 author MUST convert this to HTML when authoring `Documents/XIL2CPP.html` (the actual system spec). **Open contradiction tag: see Section 10, item Q0.**
>
> **Source corpus (with XIL2CPP reference counts):**
>
> | Doc                                             | Refs | Role                                                                     |
> | ----------------------------------------------- | ---- | ------------------------------------------------------------------------ |
> | `XToolchainContract.html` Rev 13.9              | 90   | The ABI contract — interface lock for what XIL2CPP must produce/consume |
> | `XBT.html` Rev 10                               | 71   | Build-pipeline integration; action graph; manifest                       |
> | `XCoreXObject.html` Rev 4                       | 38   | GC integration; stack maps; sim-path runtime invariant; XPACT_GC_STORE   |
> | `XHT.html` Rev 6                                | 35   | Reflection-metadata producer XIL2CPP consumes; cross-tool symbol contract|
> | `XToolchainContractAddendum-Step05.html` Rev 5  | 11   | Phase 1 build-graph reality (what's wired vs. stubbed)                   |
> | `XCore-4b.html` Rev 4                           | 10   | FProperty subclass family; C# language-feature reflection                |
> | `XCore-4a.html` Rev 3                           | 2    | XGCRootSpan ABI; string interning                                        |
> | `README.html`                                   | 2    | Project doc convention                                                   |

---

## Section 1 — System Overview (What is XIL2CPP?)

### 1.1 Definition (from `XToolchainContract.html` §0)

> "**XIL2CPP** — XPact IL2CPP transpiler. C# standalone. Transpiles C# source into C++ with GC root scaffolding, exception lowering, C# string mapping, and stable symbol mangling. **No Unreal precedent** — closest reference is Unity's IL2CPP, but XIL2CPP transpiles directly from C# source (not IL) and emits hot-patchable, hybrid-GC-aware code rather than Mono-runtime-bound code." (`XToolchainContract.html` §0, line 262)

### 1.2 L-tier position

XIL2CPP is **System 6** in the Master Plan build order (`XCoreXObject.html` §1.5 line 179; `XBT.html` §10 line 1464 — "XIL2CPP is not yet implemented (System 6 per the Plan, scheduled after XBT and XHT)"). XIL2CPP belongs to **Layer 0** standalone tools (`XToolchainContract.html` §10.2 schema comment line 1845-1846: "Programs (ordinal 4) added under Rev 13 after ThirdParty (ordinal 3); prior ordinals stable. Required for Layer-0 standalone tools (XBT, XHT, XIL2CPP, XAutomationTool)").

It is one of the three Toolchain Contract participants alongside XBT (build tool) and XHT (header tool) (`XToolchainContract.html` §0 line 258-263).

### 1.3 What XIL2CPP produces

Per `XBT.html` §10.2 line 1478:

> "For each `.cs` in the module's `SourceFiles.CSharp`, XIL2CPP emits a transpiled `.cpp` + paired `.h` under `Intermediate/.../<Module>/Transpiled/`. Each emitted symbol is mangled per Contract Rev 12 Section 2 using the **Itanium-ABI-style length-prefixed components** rule. GC root scaffolding emits using the span-based ABI (Contract Section 3: `XGC_RegisterRootSpan` family, not per-slot). Exception codegen splits per Contract Section 5 (shim/direct). C# string mapping is per Contract Section 6."

Additionally a per-module transpilation manifest listing inputs and produced outputs (`XBT.html` §10.5 line 1492).

### 1.4 What XIL2CPP consumes

1. The **JSON manifest + binary FlatBuffers sidecar** emitted by XBT (`XBT.html` §8 line 1157; `XToolchainContract.html` §10.2; `XBT.html` §10.1 line 1472-1474 — invocation: `XIL2CPP.exe -Manifest=…/Manifest.json -ManifestBin=…/Manifest.bin -Module=<ModuleName>`).
2. **XCoreXObject's compiled header surface**, "for the GC ABI emit, per Contract Section 3.1" (`XBT.html` §10.5 line 1492).
3. **The `.cs` source files** of the module being transpiled (`XBT.html` §10.5 line 1492; `XToolchainContract.html` §10.3 line 2039: "Inputs: manifest + all module .cs files + (XCoreXObject's compiled header surface, for the GC ABI emit)").
4. **The per-module `.gen.manifest`** XHT emits, which lists the cross-language type pairs (`XHT.html` §17.3 line 1582: "XIL2CPP reads the per-module `.gen.manifest` at transpile time").
5. **XHT-emitted reflection metadata** for cross-module callees, specifically the `NoThrow` annotation bit (`XToolchainContract.html` §5.2 line 934-935; `XHT.html` §17.2 line 1576).
6. **The shared `XPact.Mangling` NuGet package**, shared with XHT (`XHT.html` §17.4 line 1617: "Both tools share the table via the shared `XPact.Mangling` NuGet package referenced by both").

### 1.5 Architectural positioning

XIL2CPP runs **concurrent with XHT**, not sequential (`XBT.html` §10.6 line 1494-1498; `XToolchainContract.html` §10.1 line 1704-1713: "XHTAction and XIL2CPPAction concurrent"). Both consume the manifest; both feed the C++ compile step. Per-module pipelining: once both XHT and XIL2CPP finish module A, that module's C++ compiles can start without waiting for other modules.

XIL2CPP unblocks gameplay-tier systems (XActor / XComponent / XWorld) and the Foundation Prototype milestone (Step 5.5) (`XCoreXObject.html` §1.5 line 160: "XCoreXObject unblocks System 6 (XIL2CPP full emitter, which depends on the XObject runtime to emit GC-aware allocations and root scaffolding)").

### 1.6 The Phase 1 stub (current state)

Per `XBT.html` §10 line 1466: "Phase 1 status: XIL2CPP is not yet implemented (System 6 per the Plan, scheduled after XBT and XHT). This section describes the contract XBT will follow when XIL2CPP lands; the corresponding XBT code paths exist but invoke a stub. The `XActionType` slot `XIL2CPPAction` (ordinal 4) is reserved (Section 5.1); no concrete action class is emitted in Phase 1." See also `XBT.html` §3.2 line 258: `run-xil2cpp` mode "Realised as a stub `IToolMode<T>` (`RunXIL2CPPMode.cs`) returning exit code 24 (`PluginNotFound` mnemonic) with a Phase 2 schedule diagnostic, symmetric to `run-xht`."

`XToolchainContractAddendum-Step05.html` §10 line 370-371 confirms: "XHT and XIL2CPP are not built yet, so no `.gen.h` emit action is in the action graph. The `XActionType` slots `ParseHeadersAction` (ordinal 2), `EmitReflectionAction` (3), and `XIL2CPPAction` (4) exist but have no concrete classes."

### 1.7 Why XPact's transpiler approach (vs UE)

Per `XToolchainContract.html` §11.2 line 2120: GC root-registration ABI "No UE precedent — UE uses a single C++ GC and managed C# in a sibling Mono runtime." XPact diverges: real C# → C++ transpilation with hybrid-GC awareness and hot-patch support.

Per `XCoreXObject.html` §15 design rationale list line 2935: "Precise C# stack scanning via XIL2CPP stack maps — Unity's IL2CPP does precise stack scanning via similar maps; UE's blueprint VM doesn't have a comparable concept. XPact picks the precise approach as the only one compatible with the Master Plan's 'no conservative stack scanning' lock."

---

## Section 2 — APIs XIL2CPP MUST PRODUCE

Format: `**API/artifact** (cite: doc §x.y): what it is.`

### 2.1 Transpiled C++ source files

- **Per-`.cs` `.cpp` + paired `.h`** (`XBT.html` §10.2 line 1478; `XHT.html` §17.2 line 1572): Output to `Intermediate/.../<Module>/Transpiled/<CSFile>.cs.cpp` + `.cs.h`. Each file `#include`s the corresponding XHT-emitted `{Header}.gen.h` to pick up XHT's property accessors (`XHT.html` §17.2 line 1573).

- **Per-module transpilation manifest** (`XBT.html` §10.5 line 1492): Lists the inputs and produced outputs.

### 2.2 Symbol mangling — Itanium-ABI-style length-prefixed (LOCKED ABI)

- **Mangled C++ symbol name format** (`XToolchainContract.html` §2.2 line 480-492):
  ```
  {ContractVersion}__{Namespace}::{Type}::{Method}{GenericArgManglings}{ParamManglings}
  ```
  Components:
  - `{ContractVersion}`: literal `_v` + first ten hex chars of contract-structure BLAKE3.
  - `{Namespace}`: dots → `::`.
  - `{Type}`: simple type name; nested types joined by `::`.
  - `{Method}`: simple method name; CLR operator overload conventions (`op_Add`, `op_Equality`); constructors `$ctor`; static constructors `$cctor`.
  - `{GenericArgManglings}`: `_G_` + comma-separated mangled args in angle brackets, recursive.
  - `{ParamManglings}`: `_P_` + parenthesized comma-separated mangled params. Reference types prefix `R`; value types `V`; by-ref `B`; in/out `I`/`O`.

  Linker-visible translation (deterministic and reversible per §2.3 line 517): `::` → `__`; parentheses dropped; commas/spaces → `_`.

- **Free-function form with explicit self** (`XToolchainContract.html` §2.3 line 494-517): Every transpiled C# method becomes an `extern "C"` free function with an explicit `XObject* self` first parameter. Vtable dispatch for virtuals is handled by a generated dispatcher (also a free function) that performs the table lookup and tail-calls the resolved free function.

  Example (line 512-515):
  ```
  extern "C" void _v1ab12cd34__Simgenics__XPact__GameFramework__Valve__SetOpenFraction_P_R_Simgenics__XPact__GameFramework__Valve_V_float(
      Simgenics::XPact::GameFramework::Valve* self,
      float inFraction);
  ```

- **Determinism requirements** (`XToolchainContract.html` §2.4 line 519-527): purely structural mangling; generic-instantiation walk in source-declaration order; XIL2CPP's internal dict iteration sorted by name before emit; XIL2CPP's own binary is reproducible (its build inherits the reproducibility envelope).

- **Cross-tool symbol-space convention** (`XToolchainContract.html` §10.2 §"Cross-tool symbol-space convention" lines 1808-1818): Reflection-registration singleton-getter format `Z_Construct_X<Kind>_<ModuleName>_<TypeName>` where `<Kind>` ∈ {`Class`,`Struct`,`Enum`,`Interface`,`DelegateFunction`,`Function`,`Property`}. **For C#-declared types** (`[XClass]` attribute) XHT emits the singleton-getter **declaration** as an `extern "C"`; **XIL2CPP emits the matching definition** in its transpiled C++ output. "**XIL2CPP MUST emit exactly the symbol XHT emits an extern for** — this is the cross-tool contract Rev 13.7 makes explicit." (line 1818). Reiterated in `XHT.html` §16 line 1157.

### 2.3 GC root scaffolding (span-based ABI)

- **`XGCRootSpan` member on every transpiled container type** (`XToolchainContract.html` §3.1 line 547-575; §3.3 line 607-663 illustrative emit):
  - At construction: build `XGCRootSpan{base, stride, count=0, kind, _padding=0}` and call `XGC_RegisterRootSpan(&span)`.
  - On resize (`Add`, `EnsureCapacity`): call `XGC_UpdateRootSpan(&span, newBase, newCount)`.
  - On mutation that changes count: call `XGC_UpdateRootSpan`.
  - At destruction: `XGC_UnregisterRootSpan(&span)`.

  Per `XToolchainContract.html` §3.3 line 663: "the `XGCRootSpan` member is part of every transpiled container type. XIL2CPP emits it at the end of the class body; reflection (XHT) does not enumerate it (it is not an `XPROPERTY`)."

- **`XGCRootKind` selection** (`XToolchainContract.html` §3.2 line 581-594):
  - Generic arg statically XObject-derived (`List<XActor>`, `Dictionary<FName, XComponent>`) → `XGCRootKind::Strong`.
  - Generic arg statically value type (`List<int>`, `List<FVector>`) → **no GC scaffolding emitted at all** (pure RAII).
  - Generic arg `object` or non-statically-typed interface that could hold XObjects → `XGCRootKind::Conservative`.

- **Move-only container types** (`XToolchainContract.html` §3.4 line 665-699; `XBT.html` §10.2 line 1480): Containers with embedded `XGCRootSpan` are **move-only**. XIL2CPP emits `= delete` for copy constructor and copy assignment. Move construction/assignment transfer span-registration ownership atomically with respect to mark phase.

- **`container = true` class-descriptor flag** (`XToolchainContract.html` §3.7 line 759): "XIL2CPP emits on any class with an embedded `XGCRootSpan`" — consumed by XLiveCoding's patch-validation pass for layout-stability check.

### 2.4 Per-function stack-map records (precise stack scanning)

- **`FStackMapRecord` per live PC range** (`XCoreXObject.html` §5.4 line 1281-1290):
  ```
  struct alignas(8) FStackMapRecord {
      uint32_t pcRangeBegin;     // byte offset within the function
      uint32_t pcRangeEnd;       // byte offset
      uint16_t numLiveRefs;
      uint16_t _pad;
      int32_t  liveRefOffsets[]; // byte offsets within the stack frame
  };
  ```
  Static const data in `.rodata`; one record per live range. Size budget ~30 bytes per function; ~300 KB for 10k transpiled functions.

- **Sources of live refs in the stack map**: locals + temporaries + arguments (`XCoreXObject.html` §5.3 line 1223).

- **Coverage**: every transpiled C# function emits a stack-map record (`XCoreXObject.html` §5.3 line 1223; §10.7 line 2410: "C# stack frames are scaffolded with stack-map records (§5.4) so the precise stack scan can enumerate live references").

- **Async/await state machine stack maps** (`XCoreXObject.html` §10.7.2 line 2448-2461): "Transpiled C# async state machines emit XGCRootSpan registration at `MoveNext` entry; unregister at completion. Awaiter frames containing XObject captures stay live across await suspension."

- **Stack-stored struct-with-XObject-field** (`XCoreXObject.html` §10.7.4 line 2469-2473): "XIL2CPP stack-map registers the byte offset of the XObject field. The precise stack scan (§5.4) enumerates the field as a root."

### 2.5 Safe-point checks

- **Function-entry safe-point check** (`XCoreXObject.html` §5.5 line 1301): "Every XIL2CPP-emitted function entry (the compiler inserts the check at function prologue; cost is ~3-5 cycles)."
- **Long-loop back-edge check** (`XCoreXObject.html` §5.5 line 1302): "Every long loop's back-edge (the compiler inserts the check at backward branches in loops > some-threshold-iterations)."

### 2.6 Write barriers — XPACT_GC_STORE pattern

- **Every reference-storing assignment** (`XCoreXObject.html` §5.2 line 1199-1210):
  ```
  // C# source:
  //   actor.Owner = newOwner;
  //
  // XIL2CPP emits (simplified):
  {
      XObject** slot = &reinterpret_cast<XActor*>(actor)->Owner;
      XGC_WriteBarrier(reinterpret_cast<void**>(slot), newOwner);
      *slot = newOwner;
  }
  ```

- **Type detection rule** (`XCoreXObject.html` §5.2 line 1212): "XIL2CPP recognizes reference-storing assignments through type analysis (the C# field's reflected type is FObjectProperty, FWeakObjectProperty, FStructProperty containing reference, FArrayProperty/FMapProperty/FSetProperty with reference inner, etc.). The codegen pass inserts the barrier emission at the IL-to-C++ lowering stage."

- **Forward commitment cite** (`XCoreXObject.html` §10.7 line 2409): "C# field writes to XObject references are lowered to the XPACT_GC_STORE pattern (§5.2). This is a forward commitment XIL2CPP will need to deliver in System 6 (FIX-A-MIN-47); Rev 2 documents the design-time direction."

### 2.7 Two-tier function calling convention

- **Tier 1: ABI-stable shim** (`XToolchainContract.html` §5.1 line 903-906; §5.3 line 946-963):
  - Signature: `extern "C" void _Foo_Shim(void* self, A a, B b, XResult* outResult)`.
  - **Identical across all build configurations** (Dev native exceptions; Shipping `Result<T,E>` lowering).
  - Applies to: (a) functions crossing a DLL boundary; (b) `[XFunction(CanThrow = true)]`-annotated; (c) functions XIL2CPP cannot prove noexcept.
  - Body lowering differs Dev (line 1005-1018) vs Shipping (line 1024-1029); shim signature does not.

- **Tier 2: direct call** (`XToolchainContract.html` §5.1 line 909-913; §5.4 line 965-974):
  - Plain C++ free function with the C# method's natural signature; no `XResult` out-param.
  - "Zero overhead vs. handwritten C++."
  - Tier 2 functions are **not hot-reloadable by Live Coding's RVA-rewrite trampoline** (line 974): "which requires the `extern "C"`-style entry point), but per plan Section 2 Hot-reload commitment row, only public API methods are guaranteed hot-reloadable — internal methods rebuild their owning module."

### 2.8 Two-pass tier classification

- **Pass 1 — per-module, concurrent (mandatory MVP)** (`XToolchainContract.html` §5.2 line 931-936; `XBT.html` §10.7 line 1505):
  - Local callees participate in fixpoint normally.
  - Cross-module callees **conservatively assumed Tier 1** unless callee is annotated `[XFunction(NoThrow = true)]` in its declaring module's exported interface (read via XHT-emitted reflection metadata).
  - Per-module action publishes per-function tier assignments to `Intermediate/Build/<Target>/<Configuration>/TierTable.partial.<Module>.json`.

- **Pass 2 — sequential, post-MVP** (`XToolchainContract.html` §5.2 line 938; `XBT.html` §10.7 line 1506): "Propagate Tier 2 attribution upward through dependency-resolved cross-module call graphs. A function conservatively marked Tier 1 in Pass 1 because of an unresolved cross-module callee can be promoted to Tier 2 if Pass 2 proves the callee is in fact NoThrow." Pass 2 lands when `-EnableTier2WholeProgram` CLI flag set (default off in MVP); never demotes Tier 2.

- **NoThrow proof rules** (`XToolchainContract.html` §5.2 line 919-927):
  ```
  f is Tier 2 iff:
  1. f is not exported from its owning DLL; AND
  2. f is not annotated [XFunction(CanThrow = true)]; AND
  3. Every callee of f is itself Tier 2 (transitive); AND
  4. f's body contains no `throw` expression; AND
  5. f does not invoke a C++ function declared without `noexcept` that
     XIL2CPP cannot prove safe.
  ```

- **`[XFunction(NoThrow = true)]` is a hard assertion** (`XToolchainContract.html` §1.2 line 351; §5.2 line 942): "XIL2CPP refuses if it cannot satisfy the noexcept proof." If proof fails, build fails with exit code 63.

### 2.9 Tier 1 shim body lowering

- **Dev/Test (native exceptions)** (`XToolchainContract.html` §5.6 line 1005-1018):
  ```
  extern "C" void _v1__Foo_Shim(void* self, A a, B b, XResult* outResult) {
      try {
          T value = _v1__Foo_Body(self, a, b);
          outResult->discriminator = XResult::Discriminator::Success;
          new (outResult->success.buffer) T(std::move(value));
      } catch (const XCSharpException& ex) {
          outResult->discriminator = XResult::Discriminator::Error;
          outResult->error.code = ex.code;
          outResult->error.typeName = ex.typeName;
          outResult->error.message = ex.message;
          outResult->error.stackTrace = ex.captureStack();
      }
  }
  ```

- **Shipping (Result<T,E>)** (`XToolchainContract.html` §5.7 line 1024-1029):
  ```
  extern "C" void _v1__Foo_Shim(void* self, A a, B b, XResult* outResult) {
      _v1__Foo_Body(self, a, b, outResult);
      // Body writes to outResult directly. No try/catch — exceptions
      // are not raised in Shipping bodies.
  }
  ```

- **XResult is emitted as RAII type with conditional dtor** (`XToolchainContract.html` §5.8 line 1043-1064): The destructor reads the discriminator; on Success, calls the per-T destructor via a type-erased pointer the shim emit-site fills in.

- **Per-call-site XResult destructor teardown** (`XToolchainContract.html` §5.8 line 1041): "Non-trivially-destructible non-XObject types (e.g., `FString`, `TArray<T>`, custom RAII handles) require explicit destruction. The shim emits the appropriate destructor call when `XResult` goes out of scope on the calling side; XIL2CPP generates this teardown automatically at every call site."

### 2.10 C# string handle emission

- **`XCSharpString` value-type handle** (`XToolchainContract.html` §6.1 line 1100-1104):
  ```
  struct XCSharpString {
      const FString* m_storage;
  };
  ```
  8 bytes; trivially copyable; no destructor.

- **Compile-time literal interning** (`XToolchainContract.html` §6.2 line 1108-1120; `XCore-4a.html` §3 line 1432-1437):
  - C# string literals emit as UTF-8 constants in the binary's `.rodata` section.
  - **Build-time error if two distinct C# literals resolve to the same address** (per `XCore-4a.html` line 1434).
  - Pointer identity guarantee: `ReferenceEquals(nameof(X), "X")` holds (`XToolchainContract.html` §6.2 line 1110, 1117-1120; `XCore-4a.html` line 1436).
  - All literal usages, `nameof(X)`, `typeof(X).Name` participate in the same interning pool.
  - **A unit test in `Tests/IL2CPP/StringInternIdentity` sweeps 1000+ literal pairs across two DLLs and asserts pointer identity holds** (`XCore-4a.html` line 1436).
  - SSO/heap discriminator stable across hot-reload (`XCore-4a.html` line 1435).

- **Iterator surface emission** (`XToolchainContract.html` §6.4 line 1132-1141): `s.AsSpan()`, `s.IndexOf(c)`, `s.Substring(i, n)`, `s.AsCodepoints()` ship in the C# binding.

- **Sim-path `s[i]` is build-time error** (`XToolchainContract.html` §6.4 line 1128): "XIL2CPP emits a build-time error on `s[i]` usage in sim-path TUs". Suppressible only via `[XFunction(SimPathStringIndexOK = true)]`. Exit code (inferred from §13 table line 2341) 63.

- **C++ → C# string crossing** (`XToolchainContract.html` §6.5 line 1145): "XIL2CPP emits a cached-view wrapper that allocates an `XCSharpString` handle pointing into the FString. The cached view is interned per-call-stack-frame to avoid repeated allocations for the same FString within a frame."

### 2.11 FXObjectLifecycleTable function-pointer slot implementations

- For each transpiled XObject-derived C# class, XIL2CPP must emit (`XCoreXObject.html` §2.4 line 499-509; §10.7 line 2408):
  - `Z_PostInitProperties_<Type>` (slot 0) — `void(*)(XObject*)`.
  - `Z_BeginDestroy_<Type>` (slot 1) — `void(*)(XObject*)`.
  - `Z_IsReadyForFinishDestroy_<Type>` (slot 2) — `bool(*)(XObject*)`.
  - `Z_FinishDestroy_<Type>` (slot 3) — `void(*)(XObject*)`.
  - `Z_Serialize_<Type>` (slot 4) — `void(*)(XObject*, FArchive&, const FArchiveContext*) noexcept`.
  - `Z_AddReferencedObjects_<Type>` (slot 5) — `void(*)(XObject*, FXGrayQueue&)`.
  - `Z_PostLoad_<Type>` (slot 6) — `void(*)(XObject*)`.
  - `Z_PreSave_<Type>` (slot 7) — `void(*)(XObject*, const FObjectPreSaveContext*)`.
- **A `constinit const FXObjectLifecycleTable XType_LifecycleTable` in `.rodata`** populated with function pointers (`XCoreXObject.html` §2.8 line 621).
- Slots may be `nullptr` for unimplemented operations; the `Capabilities` bitmask (`XCoreXObject.html` §2.4 line 512) tracks which slots are present.

> Note: XHT emits these for C++-declared XObject types (`XCoreXObject.html` §2.8 line 619-624). XIL2CPP must emit the matching ones for C#-declared XObject types — the cross-tool symbol contract requires identical bytes (`XToolchainContract.html` §10.2 line 1818).

### 2.12 FClass / FStruct static initialization (`Z_Construct_*` family)

For each C#-declared `[XClass]` / `[XStruct]` / `[XEnum]` / `[XInterface]`, XIL2CPP emits the **definition** of the singleton-getter XHT declared as `extern "C"`:

- `Z_Construct_FClass_<ModuleName>_<TypeName>` (`XToolchainContract.html` §10.2 line 1812 + `XCoreXObject.html` §2.8 line 623): function calls `XReflectionRuntime::RegisterClass(&XType_Class)` at DLL load; constructs eager CDO if policy is "eager CDO" (`XCoreXObject.html` §8 + `§2.8` line 623).
- `Z_Construct_FStruct_<ModuleName>_<TypeName>` (`XToolchainContract.html` §10.2 line 1812-1814).
- A `constinit const FClass XType_Class` in `.rodata` with all the FClass fields populated (`XCoreXObject.html` §2.8 line 622).

### 2.13 Schema-vector opcode arrays (`s_<Type>_RefSchemaOps[]`)

> Note: per `XCoreXObject.html` §7.4.1 line 1773, **XHT emits the schema vector** for both C++ and C# types — but **XHT emits the singleton-getter declaration as an extern when XIL2CPP owns the definition**. The implicit cross-tool contract here is that for C#-declared types, XIL2CPP MUST emit the matching schema-vector definition consistent with the rule table in `XCoreXObject.html` §7.4.1 line 1779-1808 (22 opcodes; emit one `FXObjectRefSchemaOp` per reference-carrying FProperty + terminator). **This is an open question — see Section 10, item Q14.** The docs are ambiguous about which tool emits the `.rodata` schema array for C#-declared types.

- **`FXObjectRefSchemaOp` struct (24 bytes, alignof 8)** (`XCoreXObject.html` §7.4 line 1739-1748).
- **`FXObjectRefSchema` (24 bytes)** with `Ops` pointer to packed opcode array, ending in `EXObjectRefSchemaOp::Terminator` (`XCoreXObject.html` §7.4 line 1755-1768).

### 2.14 Generic instantiation reflection

- **For each closed C# generic instantiation reached by the program**, emit one FClass with the appropriate suffix (`XCore-4b.html` line 724): "each generic instantiation is a distinct FClass with a name suffix (e.g., `TArray_int`, `TArray_XObject`). `FArrayProperty.Inner` points at the element FProperty for the specific instantiation. XIL2CPP emits one FClass per instantiation reached by the program; the cost is per-program, not per-engine-build."

- **Closed-type-tree walk emits**:
  - If any transitive member is XObject-derived → typed `XGCRootSpan` with byte-stride (`XCoreXObject.html` §10.7.1 line 2421).
  - If a member is `object` or open-typed → Conservative `XGCRootSpan` + build-time warning on sim-path TUs (`XCoreXObject.html` §10.7.1 line 2422).
  - Pure-value generics → no `XGCRootSpan` (line 2423).

### 2.15 Other emit obligations

- **C# struct-with-XObject-field handling** by storage:
  - Stack-stored → stack-map registers byte offset (`XCoreXObject.html` §10.7.4 line 2471).
  - Class-field → schema vector recurses via `EXObjectRefSchemaOp::Struct` (line 2472).
  - Array-stored → `TArray<TStruct>` with `XGCRootSpan` partial specialization that recurses (line 2473).

- **C# `record` types** (`XCore-4b.html` line 725): "primary-constructor parameters become FProperty entries with auto-property semantics; XIL2CPP emits the init-only setter and value-equality default. Records are FScriptStruct-shaped at the C++ side; FScriptStruct's `HasIdentical` capability bit (FIX-5) is auto-populated for records."

- **C# `required` members** (`XCore-4b.html` line 726): "`EPropertyFlags` adds `CPF_RequiredInit` (1 bit). C# compile error if the field is not set in object initialization; XIL2CPP emits a runtime check in the generated constructor for the C++ side."

- **C# nullable reference types `string?` / `SomeXObject?`** (`XCore-4b.html` line 722): "XIL2CPP preserves [`CPF_NullableReferenceType` bit] through the C++ emit boundary so C# consumers can tell which reference properties are declared-nullable vs not."

- **C# nullable value types `int?` / `FVector?`** (`XCore-4b.html` line 723): "In MVP, XIL2CPP transpiles `int?` to `XOptional<int32_t>` (a XCore-4a value type) without a reflection-level nullable annotation."

- **C# `async Task` / async state machine** (`XCoreXObject.html` §10.7.2 line 2448-2461): emit state-machine struct with captures; XGCRootSpan over captures at `MoveNext` entry; unregister at completion.

- **C# `IDisposable` mapping on XObject** (`XCoreXObject.html` §10.7.3 line 2463-2465): `XObject` implements `IDisposable`; `Dispose()` calls `obj.MarkForKill()` (alias for `EObjectFlags::MarkedAsGarbage`); raw destructor call forbidden.

- **C# interfaces implemented by transpiled class** (`XCore-4b.html` line 1091): "XIL2CPP emits a C++ class with virtual methods matching the C# interface declaration; dispatch is via the emitted C++ vtable, structurally identical to the C++-user-class shape above. The transpiler ensures the vtable layout matches the C++-declared interface."

- **C# `Action<T>` / `Func<T>` binding** (`XHT.html` §17.4 line 1611, 1615; Rev 3 diagnostic XHT123): not yet authorized by Contract §1.1; until then XHT emits diagnostic `XHT123 — Action<T> / Func<T> field requires explicit [XDelegate] marker; see Contract §1.1.` XIL2CPP downstream consequence pending Contract amendment.

- **`#line` directives** (`XBT.html` §10.3 line 1484): "XIL2CPP propagates these [reproducibility flags] through to the generated C++ headers (e.g. predicating `#line` directives so cross-machine line-number consistency holds)."

---

## Section 3 — APIs XIL2CPP MUST CONSUME

### 3.1 GC ABI (XCore-4a + XCoreXObject)

- **`XGC_RegisterRootSpan(XGCRootSpan* span)`** (`XToolchainContract.html` §3.1 line 562).
- **`XGC_UpdateRootSpan(XGCRootSpan* span, void** newBase, size_t newCount)`** (line 568).
- **`XGC_UnregisterRootSpan(XGCRootSpan* span)`** (line 573).
- **`XGC_WriteBarrier(void** slot, void* newValue)`** (`XCoreXObject.html` §5.2 line 1207).

`XGCRootSpan` struct (line 547-555):
```
struct XGCRootSpan {
    void**       base;
    size_t       stride;
    size_t       count;
    XGCRootKind  kind;
    uint32_t     _padding;
};
```

`XGCRootKind` enum (line 581-586):
```
enum class XGCRootKind : uint8_t {
    Strong,
    Weak,
    Conservative
};
```

### 3.2 XObject allocation

- **`NewObject<T>(Outer, Name, Flags)`** — infallible-style; returns `T*` (`XCoreXObject.html` §3.5 line 820, 830).
- **`TryNewObject<T>(Outer, Name, Flags)`** — fallible-style; returns `Result<T*, FNewObjectError>` (line 821).
- **Allocator backbone**: `g_XObjectAllocator.AllocateRaw(cls->PropertiesSize, cls->MinAlignment, cls)` (line 848).
- **FXObjectArray slot reservation**: `g_XObjectArray.ReserveSlot(&serial)` (line 853).
- **FXObjectArray binding**: `g_XObjectArray.BindObject(internalIndex, obj)` (line 883).

### 3.3 XGCRoot pinning API

- **`XGCRoot::AddRoot(XObject*)`** (`XCoreXObject.html` §5.3 line 1236): pin as root.
- **`XGCRoot::RemoveRoot(XObject*)`** (line 1237).
- **`XGCRoot::RegisterStaticRootVisitor(FRootVisitor)`** (line 1243) for non-array native slots.

### 3.4 XPACT_GC_STORE macro (XCoreXObject §5.2)

- **C++ user-class C# write barrier macro** (`XCoreXObject.html` §5.2 line 1197, 1027): The macro form `XPACT_GC_STORE(parent_obj, &slot, newValue)`. XIL2CPP emits equivalent inline at every C# write site (see §2.6 above).

### 3.5 Determinism guards (XPACT_CHECK_SL)

- **`XPACT_CHECK_SL(condition)`** (`XCoreXObject.html` §2 line 301, §3.5 line 845): a compiled-in guard active on sim-path TUs.
  - `XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU())` for SerialNumber read.
  - `XPACT_CHECK_SL(::XCore::HAL::IsSimPathThread() || !::XCore::HAL::IsSimPathTU())` for NewObject.
  - `XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU(), "MarkForKill is non-sim-path; …")` (line 329-330).

### 3.6 Hot-reload integration APIs

- **`XCoreXObject::ApplyClassReplacement(OldClass, NewClass)`** (`XCoreXObject.html` §5.6 line 1316): called for each replaced FClass.
- **`OnClassReplaced(FClassReplacementContext)`** delegate (line 2499) — fired via `XCoreDelegates`; XIL2CPP-emitted code that caches FClass* (e.g., generic instantiations) should listen.
- **`BeginHotReloadQuiesce(FHotReloadThreadEnumeration)`** / **`FinishHotReloadCascade(FClassReplacementMap)`** (line 1314, 1317).

### 3.7 Reflection runtime

- **`XReflectionRuntime::RegisterClass(const FClass*)`** (`XCoreXObject.html` §2.8 line 623).
- **`XReflectionRuntime::FindClass(name)`** (`XCoreXObject.html` §10.9 line 2489).

### 3.8 Manifest readers

- **FlatBuffers binary sidecar `Manifest.fbs.bin`** at `Intermediate/Build/<Target>/<Configuration>/` (`XToolchainContract.html` §10.2; `XBT.html` §8.5 line 1298). `file_identifier "XMFT"`. Read via `mmap` zero-copy with the verifier limits (line 1284-1287).
- **JSON fallback `Manifest.json`** (same path) when binary is missing/fails verification (`XToolchainContract.html` §12.12 line 2243).

### 3.9 Per-target settings

Per `XToolchainContract.html` §4.5 line 885: "XBT also propagates a per-target settings struct to XHT and XIL2CPP — the analog of UE's `UHTTargetSettings`. The struct carries `SimPathConservativeRootsAllowed`, `FipsMode`, the contract version, the target's platform identifier, and the target's `SimdLevel` default. XHT and XIL2CPP read this in addition to the per-module manifest."

### 3.10 Cross-module NoThrow lookup

- XHT-emitted reflection metadata exposes per-function `NoThrow` annotation (`XToolchainContract.html` §5.2 line 934-935; `XHT.html` §17.2 line 1576): "Phase 2 NoThrow lookup (Contract Section 5.2 N2) consults XHT's emitted reflection metadata for cross-module callee classification."

### 3.11 String interning storage

- **FName shared table** (`XToolchainContract.html` §6.3 line 1124): "single shared FName table across XHT-generated and XIL2CPP-generated callers, with stable indices across hot-reload."
- **`.rodata` FString constants for C# literals** (`XCore-4a.html` line 1434): "C# string literals compile to `constinit const FString` objects placed in the DLL's `.rodata` segment."

### 3.12 ABI envelope tags (compile-time validation)

Per `XToolchainContract.html` §10.2 line 1821: "The per-`.gen.cpp` emit also pins the manifest's GC root / exception / mangling tags to runtime macros in `XReflectionRuntime.h` via compile-time `static_assert`s so a runtime rebuilt against a different ABI fails the compile loudly." XIL2CPP-emitted `.cpp` must include analogous pins.

### 3.13 XPactDetail::CompileTimeStrEq (Stage B layout pins)

Per `XToolchainContract.html` §14.3 line 2423: "The macro `XPactDetail::CompileTimeStrEq` is a `constexpr` string-equality helper defined in `XReflectionRuntime.h` (preserved verbatim from the Stage-A stub for backward compatibility with the existing GC-root / exception / mangling pins)." Every transpiled `.cpp` referencing FProperty / FStruct / FClass etc. must carry the 15 layout-tag content pins + 14 sizeof pins (`XToolchainContract.html` §14.1-14.2).

---

## Section 4 — IL Semantic Mapping Rules

Format: `**C# construct** → **C++ emit**: cite.`

### 4.1 Type-level mappings

- **`class Foo : XObject { ... }`** → **`class XFoo : public ::XCore::Reflect::XObject { ... }`** with FClass static-init (`XCoreXObject.html` §10.7 line 2408): "A C# `class MyActor : Actor { ... }` emits a C++ `class MyActor : public XActor { ... }` with the same XCLASS macro and XPROPERTY/XFUNCTION markers."

- **`record Foo(int X, int Y)`** → FScriptStruct with FProperty entries; HasIdentical capability set (`XCore-4b.html` line 725).

- **C# struct (value type)** → C++ struct with stack-map registration for embedded XObject fields (`XCoreXObject.html` §10.7.4 line 2469-2473).

- **C# interface declared `[XInterface]`** → C++ pure-virtual class with FInterface descriptor (`XToolchainContract.html` §1.1 line 294).

- **C# enum declared `[XEnum]`** → C++ scoped enum class with FEnum descriptor (`XToolchainContract.html` §1.1 line 293).

### 4.2 Method-level mappings

- **C# instance method `T Foo.Bar(...)`** → free `extern "C"` function with explicit `XObject* self` parameter (`XToolchainContract.html` §2.3 line 494).

- **C# virtual method** → generated dispatcher free function performing vtable lookup + tail-call to resolved free function (line 496).

- **C# static method** → free function with `self = nullptr` (line 957 shim signature comment).

- **C# constructor (`.ctor`)** → method named `$ctor` with appropriate signature (`XToolchainContract.html` §2.2 line 489).

- **C# static constructor (`.cctor`)** → method named `$cctor` (line 489).

- **C# operator overload** → CLR-convention method name: `op_Add`, `op_Equality`, etc. (line 489).

- **`[XFunction(NoThrow = true)]`-annotated method** → Tier 2 direct call if proof succeeds, else build error exit 63 (`XToolchainContract.html` §5.2 line 942).

- **`[XFunction(CanThrow = true)]`-annotated method** → forced Tier 1 shim (line 944).

### 4.3 Reference-store mappings

- **`obj.Field = value;` where `Field` is XObject reference** → `XPACT_GC_STORE(parent, &slot, value)` equivalent inline (`XCoreXObject.html` §5.2 line 1199-1210; §10.7 line 2409).

- **`obj.Field = value;` where `Field` is value type or non-reference** → plain store (no barrier).

### 4.4 Container mappings

- **`List<T>` where T is XObject-derived** → `TArray<XPtr<T>>` with `XGCRootSpan` (Strong kind), move-only (`XCoreXObject.html` §10.7 line 2411).

- **`List<T>` where T is value type** → `TArray<T>` no GC scaffolding (`XToolchainContract.html` §3.2 line 592).

- **`List<object>`** → `TArray<void*>` with `XGCRootSpan` (Conservative kind) + build-time sim-path warning (`XToolchainContract.html` §3.2 line 593; `XCoreXObject.html` §10.7 line 2412).

- **`Dictionary<K, V>`** → `TMap<K', V'>` with `XGCRootSpan` if K or V holds XObject refs (`XHT.html` §17.4 line 1608; `XCoreXObject.html` §10.7 line 2411).

- **`HashSet<T>`** → `TSet<T'>` (`XHT.html` §17.4 line 1609).

- **`int[]`** → `TArray<int>` (no GC; pure RAII).

### 4.5 String mappings

- **C# `string`** → `XCSharpString` value-type handle, 8 bytes, `const FString*` storage (`XToolchainContract.html` §6.1 line 1098).

- **C# string literal `"foo"`** → `const FString*` in `.rodata` (`XToolchainContract.html` §6.2 line 1110; `XCore-4a.html` line 1434).

- **`nameof(X)`** → same `const FString*` as literal `"X"` (line 1110, 1117).

- **`s[i]` on sim-path TU** → **build-time error exit 63** unless `[XFunction(SimPathStringIndexOK = true)]` (`XToolchainContract.html` §6.4 line 1128).

- **`s.AsSpan()` / `s.IndexOf(c)` / `s.Substring(i, n)` / `s.AsCodepoints()`** → idiomatic; no special emission rules beyond what `XCSharpString` exposes (`XToolchainContract.html` §6.4 line 1132-1141).

### 4.6 Generic mappings

- **Closed generic type tree walk** (`XCoreXObject.html` §10.7.1 line 2417-2424): emit typed XGCRootSpan if any transitive member is XObject-derived; emit Conservative if `object` or open-typed; emit nothing if pure-value.

- **Recursion bound**: max depth 16 (`XCoreXObject.html` §10.7.1 line 2429); exceeded → **build-time error `XIL2CPP123 — generic instantiation closure exceeds depth 16`**.

- **Cycle detection**: HashSet of visited type instantiations (line 2430); cycle → **build-time error `XIL2CPP124 — cyclic generic type closure detected`**.

### 4.7 C# language-feature mappings

- **`async Task` / `await`** → state-machine struct + XGCRootSpan over captures at `MoveNext` (`XCoreXObject.html` §10.7.2 line 2457-2459). **Open question: thread-affinity of awaiter resumption on sim-path — see Section 10, Q4.**

- **`IDisposable` on XObject** → `Dispose()` calls `MarkForKill()` (`XCoreXObject.html` §10.7.3 line 2463). Raw `~XObject()` call forbidden.

- **`using` block on XObject** → IDisposable surface; same mapping (line 2465).

- **`required` members** → constructor runtime check + `CPF_RequiredInit` bit (`XCore-4b.html` line 726).

- **`int?` / `Vec?` (nullable value types)** → `XOptional<T>` (MVP; no reflection-level annotation) (`XCore-4b.html` line 723).

- **`string?` / nullable XObject refs** → reference + `CPF_NullableReferenceType` bit (`XCore-4b.html` line 722).

- **`Action` / `Func<T>` delegates** → `[XDelegate]` translation (currently emits diagnostic XHT123; pending Contract §1.1 amendment) (`XHT.html` §17.4 line 1611, 1615).

- **`try / catch / finally`** → Tier 1 only; lowered to native C++ exception in Dev/Test, `Result<T,E>` early-return in Shipping (`XToolchainContract.html` §5.6-5.7 line 1001-1029).

- **`throw expr`** in Tier 2 body → impossible by classification (line 924); forces Tier 1.

- **`lock(obj)` / `Monitor.Enter` / `Monitor.Exit`** → **NOT EXPLICITLY DOCUMENTED. See Section 10, Q5.**

- **`unsafe` blocks / pointer arithmetic** → **NOT EXPLICITLY DOCUMENTED. See Section 10, Q6.**

- **Reflection API (`typeof`, `GetType`, `Type.GetMethods`)** → partial via XReflectionRuntime; full Reflection.Emit is **post-MVP** (`XCoreXObject.html` §1.3 line 73: "Roslyn source-generator hosting … deferred to post-MVP"). **See Section 10, Q7.**

- **`unchecked` arithmetic** → standard C++ semantics (overflow undefined for signed; wraps for unsigned). **Not documented; see Section 10, Q8.**

- **`checked` arithmetic** → **NOT DOCUMENTED. See Section 10, Q8.**

- **`fixed` statement (pinning)** → **NOT DOCUMENTED.** XPact uses non-moving GC (`XCoreXObject.html` §1.3 line 70 — non-moving is locked) so pinning is trivially satisfied; **see Section 10, Q9.**

- **`stackalloc`** → likely maps to `alloca` or stack array; **NOT DOCUMENTED. See Section 10, Q10.**

- **Lambda capturing XObject*** → likely emits state-machine struct similar to async with XGCRootSpan over captures; **NOT EXPLICITLY DOCUMENTED. See Section 10, Q11.**

- **`yield return` iterator** → state-machine; **NOT DOCUMENTED. See Section 10, Q12.**

### 4.8 Allocation site mapping

- **`new Foo(args)` where Foo is XObject-derived** → `NewObject<XFoo>(outer, name, flags)` (`XCoreXObject.html` §3.5 line 830) — outer/name/flags derivation from the C# construction site is **not explicitly documented; see Section 10, Q13.**

- **`new Foo(args)` where Foo is C# struct or non-XObject class** → stack-construct or heap-allocate via standard C++ semantics (NOT GC-managed).

---

## Section 5 — Sim-path TU emission (the determinism story)

### 5.1 Module-level sim-path declaration

A module is sim-path iff its descriptor declares `sim_path = true` in TOML or `SimPath = true` in `.Build.cs` (`XToolchainContract.html` §4.1 line 781). The flag propagates to all TUs in the module; per-file overrides are not supported in MVP (line 790: "If a single file in a non-sim-path module legitimately needs sim-path semantics, the file must be split into its own micro-module").

### 5.2 Compile flags forced on sim-path TUs

Per `XToolchainContract.html` §4.2 line 811-839 (excerpts):

- MSVC: `/fp:precise`; banned `/fp:fast`, `/fp:except`. SIMD ≤ SSE42.
- Clang Linux: `-ffp-contract=off`, `-fno-fast-math`, `-fno-finite-math-only`, `-mno-fma`; banned `-ffast-math`, `-funsafe-math-optimizations`, `-ffp-contract=fast`/`on`, `-mfma`.
- Clang Android ARM64: same + `-mllvm -enable-fp-contract=false`.
- SIMD constrained to ≤ SSE42 (`None`, `SSE2`, `SSE42`, or `Default → SSE42`).
- `PCHUsage = NoSharedPCHs` (forced) (line 786).

### 5.3 XIL2CPP-emitted runtime checks on sim-path TUs

Per `XCoreXObject.html`:

- **`XPACT_CHECK_SL(::XCore::HAL::IsSimPathThread() || !::XCore::HAL::IsSimPathTU())`** at every NewObject call (`XCoreXObject.html` §3.5 line 845): "Sim-path TUs must call NewObject only from the SimPathSerialExecutor thread. Non-sim-path TUs may call from any thread."
- **`XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU())`** on every read of `XObject::GetSerialNumber()` (line 301).
- **`XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU(), "MarkForKill is non-sim-path; the next-GC-sweep timing is non-deterministic")`** at `MarkForKill` entry (line 329-330).
- **`XObjectKey::GetHashCode()` forbidden on sim-path** — guarded analogously (line 341).
- **FXObjectArray iteration order forbidden on sim-path** (line 2956 fix description).

### 5.4 Build-time errors on sim-path TUs

- **`s[i]` on sim-path** → error (suppressible via `[XFunction(SimPathStringIndexOK = true)]`) (`XToolchainContract.html` §6.4 line 1128).
- **Banned-API check on preprocessed text** (`XToolchainContract.html` §4.4 line 861-881): post-preprocessor regex against `XSimPathBannedAPIs.txt` (`GetSystemTime`, `GetTickCount`, `QueryPerformanceCounter`, `gettimeofday`, `clock_gettime`, `GetCurrentThreadId`, `rand`, `srand`, etc.); exit code 41.
- **`SimdLevel = AVX` or higher on sim-path module** → build error exit 41 (line 847).
- **`PCHUsage != NoSharedPCHs` on sim-path module** → build error (line 419).
- **Conservative XGCRootSpan on sim-path** → build-time warning category `XIL2CPP-Conservative`, deduplicated per closed-type instantiation (`XCoreXObject.html` §10.7.1 line 2439-2446).

### 5.5 Acceptance gates

- **X-DET** (`XCoreXObject.html` §13.2 line 2840): "cross-arch determinism invariant guards. Test that compiled-in `XPACT_CHECK_SL` fires on every sim-path TU that attempts to read `XObject::GetSerialNumber()` or hashes an `XObjectKey`. Pass criterion: 100% of sim-path TUs guarded; no false-negatives in static analysis pass."
- **X-NEW** (`XCoreXObject.html` §13.2 line 2841): "sim-path NewObject runtime invariant guards. Test that `XPACT_CHECK_SL(IsSimPathThread() || !IsSimPathTU())` fires on every sim-path NewObject call from a non-SimPathSerialExecutor thread. Pass criterion: assert fires in Dev; abort in Shipping; no false-negatives."

### 5.6 Foundation Prototype Criterion (c) — Cross-arch determinism

`XToolchainContract.html` §12.6 line 2195: "A module declared `SimPath = true` in its descriptor compiles with `/fp:precise` + `-ffp-contract=off` + `-mno-fma` + Sleef-vendored transcendentals + the banned-API regex check on preprocessed output (Section 4.4) + `NoSharedPCHs` + `SimdLevel ≤ SSE42`."

Foundation Prototype `(c)` Cross-arch determinism: "validates Section 4 flag propagation + Sleef substitution + full reproducibility envelope per Section 2.1." Maps to 100 replays on Win64 + Quest 3 with bit-exact state (`XCoreXObject.html` §13.1 line 2815).

---

## Section 6 — Hot-reload integration

### 6.1 Symbol stability (RVA-rewrite trampoline)

- **Per `XToolchainContract.html` §12.2 line 2163**: "Running XIL2CPP twice over the same input `.cs` source produces byte-identical mangled output symbols. A method in build N+1 has the same symbol name as in build N if its signature is unchanged."
- **Maps to Foundation Prototype criterion (g)**: "Critical for XLiveCoding's RVA-rewrite trampolines — the trampoline target's address resolves by symbol name" (line 2167).
- **Free-function form with explicit self is load-bearing** (`XToolchainContract.html` §2.3 line 496): "This makes Live-Coding-style RVA-rewrite trampolines work cleanly — the patcher writes one jump at the function's entry point, without vtable-slot rewriting."

### 6.2 Vtable stability across patches

- Per `XToolchainContract.html` §2.3 line 496: "no virtual-method signature changes are permitted mid-session, so the vtable layout is stable across patches."
- Tier 2 functions are not hot-reloadable (line 974) — only Tier 1 (`extern "C"` shim form) gets RVA trampolines.

### 6.3 Container type layout freeze (Section 3.7)

- Per `XToolchainContract.html` §3.7 line 737-739: "Container class layouts (any class with an embedded `XGCRootSpan` member) are **layout-frozen within a hot-reload session**. XLiveCoding rejects any patch that modifies a container type's class layout."
- XIL2CPP emits `container = true` class-descriptor flag on every such type (`XToolchainContract.html` §3.7 line 759).
- Patch-validation pass compares field-offset list field-by-field; mismatch fails patch with exit codes 90-99 (Live Coding domain).
- **Allowed**: function-body changes; adding new non-virtual methods (line 743-744).
- **Disallowed**: adding/removing/reordering data fields, relocating `m_rootSpan`, changing span `kind`, changing stride (line 749-752).

### 6.4 Per-FClass sub-pool rebind

- Per `XCoreXObject.html` §3.6 line 917: "The class replacement protocol (§9.2) includes invalidating the OldClass's sub-pool entry in FXObjectAllocator. Cells in the OldClass sub-pool are migrated to the NewClass sub-pool (the cells themselves don't move; only the sub-pool ownership changes). This preserves the type-segregation invariant across hot-reload."

### 6.5 Preserved XObjectKey resolution

- Acceptance gate **X6** (`XCoreXObject.html` §13.2 line 2833): "hot-reload class replacement preserves XObjectKey resolution for 100% of instances in a synthetic 10k-instance test."
- XIL2CPP must not embed identity assumptions that break under hot-reload — i.e., must use FName indices / XObjectKey, not raw pointers, for cached references that survive a patch.

### 6.6 OnClassReplaced delegate

- `XCoreXObject.html` §10.10.1 line 2499: "When a class is replaced by hot-reload, XCoreXObject fires `OnClassReplaced(FClassReplacementContext)` delegate via XCoreDelegates."
- Generic instantiations that cache `FClass*` (per `XCore-4b.html` line 724) must listen and invalidate.

### 6.7 Phase 1 vs Phase 2 hot-reload

Phase 1 patches reuse same vtable layout; Phase 2 (close-reopen) is required for layout-changing patches (`XToolchainContract.html` §9.7 line 1667).

### 6.8 Foundation Prototype criteria

- **(d) Single-method hot-patch < 30s** (`XToolchainContract.html` §12.19 line 2305): "validates Section 2 symbol mangling stability + XLiveCoding's patch-DLL discovery."
- **(e) Cascade hot-patch < 120s nominal / < 150s timeout** (line 2306): "validates the XLiveCoding cascade orchestration's interaction with Section 9 module-graph topology."

---

## Section 7 — Build system integration (XBT pipeline)

### 7.1 Invocation form

`XIL2CPP.exe -Manifest=<JSON path> -ManifestBin=<FBS path> -Module=<ModuleName>` (`XBT.html` §10.1 line 1472-1474).

Per `XHT.html` §17.3 line 1582: "XIL2CPP's own subprocess is launched by XBT with the manifest path passed via the same `-Manifest` arg XHT uses."

### 7.2 Action graph placement

- **`XIL2CPPAction`** = `XActionType` ordinal 4 (`XBT.html` §5.1 line 765; `XToolchainContract.html` §10.3 line 2039).
- **One per module containing C# sources** (`XBT.html` §10.5 line 1492).
- **Concurrent with `XHTAction` / `EmitReflectionAction`** for the same module (`XToolchainContract.html` §10.3 line 2039: "**Not prerequisite to XHTAction.**"; `XBT.html` §10.6 line 1496-1498).
- **Prerequisite to `CompileCppAction`** for the module's transpiled `.cpp`s (`XToolchainContract.html` §10.3 line 2040).
- **`Tier2WholeProgramPass`** = `XActionType` ordinal 13 (`XBT.html` §5.1 line 781): post-MVP optional whole-program XIL2CPP Tier 2 promotion pass; consumes every per-module `XIL2CPPAction`; produces `TierTable.json` + per-module re-emit deltas.

### 7.3 Per-action inputs / outputs

Per `XToolchainContract.html` §10.3 line 2039 + `XBT.html` §10.5 line 1492:

- **Inputs**: manifest (JSON + FBS) + every `.cs` file in module + XIL2CPP's own binary content hash + XCoreXObject's exported header surface (for GC ABI emit).
- **Outputs**: transpiled `.cpp`/`.h` in module's intermediate directory + per-module transpilation manifest + (Pass 1) `TierTable.partial.<Module>.json`.

### 7.4 Incremental / caching

- **Per-action cache key** (`XBT.html` §15.1 line 1687): "XIL2CPP binary: content hash of `XIL2CPP.exe`."
- **`CacheKeyComponents`** of `IExternalAction` (`XBT.html` §5.2 line 827; `XToolchainContract.html` §10.3 line 2059): "explicit cache-key components beyond what CommandVersion captures. Examples: SimdLevel, FpSemantics-derived flag set, environment FIPS mode."
- **`DependencyListFile`** (Phase 2 hook; `XBT.html` §5.2 line 829-836): "names a per-action file that lists the action's transitive header dependencies. Phase 2's `CppDependencyCache` will read this file."
- **Manifest binary sidecar** is read mmap zero-copy via FlatBuffers (`XToolchainContract.html` §10.2 line 1823-1825): "XHT and XIL2CPP receive the binary path on the command line; they `mmap` the file and read fields zero-copy. Because both forms are written atomically from the same C# manifest object on every build, the two are always structurally equivalent at any observation point."

### 7.5 Per-target ABI envelope (load-bearing for XIL2CPP)

Per `XBT.html` §8.6 line 1220 + `XToolchainContract.html` §10.2 line 1907-1922 (`TargetInfo` table):
- `gc_root_abi: string` — Phase 1 `"Span-based v1"`. XIL2CPP must emit calls per this version.
- `exception_abi: string` — Phase 1 `"Tier1-Shim/Tier2-Direct"`.
- `mangling_scheme: string` — Phase 1 `"Itanium-LengthPrefixed-v1"`. XIL2CPP validates the scheme against its supported set and surfaces diagnostic on mismatch (analogous to XHT124 in `XToolchainContract.html` §10.2 line 1821).
- `architecture: string` — `"x86_64"` or `"aarch64"`.
- `station_role: StationRole` — Engineer / Instructor / Trainee.
- `fips_mode: bool`.
- `sim_path_conservative_roots_allowed: bool`.
- `simd_level_default: SimdLevel`.

### 7.6 Per-module manifest fields (load-bearing)

Per `XBT.html` §8.3 line 1232 + `XToolchainContract.html` §10.2 FBS schema (line 1938-1962):

- `name: string`.
- `tier: ModuleTier` (Engine / Studio / Project).
- `module_type: ModuleType`.
- `languages: Languages` (bit_flags: Cpp=bit0, CSharp=bit1) — XIL2CPP processes modules where bit 1 set.
- `base_directory: string`.
- `source_files: [SourceFile]` (each with `is_csharp`, `is_header`, `is_test_only` flags).
- `csharp_sources: [string]` — populated explicitly (`XBT.html` §8.3 line 1249).
- `include_paths: [string]`.
- `public_defines: [string]`.
- `module_dependencies: [ModuleDep]` (each with `name`, `interface_module`, `is_dynamic`).
- `generated_cpp_filename_base: string`.
- `sim_path: bool`.
- `simd_level: SimdLevel`.
- `pch_usage: PCHUsageMode`.
- `b_exclude_from_shared_pch: bool`.
- `b_allow_hot_reload: bool`.

### 7.7 Per-module pipelining

- Once XHTAction(M) finishes AND XIL2CPPAction(M) finishes → `CompileCppAction` for M's TUs can start (`XToolchainContract.html` §10.1 line 1713).
- XHTAction and XIL2CPPAction are NOT prerequisites of each other (line 1713, 2039).

### 7.8 Reproducibility flag propagation

Per `XBT.html` §10.3 line 1484: "XBT forwards the platform's reproducibility flags to XIL2CPP via the manifest. XIL2CPP propagates these through to the generated C++ headers."

XIL2CPP's own binary must be built with the reproducibility envelope (`XToolchainContract.html` §2.4 line 526): "The reproducibility flags … propagate from XIL2CPP's own build of itself, not just the user's build — XIL2CPP's binary is itself reproducible."

### 7.9 Exit codes

Per `XToolchainContract.html` §13.1 line 2326-2349:
- `61` — XIL2CPP subprocess failure (XBT aggregator code).
- `63` — "XIL2CPP analysis (e.g., NoThrow proof) or emit failure (returned to XBT as 61)."
- `41` — SimPath banned-API check failure.
- `30` — RulesAssembly compile failure.

Per `XBT.html` §3.2 line 258: Phase 1 stub returns exit code 24 (`PluginNotFound`).

### 7.10 In-process vs subprocess

Per `XToolchainContract.html` §7.1 line 1172: "XHT runs as a subprocess of XBT (or as an in-process library — XBT's choice, internal). XBT writes a JSON manifest + binary sidecar (see Section 10) and either passes the binary sidecar's path on the command line or calls XHT's in-process entry point with the loaded manifest." Same applies to XIL2CPP.

Per `XBT.html` §6.4 line 2053: "In-process XHT and XIL2CPP (the Phase 2 perf option per Section 9 / Section 10) receive the cancellation token through their entry-point signature."

---

## Section 8 — Reflection consumption (XHT integration)

### 8.1 Where XHT ends and XIL2CPP begins

Per `XHT.html` §17.2 line 1565-1574:
- XHT parses the `.cs` file and produces a reflected type record per `[XClass]`-attributed class.
- XHT emits the **C++ side** in `{Header}.gen.cpp`: FClass descriptor + FProperty descriptors for `[XProperty]`-attributed fields.
- XIL2CPP, running independently, transpiles the same `.cs` file's **body**.
- The two converge at the C++ compile step: transpiled `.cs.cpp` `#include`s `{Header}.gen.h` to pick up property accessors; the linker resolves singleton-getter references between transpiled and reflected sides.

### 8.2 XHT-emitted metadata XIL2CPP reads

- **FClass descriptors** for cross-module classes (`XCoreXObject.html` §2.8 line 622): name, super, property list head, lifecycle table pointer, class flags, etc.
- **FProperty descriptors** for each `[XProperty]` field (`XHT.html` §17.2 line 1571).
- **NoThrow annotation bit** on function descriptors (`XToolchainContract.html` §5.2 line 934-935; §1.2 line 351): consumed by Tier 1/Tier 2 classification.
- **FXObjectRefSchema opcode arrays** for each FClass / FStruct — XHT emits these per `XCoreXObject.html` §7.4.1 line 1773 ("XHT's emit pass walks `FClass.ObjectRefProperties` at compile time and emits one `FXObjectRefSchemaOp` per reference-carrying FProperty"). **Open question Q14: who emits the schema-vector definition for C#-declared classes — XHT or XIL2CPP?**
- **FXObjectLifecycleTable** for C++-declared classes (XHT emits) (`XCoreXObject.html` §2.8 line 621). For C#-declared classes, XIL2CPP must emit the matching table per the cross-tool symbol contract.

### 8.3 Cross-language type pair manifest

Per `XHT.html` §17.3 line 1580: "For each cross-language type pair (a C++ class + a C# partial class declaring the same type), the `.gen.manifest`'s `[Types]` section emits both source files. This lets XIL2CPP see the pairing at transpile time so the IL2CPP-emitted body code can target the same singleton-getter the C++ side references."

### 8.4 Binding table (shared library)

Per `XHT.html` §17.4 line 1617: "The binding table is the canonical source for the C# ↔ C++ correspondence. XHT consults it at emit time; XIL2CPP consults it at transpile time. Both tools share the table via the shared `XPact.Mangling` NuGet package referenced by both."

C# → C++ binding table (`XHT.html` §17.4 line 1590-1612): primitives map as expected (`bool`→`bool`, `int`→`int32`, `float`→`float`, `string`→`FString`, etc.); `List<T>`→`TArray<T'>`; `Dictionary<K,V>`→`TMap<K',V'>`; `HashSet<T>`→`TSet<T'>`; XObject subclass → C++ class name or forward-declared placeholder with `FObjectProperty`.

### 8.5 What XIL2CPP does NOT consume

Per `XToolchainContract.html` §10.2 line 2029: "Neither parses any other tool's binary output during their own work. The manifest is the join key." XIL2CPP does NOT parse XHT's `.gen.h` content as data; it reads it via standard C++ include for the property accessors.

### 8.6 Schema-version coordination

Per `XHT.html` §17.3 line 1584: "STAGE B: the exact byte layout of the cross-language reflection metadata (the C# field's reflected FProperty descriptor on the C++ side) freezes at Step 4 with the rest of the FProperty surface. Phase 1 emits provisional layouts; XIL2CPP's transpiler honours the provisional shape via the schema-version field in the descriptor."

---

## Section 9 — Acceptance criteria + Foundation Prototype mapping

### 9.1 Foundation Prototype criteria touching XIL2CPP

Per `XToolchainContract.html` §12.19 line 2297-2311 + `XCoreXObject.html` §13.1 line 2810-2823:

- **(a) GC pause < 5 ms typical Quest 3** — XIL2CPP must keep barrier emit cost ≤ 5 cycles (X4 in `XCoreXObject.html` §13.2 line 2831) and stack-map per-function size budget ≤ ~30 bytes (`XCoreXObject.html` §5.4 line 1292).
- **(b) Full-heap-scan fallback < 50 ms** — XIL2CPP's schema-vector emit must be correct so the fast-path walker covers >4000 refs/ms (X-SCHEMA gate, `XCoreXObject.html` §13.2 line 2844).
- **(c) Cross-arch determinism 100 replays** — XIL2CPP enforces sim-path TU runtime invariants (X-DET, X-NEW). See Section 5 above.
- **(d) Single-method hot-patch < 30 s** — XIL2CPP symbol mangling stability (`XToolchainContract.html` §12.19 line 2305).
- **(e) Cascade hot-patch < 120 s nominal / 150 s timeout** — XIL2CPP container layout-freeze + sub-pool rebind support (`XToolchainContract.html` §3.7; `XCoreXObject.html` §3.6).
- **(f) Reflection round-trip clean** — XIL2CPP transpiles property to getter/setter that the FProperty table dispatches to (`XToolchainContract.html` §12.11 line 2239: "XHT extracts the property from `[XProperty]`-attributed C# fields; XIL2CPP transpiles to a getter/setter pair the shared FProperty table can call; XCoreXObject's reflection runtime dispatches to them").
- **(g) Symbol stability** — primary XIL2CPP responsibility (`XToolchainContract.html` §12.1 line 2159, §12.2 line 2163). Maps to byte-identical mangled output symbols across repeated runs.
- **(h) Fragmentation < 15% over 4 hrs Quest 3** — XIL2CPP not directly responsible; allocator owns (`XCoreXObject.html` §13.1 line 2820).
- **(i) Editor cold-start ≤ 15 s on 1000-asset project** — XIL2CPP must work correctly with lazy CDO (`XCoreXObject.html` §13.1 line 2821).

### 9.2 XCoreXObject-specific XIL2CPP gates

Per `XCoreXObject.html` §13.2 line 2827-2845:

- **X10** (line 2837): "precise stack scanning enumerates 100% of XIL2CPP-emitted XObject locals on Win64 + Quest 3 in a synthetic test with 100 nested transpiled functions, each with 1-5 stack-resident XPtrs."
- **X-DET** (line 2840): every sim-path TU SerialNumber/XObjectKey access guarded.
- **X-NEW** (line 2841): sim-path NewObject runtime invariant fires from non-SimPathSerialExecutor.
- **X-INIT** (line 2842): NewObject before `EInitPhase::PostStaticInit` asserts.
- **X-INIT-OBJ** (line 2843): FXObjectInitializer destructor fires PostInitProperties exactly once.
- **X-SCHEMA** (line 2844): schema-vector walk > 4000 refs/ms Quest 3.
- **X-INIT-EAGER** (line 2845): EagerCDO construction at PostStaticInit.

### 9.3 Contract-level XIL2CPP verifications

Per `XToolchainContract.html` §12:

- **12.1 Reproducibility test** (line 2151): two clean builds → byte-identical outputs.
- **12.2 Hot-reload symbol stability** (line 2161): repeated XIL2CPP runs → byte-identical symbols.
- **12.3 Cross-config ABI** (line 2169): Dev shim signature = Shipping shim signature.
- **12.4 Calling-convention classification** (line 2177): Tier 2 known-noexcept method emits direct call; `[NoThrow]` with unsafe callee fails build.
- **12.5 GC span ABI race** (line 2185): mid-realloc mark observes consistent `(base, count)`.
- **12.6 Determinism flag-propagation** (line 2193).
- **12.11 Reflection round-trip** (line 2233).
- **12.14 Container move-only** (line 2257).
- **12.16 Tier 2 cross-module classification** (line 2273).
- **12.17 XResult destructor exception** (line 2281).

### 9.4 XIL2CPP-specific diagnostic codes

From `XCoreXObject.html` §10.7.1 line 2434-2435 + `XHT.html` §17.4 line 1615:
- `XIL2CPP123` — generic instantiation closure exceeds depth 16.
- `XIL2CPP124` — cyclic generic type closure detected.
- (`XHT123` — Action<T>/Func<T> requires explicit [XDelegate] — XHT-side but the rule blocks XIL2CPP emission.)
- Inferred: more `XIL2CPP1xx` diagnostics will be defined by Rev 1 author for: sim-path banned API at codegen layer, NoThrow proof failure, schema-vector emit failure, etc.

### 9.5 Exit code 61 / 63

Per `XToolchainContract.html` §13.1 line 2339-2341 + §13.3 line 2357: "XIL2CPP subprocess exits with codes 61-69 (63 is the canonical XIL2CPP analysis/emit internal failure; XBT translates child exit to 61 for aggregation)."

---

## Section 10 — Open design questions (CONTRADICTIONS / GAPS)

Each question is for the Rev 1 author to resolve. Some have a likely-correct answer in parens; the Rev 1 author should verify.

### Q0 — Document format

**Contradiction.** `README.html` line 10 states: "**No markdown is permitted anywhere in this repository.**" Yet this constraint inventory is in Markdown by the user's explicit instruction (to distinguish from a Rev 1 design doc). Resolution: this file is a working / harvest artifact, not a system spec. The Rev 1 author MUST author `Documents/XIL2CPP.html` in HTML and may either (a) delete this Markdown file once the spec lands or (b) keep it as a research/audit-trail artifact and accept the policy deviation. **Recommend (a).**

### Q1 — C# language version coverage

**NOT DOCUMENTED.** None of the seven docs state which C# version XIL2CPP transpiles (12? 13? 14?). The `project_architecture` memory mentions ".NET 9 runtime" but that memory is 58 days old and may describe a superseded architecture (see Q3 below). `XCoreXObject.html` §1.3 line 73 notes "Roslyn source-generator hosting … deferred to post-MVP." `XHT.html` §3.5 uses Roslyn "at parse time only." Recommend Rev 1 commit to: **C# 12 (the latest stable at .NET 8) as MVP; .NET 9 once available**. Need user input.

### Q2 — C# language-subset coverage

**Multiple gaps.** Section 4.7 above lists features explicitly documented (async, IDisposable, required, nullable types, Action/Func, try/catch, throw, lock) vs. NOT documented (lock semantics on sim-path, unsafe, full Reflection API, checked/unchecked, fixed, stackalloc, lambda+capture, yield return iterators, LINQ, dynamic, partial methods semantics in transpiled context). Rev 1 author must enumerate the supported subset with clear rejection diagnostics for unsupported features.

### Q3 — Managed CLR coexistence vs replacement (CRITICAL CONTRADICTION)

**Contradiction with project_architecture memory.** The memory states: ".NET 9 runtime hosted via nethost/hostfxr APIs … P/Invoke with DisableRuntimeMarshalling, custom NativeMarshalling attributes … ScriptingObject (C++) ↔ Object (C#) bridging with GC handles and IntPtr." This is the *Unity-style hosted CLR pattern*.

The XPact docs describe a *transpilation* pattern: C# source → C++ at build time; no managed runtime hosted at game time. Per `XToolchainContract.html` §0 line 262: "XIL2CPP transpiles directly from C# source (not IL) and emits hot-patchable, hybrid-GC-aware code rather than Mono-runtime-bound code."

**These two architectures are incompatible.** Either:
- (a) The memory describes an older approach now superseded by XPact's transpilation-based design.
- (b) XIL2CPP transpiles *some* C# (sim-path / gameplay) while the managed CLR handles other parts (editor / non-deterministic).
- (c) The memory is stale (it is marked 58 days old).

**The Rev 1 author MUST resolve this with the user.** Per Prime Directive ("if you see something is wrong because of the user's uneducated answers, bring it up"): if the user is still thinking in terms of hosted CLR, the docs as written contradict that. Recommend: **transpilation is the canonical XPact path; hosted CLR is NOT in XPact**. This is consistent with the entire toolchain design (mangling, GC root spans, stack maps, etc.).

### Q4 — Async/await thread affinity on sim-path

`XCoreXObject.html` §10.7.2 line 2459 documents that XGCRootSpan stays live across await but does not specify which thread the continuation resumes on. For sim-path TUs, NewObject must happen on the SimPathSerialExecutor thread (X-NEW). If a `MoveNext` continuation resumes on a worker pool thread, any NewObject in that continuation violates X-NEW. Recommend: **sim-path async is banned, or `await` on sim-path must enforce continuation back to SimPathSerialExecutor**. Need user input.

### Q5 — `lock(obj)` / `Monitor.Enter/Exit` mapping

**NOT DOCUMENTED.** XPact has `FCriticalSection` (XCore-4a). Rev 1 author should specify: `lock(obj)` → RAII `FScopedCriticalSection`? What `obj` shape? Banned on sim-path? Recommend: **`lock` maps to `FCriticalSection` for non-XObject objects; XObject monitor pattern needs design; banned on sim-path**.

### Q6 — `unsafe` blocks and pointer arithmetic

**NOT DOCUMENTED.** C# `unsafe { void* p = &obj.Field; … }` raises GC interaction questions (does the pointer survive across a safepoint? what stack-map records it?). Recommend: **`unsafe` allowed for non-XObject types; XObject pointers inside `unsafe` must use the stack-map registration; full ban on sim-path TUs**.

### Q7 — Reflection runtime API (`typeof`, `GetType`, MethodInfo, etc.)

**Partially documented.** `XReflectionRuntime::FindClass(name)` exists. `typeof(T).Name` participates in literal interning (`XToolchainContract.html` §6.2 line 1110). `Type.GetMethods` / `MethodInfo.Invoke` etc. NOT documented. Recommend Rev 1: **`typeof` → const FClass*; reflection-only API surface limited to FClass/FProperty lookups; full System.Reflection.Emit is post-MVP per `XCoreXObject.html` §1.3 line 73**.

### Q8 — `checked` / `unchecked` arithmetic

**NOT DOCUMENTED.** Determinism story (`XToolchainContract.html` §4) covers FP but not integer overflow. Sim-path TUs need a consistent rule: either `checked` (throws on overflow) or `unchecked` (wraps). Recommend: **default `unchecked` (standard C++ semantics); `checked` block emits explicit overflow check; sim-path requires consistent rule across both architectures**.

### Q9 — `fixed` statement pinning

**NOT DOCUMENTED.** Non-moving GC (`XCoreXObject.html` §1.3 line 70) means pinning is trivially satisfied — but XPact does not document the language-level mapping. Recommend: **`fixed` is a no-op cast to raw pointer**.

### Q10 — `stackalloc` and `Span<T>`

**NOT DOCUMENTED.** `Span<T>` / `ReadOnlySpan<T>` appears in the C# string surface (`s.AsSpan()` in `XToolchainContract.html` §6.4 line 1132). `stackalloc` mapping unclear. Recommend: **`stackalloc T[n]` → `alloca(n * sizeof(T'))` or fixed C-array; `Span<T>` → custom value-type pair `{T*, size_t}`**.

### Q11 — Lambda capturing XObject*

**NOT DOCUMENTED EXPLICITLY.** Async state machine handling (§10.7.2) is the closest precedent. Recommend: **lambda capturing XObject* emits a state-machine struct with XGCRootSpan over captures, lifetime equals the lambda's lifetime**.

### Q12 — `yield return` iterators

**NOT DOCUMENTED.** Recommend: **same treatment as async — state machine struct + XGCRootSpan over captures across yield boundaries**.

### Q13 — `new XObject()` semantics in C#

**NOT EXPLICITLY DOCUMENTED.** `XCoreXObject.html` §3.5 line 830 documents `NewObject<T>(Outer, Name, Flags)`. But C# code writes `new MyActor()` without Outer/Name/Flags. How does XIL2CPP derive these? Recommend: **C# `new Foo()` for XObject-derived emits `NewObject<XFoo>(GetTransientPackage(), NAME_None, RF_NoFlags)`; C# `new Foo() { Outer = x, Name = "y" }` uses explicit initializer; or require explicit `XObject.New<Foo>(outer, name, flags)` factory**. Need user input.

### Q14 — Schema-vector emit ownership (XHT vs XIL2CPP for C#-declared types)

**AMBIGUITY.** `XCoreXObject.html` §7.4.1 line 1773 says "XHT's emit pass walks `FClass.ObjectRefProperties` at compile time and emits one `FXObjectRefSchemaOp` per reference-carrying FProperty." But `XToolchainContract.html` §10.2 line 1818 says "XIL2CPP emits the matching definition in its transpiled C++ output" for C#-declared types' singleton-getters. Does the schema-vector array `.rodata` live with XHT-emitted `.gen.cpp` or XIL2CPP-emitted `.cs.cpp`? Recommend: **XHT emits the schema vector for both C++ and C#-declared classes (since it has the full FProperty knowledge from its C# front-end); XIL2CPP emits only the singleton-getter function body and the `Z_PostInitProperties_*` etc. lifecycle slot implementations**. Need to formally confirm with `XHT.html` author.

### Q15 — FPSemantics enum NOT in manifest

Per `XToolchainContract.html` §10.2 line 1984: "`FPSemantics` enum is not in the schema (XBT resolves FP semantics from `ModuleRules` at flag-derivation time rather than persisting it through the manifest; XHT / XIL2CPP do not consume it)." Question: how does XIL2CPP know it's in a sim-path TU? Answer: via `Module.sim_path: bool` (line 1953). Just a documentation note — no real contradiction.

### Q16 — Two-pass tier classification interface

`XBT.html` §10.7 line 1505 describes per-module `TierTable.partial.<Module>.json` published by each per-module XIL2CPPAction; Pass 2 reads them and re-invokes XIL2CPP "in a whole-program-propagation mode." Recommend: Rev 1 author specify the CLI interface for Pass 2 mode + JSON schema for the partial / merged tier tables.

### Q17 — Outer / Name / Flags derivation rule

(See Q13.)

### Q18 — Conservative-root sim-path warning behavior

Conservative XGCRootSpan emits "build-time warning once per closed-type instantiation, deduplicated via XIL2CPP build cache" (`XCoreXObject.html` §10.7.1 line 2439). The dedup cache layout is internal to XIL2CPP — Rev 1 should document where the cache lives (incremental cache key? Manifest cross-build?).

### Q19 — `[XFunction(NoThrow = true)]` cross-module proof scope

Pass 1 conservatively treats cross-module callees as Tier 1 unless they carry NoThrow annotation. Question: what about C++-implemented functions called from C# transpiled code? Per `XToolchainContract.html` §5.2 line 925: "C++ interop wall — if the wall is crossed, the wall side must be Tier 1." But XHT can also emit NoThrow on `XFUNCTION(NoThrow = true)` C++ functions. Recommend: Rev 1 align C++ NoThrow annotation discovery with C# NoThrow.

### Q20 — Per-target `FPSemantics`, ABI envelope, and live coding hints in the binary sidecar

The FBS schema (`XToolchainContract.html` §10.2 line 1907-1922) carries `gc_root_abi`, `exception_abi`, `mangling_scheme` strings. XIL2CPP must reject manifests with unknown ABI tags loudly. Recommend: Rev 1 author specify the supported-set table for each tag and the diagnostic codes.

### Q21 — Container `T` typed walker emit for arrays of XPtr

Per `XCoreXObject.html` §10.7 line 2411: "C# generic collections (`List<XObject>`, `Dictionary<FName, XObject>`) are emitted as XCore-4a TArray / TMap with the GC-aware partial specialization (XGCRootSpan registration in constructor, unregister in destructor; XCore-4a §5.4)." Question: do those TArray / TMap partial specializations live in XCore-4a (one specialization per X type) or does XIL2CPP synthesize them per closed instantiation? Recommend: Rev 1 author specify (likely the latter, given `XCore-4b.html` line 724's "XIL2CPP emits one FClass per instantiation reached by the program").

### Q22 — Vtable layout for transpiled C# classes implementing C++ interfaces

Per `XCore-4b.html` line 1091: "XIL2CPP emits a C++ class with virtual methods matching the C# interface declaration; dispatch is via the emitted C++ vtable, structurally identical to the C++-user-class shape above. The transpiler ensures the vtable layout matches the C++-declared interface." Question: how does XIL2CPP know the C++ vtable layout? Via XHT-emitted FInterface descriptor? Rev 1 author specify the protocol.

### Q23 — Phase 1 stub vs Phase 6 (full XIL2CPP) entry point compatibility

The Phase 1 stub returns exit 24 (`PluginNotFound`) per `XBT.html` §3.2 line 258. Phase 6 must use a different exit-code discipline (codes 0 / 61 / 63). The CLI surface should be locked early so Phase 1 stub and Phase 6 real implementation use the same flag names. Recommend: Rev 1 author specify the CLI mode contract aligned with `run-xht` mode shape.

### Q24 — Master Plan reference

Sections of `XCoreXObject.html` and `XBT.html` cross-reference `/Master Plan §x.y` and the plan file lives outside the repo at `C:\Users\benne\.claude\plans\unreal-engine-source-code-lively-pillow.md` per `README.html` line 13. The Rev 1 author should consult that plan for any constraints not captured in the seven HTML docs (e.g., Section 2 row-by-row commitments cited inline throughout, Section 11 Foundation Prototype, Section 13 amendment row).

### Q25 — Compiler-version constraint for ABI

Per `XCoreXObject.html` §13.2 line 2828: "sizeof(XObject) == 56; offset locks per §11.3 hold on every supported compiler (MSVC 19.44+, Clang 17+, GCC 13+)." XIL2CPP's emitted C++ must compile under the same compiler matrix.

### Q26 — Banned-API check at codegen layer

Per `XToolchainContract.html` §4.4 line 877: "the regex pass runs against the TU's preprocessed output, not its raw source." XIL2CPP-emitted `.cs.cpp` will be preprocessed and fed through the banned-API check just like any other `.cpp`. Question: does XIL2CPP have its own banned-API list (e.g., for IL features that map to non-deterministic platform APIs)? Recommend: Rev 1 author specify the IL-level banned set in addition to the C++-level set.

### Q27 — In-process XIL2CPP and cancellation tokens

`XBT.html` §6.4 line 2053: "In-process XHT and XIL2CPP (the Phase 2 perf option per Section 9 / Section 10) receive the cancellation token through their entry-point signature." Rev 1 author specify the entry-point signature.

### Q28 — IL feature coverage matrix vs C# feature coverage matrix

Subtly: XIL2CPP could be either an "IL → C++" transpiler (reads compiled IL bytecode) or "C# AST → C++" transpiler (reads C# source via Roslyn). Per `XToolchainContract.html` §0 line 262: "transpiles directly from C# source (not IL)." This is a major architectural commitment. Rev 1 author must commit to: **C# source-level transpilation via Roslyn parse tree; no IL bytecode consumption**.

### Q29 — Two-pass tier classification: when does Pass 2 fire?

`XBT.html` §10.7 line 1506: Pass 2 fires "when the CLI flag `-EnableTier2WholeProgram` is set (default: off in MVP)". CLI flag belongs to XBT, not XIL2CPP. How does XIL2CPP know it's in Pass 2 mode? Likely a separate `-WholeProgramTier2` flag on the XIL2CPP CLI; Rev 1 author specify.

### Q30 — Foundation prototype scene specification

X-SCHEMA gate (`XCoreXObject.html` §13.2 line 2844): "schema-vector walk > 4000 refs/ms on Quest 3 (vs FProperty walk's ~3000)." The benchmark scene definition is in Master Plan §11 (external file); Rev 1 author should pull the benchmark spec into the design doc.

---

## Section 11 — Risks + UE comparisons

### 11.1 UE divergences XPact takes

Per `XToolchainContract.html` §11.2 line 2110-2134:

- **Transpilation vs hosted CLR**: UE uses Blueprint VM + Mono CLR side-by-side. XPact transpiles. Trade: long C++ build times in exchange for one runtime + hot-reload as RVA-rewrite.
- **Span-based GC root ABI**: UE uses single C++ GC + managed C#. XPact's hybrid model is the harder right answer per Prime Directive.
- **Free-function form with explicit self**: UE binds via vtable rewriting; XPact does it via RVA trampolines, simpler at the cost of an extra free-function indirection.
- **Itanium-ABI-style length-prefixed mangling**: UE's mangling has the under-escaped-underscore collision risk (`XToolchainContract.html` §1.4 line 383). XPact preempts.

### 11.2 UE patterns XPact adopts

Per `XToolchainContract.html` §11.1 line 2092-2107:
- `ModuleRules` base-class three-tier dependency model.
- JSON manifest as build-tool contract.
- `IExternalAction` graph for incremental builds.
- ConstInit-style emit for class descriptors.
- Per-target settings struct passed to XHT (extended to XIL2CPP).
- Body-macro suffix table.

### 11.3 Risk areas to flag for Rev 1

- **Reflection.Emit / dynamic code**: explicitly post-MVP per `XCoreXObject.html` §1.3 line 73. The Rev 1 design must NOT promise it.
- **Async sim-path**: thread affinity is unclear (Q4).
- **Memory pressure during Pass 1 → Pass 2 transition**: per-module partial tier tables can grow; Rev 1 should specify size limits.
- **Schema-version drift between XHT-provisional and XIL2CPP**: Stage A locks emit mode but not byte layout (`XToolchainContract.html` §7.1 line 1162). Rev 1 must handle the provisional-layout phase gracefully.
- **Lock-free safe-point handshake correctness**: relies on every transpiled function having a function-entry safe-point check (`XCoreXObject.html` §5.5 line 1301). If XIL2CPP misses a function (e.g., one created via reflection emit at runtime), GC mark phase will hang. Rev 1 must guarantee 100% coverage.

---

## Section 12 — Cross-system dependency graph

### 12.1 XIL2CPP depends on (must ship first)

| System              | Why                                                              | Cite                                            |
| ------------------- | ---------------------------------------------------------------- | ----------------------------------------------- |
| XCore-4a            | `XGCRootSpan` ABI; `FString`; `TArray`/`TMap`/`TSet`; FName base | `XCore-4a.html` §3, §5.4                        |
| XCore-4b            | FProperty subclass family; FStruct/FClass layouts; FInterface    | `XCore-4b.html` line 91                         |
| XCoreXObject (Sys 5)| XGCRoot::AddRoot; NewObject; XPACT_GC_STORE; stack-map readout   | `XCoreXObject.html` §1.5 line 179 ("XCoreXObject ↔ XIL2CPP (System 6; forward commitment FIX-A-MIN-47)") |
| XBT (Sys 1)         | Manifest emit; action graph; flag derivation; sub-process drive  | `XBT.html` §10                                  |
| XHT (Sys 2)         | FClass descriptors; FProperty descriptors; cross-tool symbol contract | `XHT.html` §17                            |
| Toolchain Contract  | Symbol mangling, ABI envelope, exception calling-convention tier | `XToolchainContract.html` §0-§14                |

### 12.2 XIL2CPP unblocks

| System            | Why                                                                       | Cite                                        |
| ----------------- | ------------------------------------------------------------------------- | ------------------------------------------- |
| XActor (Sys 7+)   | Gameplay-tier C# classes need transpilation                               | `XCoreXObject.html` §1.5 line 160           |
| XComponent        | Same                                                                       | Same                                        |
| XWorld            | Same                                                                       | Same                                        |
| XSerialization (Sys 9) | C# class Serialize slot implementations                              | `XCoreXObject.html` §10.7 line 2408         |
| XNetworking (Sys 13) | C# class replication interfaces                                          | `XCoreXObject.html` §10.10                  |
| Foundation Prototype (Step 5.5) | criteria (c), (d), (e), (f), (g) all require XIL2CPP working | `XToolchainContract.html` §12.19            |
| XLiveCoding (Phase 2) | RVA trampolines target XIL2CPP-emitted symbols                       | `XToolchainContract.html` §2.3 line 496     |
| XPactBuildAccelerator (post-MVP) | Remote-cache integration via `CacheKeyComponents`         | `XBT.html` §5.2; `XToolchainContract.html` §10.3 line 2059 |

### 12.3 Implementation phase order (likely)

Based on `XCoreXObject.html` §15 phase breakdown for XCoreXObject (which XIL2CPP depends on):
1. XCoreXObject Phase 5.a-l ships first (XObject base, allocator, GC, lifecycle, hot-reload integration, telemetry, harness).
2. Then XIL2CPP Phase 6.* (TBD by Rev 1 author — likely IL-feature-by-feature: types, references, methods, generics, async, strings, exceptions, hot-reload, foundation-prototype harness).

---

## Appendix A — Constraint-count summary

- **Section 2 (APIs PRODUCED)**: 15 sub-sections covering ~60 distinct API/emission obligations.
- **Section 3 (APIs CONSUMED)**: 13 sub-sections covering ~25 distinct consumed APIs.
- **Section 4 (IL Semantic Mapping)**: 8 sub-sections covering ~50 distinct mapping rules (many flagged as open questions).
- **Section 5 (Sim-path)**: 6 sub-sections; ~15 distinct runtime / build-time checks XIL2CPP must emit / honor.
- **Section 6 (Hot-reload)**: 8 sub-sections; ~10 distinct hot-reload contract points.
- **Section 7 (Build pipeline)**: 10 sub-sections; ~20 distinct action-graph / manifest constraints.
- **Section 8 (Reflection consumption)**: 6 sub-sections; ~10 distinct XHT-XIL2CPP interface points.
- **Section 9 (Acceptance criteria)**: 5 sub-sections; ~20 distinct gates / criteria.

**Total constraints in Sections 2-9 (approximate count): ~210.**

**Section 10 open questions: 31 (Q0-Q30).**

**Cross-doc contradictions surfaced: 3 critical (Q0 doc format, Q3 hosted CLR vs transpilation, Q14 schema-vector ownership) + several minor ambiguities (Q4, Q5, Q11, Q12, Q13).**

---

## Appendix B — Foundation-Prototype-mapping cheat sheet

| FP Criterion | XIL2CPP role | Verification gate |
|---|---|---|
| (a) GC pause < 5 ms | barrier emit cost ≤ 5 cycles; correct stack-map | X4 (XCoreXObject §13.2) |
| (b) Full-heap-scan < 50 ms | correct schema-vector ops emit | X-SCHEMA |
| (c) Cross-arch determinism | sim-path X-DET + X-NEW invariants compiled in | X-DET, X-NEW |
| (d) Single-method hot-patch < 30 s | symbol mangling stability | 12.2 contract test |
| (e) Cascade hot-patch < 120 s | container layout freeze; sub-pool rebind | 12.13, 12.14 |
| (f) Reflection round-trip | property accessor pair emit | 12.11 |
| (g) Symbol stability | deterministic mangling | 12.1, 12.2 |
| (h) Fragmentation < 15% | not direct; type-segregated allocations help | — |
| (i) Editor cold-start ≤ 15 s | works with lazy CDO; no eager-everything pattern | X-INIT, X-INIT-EAGER |

---

*End of XIL2CPP Constraint Inventory. Rev 1 author should treat Section 10 questions as REQUIRED PRE-WRITE resolutions and consult the Master Plan at `C:\Users\benne\.claude\plans\unreal-engine-source-code-lively-pillow.md` for additional commitments not captured in the seven HTML docs.*
