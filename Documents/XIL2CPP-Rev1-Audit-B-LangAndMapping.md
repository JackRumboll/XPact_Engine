# XIL2CPP Rev 1 — Round 1 Audit B — C# 12 Language Coverage and IL/AST → C++ Mapping Correctness

> **Audit perspective.** Auditor B (C# 12 + Roslyn + IL/AST → C++ mapping).
> **Audit scope.** Read-only audit of `Documents/XIL2CPP.html` Rev 1 dated 2026-05-28.
> **Cross-references.** `Documents/XIL2CPP-Constraints.md` Sections 4 + 10; C# 12 / Roslyn semantic model behavior; XCoreXObject Rev 4 GC-emit obligations; XCore-4b Rev 4 FProperty taxonomy.
> **Output format.** Each finding is an ID-prefixed entry with the offending Rev 1 quote (or absence-quote), the bug, the fix, and severity.
> **Severity scale.** CRITICAL = design-breaking (cannot ship as written); HIGH = correctness bug at emit time / latent runtime hazard; MEDIUM = author-facing ergonomics or missing feature coverage with a workaround; MINOR = doc-prose / numbering / cross-ref hygiene.
> **Locked-commitment respect.** Pure transpile, C# 12 / .NET 8 MVP, and `XIL2CPP001` on bare `new Foo()` are NOT revisited.

---

## Executive Summary

- **Total findings: 62.**
  - CRITICAL: 6
  - HIGH: 21
  - MEDIUM: 23
  - MINOR: 12
- **Top 3 most impactful findings.**
  1. **FIX-B-CRIT-01.** `XObject.New<T>` declares `where T : XObject, new()` — Roslyn requires a parameterless `new()` constraint to be satisfiable, which means the analyzer must SEE a synthesizable parameterless ctor on the XObject-derived `T`. The very `XIL2CPP001` diagnostic that bans `new Foo()` makes that ctor non-callable. The constraint and the diagnostic are mutually contradictory; this design as written either (a) defeats the diagnostic (the user can still call `new()` via the factory's body) or (b) makes every XObject derived class fail constraint satisfaction. The factory's constraint must change.
  2. **FIX-B-CRIT-02.** Pattern matching `o switch { int n => ... }` on `object` requires C# to box the `int`. The doc admits this and bans the case on sim-path, but the non-sim-path emit references `Int32Box` / `FloatBox` "wrapper types" that are not defined anywhere in the XPact stack. In a pure-transpile, no-managed-runtime engine, boxing has NO emit story — `object` itself has no emit story. The §5.6 example is structurally undefined; the doc has not designed `System.Object` as a transpilable type.
  3. **FIX-B-CRIT-03.** Plain (non-XObject) C# `class` emit is structurally underspecified. §5.1 says the emitter "assumes value semantics by default; if the semantic-model proves reference identity is observable... uses `std::shared_ptr<T>`." Reference identity in C# is the language default — every `class` has reference identity unless the analysis specifically proves otherwise, and that proof is the *open-world* problem (cross-module callees can observe identity). The default is backwards. Either commit to `shared_ptr` always (lose perf) or commit to value semantics with a `[ValueClass]` opt-in and reject all other plain reference classes (changes semantics). Rev 1 cannot leave this in.
- **Rev 1 structural soundness.** The umbrella structure is sound; pipeline (§3), tier classification (§3.3), GC integration (§6), hot-reload (§8), and XBT integration (§9) hold up. The mapping rules in §5 are where most findings live — many constructs are sketched, not specified, and several have semantic gaps that compile-time error suppression cannot paper over.
- **Missing C# 12 features.** `params ReadOnlySpan<T>`, `using` alias for non-named types (C# 12 alias-any-type), explicit `[Experimental]` attribute interplay, `ref readonly` parameters, **interface static abstract members**, `file` type modifier, primary-constructor capture semantics (the "captures may not be re-assigned" rule and the implicit-backing-field rule), record `with`-expression init-only semantics through a non-trivial copy constructor. None of these are in the §4.1 matrix.
- **Structurally wrong IL/AST → C++ mappings.** Six are structural rather than incremental: (1) the boxing-of-value-types-into-object problem (CRIT-02), (2) the plain-class reference-identity problem (CRIT-03), (3) lambdas emitted with `noexcept` (HIGH-05), (4) lambda closure capture semantics (HIGH-06), (5) string interpolation lowered to mutable builder (HIGH-15), (6) the missing IComparable-on-T constraint for `Sort` and friends (HIGH-19).

---

## Section 1 — C# 12 features missing from the coverage table

### FIX-B-CRIT-04 — `using X = SomeType<int>;` (alias for any type) is in C# 12 and missing

**Offending Rev 1 quote (§2.2 line 158 / coverage matrix §4.1):**
> "`using` alias for any type."

This is listed in the explicit C# 12 MVP feature list at §2.2 but is **NOT in the §4.1 coverage matrix table**, has **no §5 mapping rule**, has **no example emit** anywhere in the doc, and the AST normalization pass in §3.2 / Pass 2 does not mention it. C# 12's `using X = (int Year, int Month);` (alias for a tuple type) or `using FlowMap = Dictionary<FName, XActor>;` is a per-file declaration that requires emit-time substitution into every use site or a C++ `using` alias.

**Bug.** Rev 1 claims C# 12 MVP but omits one of C# 12's headline features from the mapping rules. If a user writes `using Pair = (int, string);` the transpiler has no defined behavior.

**Fix.** Add a §5.21 (or similar) describing the alias emit:
- For an alias to a named type: emit as C++ `using Alias = TargetType;` in the enclosing namespace.
- For an alias to a tuple type: emit the anonymous tuple struct (per §5.7) and a `using Alias = _Tuple_...;` redirect.
- The alias is file-scoped in C# 12; the C++ `using` must be inside an anonymous namespace inside the file's translation unit to honor that.

**Severity.** CRITICAL — a locked C# 12 MVP feature has no design.

---

### FIX-B-HIGH-01 — `static abstract` interface members (C# 11; still C# 12) entirely missing

**Offending Rev 1 quote:** None. §4.1 lists "interface (with [XInterface])" as Supported but does not enumerate static-abstract members. §5.1's interface emit shows `virtual void TakeDamage(float)` — pure-virtual instance — and stops there.

**Bug.** C# 11 introduced `static abstract` members on interfaces (e.g., `interface INumber<T> { static abstract T Zero { get; } static abstract T operator+(T a, T b); }`). These are how `IAddable<T>` / `INumber<T>` / generic-math interfaces are built. A transpiler that targets .NET 8 BCL + C# 12 MUST handle them, because the .NET 8 BCL `System.Numerics.INumber<T>` family uses them, and any user code using mathematical generics (e.g., `T Sum<T>(IEnumerable<T> xs) where T : INumber<T> { T t = T.Zero; foreach (var x in xs) t += x; return t; }`) requires it.

**Fix.** Emit static-abstract interface members as a C++ template-trait pattern: the interface itself does not own the static methods (C++ has no static virtual dispatch), but the closed-instantiation walk emits a per-constraint trait struct (e.g., `INumberTraits<XInt32>`) that delivers the static dispatch. Alternative: ban them on MVP and add `XIL2CPP016 — static abstract interface members are post-MVP; use a non-static interface or an explicit traits class`.

**Severity.** HIGH — common feature, generic-math .NET 8 surface relies on it.

---

### FIX-B-HIGH-02 — `params ReadOnlySpan<T>` (C# 12) not in matrix

**Offending Rev 1 quote (§2.2 line 192):**
> ".NET 9 features (e.g., `params Span<T>`, alias any type, interceptors) are out-of-scope for MVP"

**Bug.** `params Span<T>` is .NET 9. But `params ReadOnlySpan<T>` (and `params CollectionExpression`) are **C# 12** when targeting .NET 8 with the runtime polyfill. The doc lumps them under .NET 9 and bans them. Even if banned, the doc must enumerate the diagnostic. Worse: the doc does not articulate what happens to a C# 12 `params` *array* (the long-standing `params object[]`), which is still ubiquitous.

**Fix.** Add to §4.1 matrix:
- `params T[]` — Supported (emit as `TArray<T>` constructed at the call site).
- `params ReadOnlySpan<T>` — Post-MVP with `XIL2CPP015 — params ReadOnlySpan<T> is post-MVP`.
- `params Span<T>` — Permanently banned (mutability + lifetime semantics intractable in transpile).

**Severity.** HIGH — `params object[]` is in ~every framework call.

---

### FIX-B-HIGH-03 — `ref readonly` parameters (C# 12) missing

**Offending Rev 1 quote:** None. §5.2 enumerates "instance methods, static methods, virtual methods, abstract methods..." but does not mention `ref`, `in`, `out`, `ref readonly`, or `scoped` parameter modifiers. Contract §2.2 line 488 mentions parameter manglings `R`/`V`/`B`/`I`/`O` (reference/value/by-ref/in/out) — no `ref readonly`.

**Bug.** C# 12 introduced `ref readonly` parameters (`void Foo(ref readonly FVector v)`), which is the canonical pattern for passing a large struct by reference without permitting mutation. The mangling-rule does not allocate a discriminator for it; the C++ emit does not specify whether it becomes `const T&` (correct) or `const T*` or `T&` (incorrect).

**Fix.** Extend Contract §2.2 mangling to add `K` (the unallocated letter) for `ref readonly`; specify the C++ emit as `T const&`. Same for `scoped ref` / `scoped ref readonly` (a Roslyn span-safety annotation that may or may not need a mangling discriminator depending on whether overload resolution distinguishes it).

**Severity.** HIGH — modifier-aware mangling is part of the Itanium-style scheme; missing modifiers can cause silent overload collisions.

---

### FIX-B-HIGH-04 — `nameof()` on instance members is C# 12; emit story conflates compile-time and runtime cases

**Offending Rev 1 quote (§4.1 line 651 / §5.5 line 1286-1289):**
> "<code>nameof(X)</code> — Supported (interned const FString* per Contract §6.2)."

**Bug.** C# 12 expanded `nameof` to support instance members of the enclosing type *inside attribute arguments* (which is the load-bearing case: `[Required(ErrorMessage = nameof(SomeProperty))]`). The §5.5 mapping treats `nameof` uniformly as a compile-time `const FString*` literal lookup, but in the attribute-argument case the value is consumed by the attribute's constructor at attribute-emit time, not at use-site emit time. Critically: `nameof` of a *generic-method type parameter* (e.g., `T Foo<T>(T x) { return default; throw new ArgumentException(nameof(T)); }`) is the *unmangled* name "T" — the doc says the lookup is "the literal name". Verify: per Roslyn, `nameof(T)` for an unbound type parameter resolves to the source-text "T", not the closed-instantiation name. The §5.5 wording "matching the literal 'T'" leaves this ambiguous.

**Fix.** Tighten §5.5: nameof on (a) a member name resolves to the *unqualified* identifier; (b) a generic type parameter resolves to the parameter's *declared* name, not the closed type's name. Cite C# spec §11.6.16 directly.

**Severity.** HIGH — attribute-argument `nameof` is the most common production usage; silent wrong-string emit is hard to spot.

---

### FIX-B-MEDIUM-01 — Primary constructor capture semantics undefined

**Offending Rev 1 quote (§2.2 line 167):**
> "Primary constructors — emit the primary-constructor parameters as FProperty entries with auto-property semantics."

**Bug.** This conflates **record primary constructors** (parameters become public init-only properties) with **class primary constructors** (parameters are field-like captures, NOT public properties, and may be re-read multiple times). For `class Foo(int x) { public int Sum(int y) => x + y; }` the parameter `x` is a *capture* — Roslyn synthesizes a hidden backing field `<x>P` and a primary-ctor body that assigns it. The doc treats both shapes as FProperty entries, which is wrong for the class case: those captures are NOT reflectable, NOT serializable, NOT replicated; emitting them as `FProperty` makes them part of the reflection surface where they should not be. They are also reassignable from within the class body (`class Foo(int x) { void Reset() => x = 0; }`) — auto-property semantics says they are init-only, but C# 12 class primary-ctor captures are mutable.

**Fix.** Distinguish:
- **Record primary ctors** → public init-only FProperty entries (current text is correct).
- **Class primary ctors** → private mutable C++ fields with no FProperty descriptor; warn (or compile-time deny) if the class is `[XClass]`-attributed and any primary-ctor parameter is referenced after the constructor body.

Add diagnostic `XIL2CPP017 — class primary-constructor parameter '<n>' captured into method body; XClass-reflectable types must use explicit fields`.

**Severity.** MEDIUM — confounds reflection with code-gen capture; could break serialization round-trip.

---

### FIX-B-MEDIUM-02 — Record `with` expression emit understated

**Offending Rev 1 quote (§3.2 Pass 2 normalization):**
> "Record `with` expressions — record1 with { X = 10 } → the record's copy constructor invocation followed by property setter calls."

**Bug.** This is incorrect for records with init-only properties (the canonical case). The `with` expression is *the only context* in which init-only properties can be re-set on the new instance, and Roslyn lowers it to a synthesized `<Clone>$()` method that returns a copy via the record's copy constructor (`Foo(Foo original)`), and then INVOKES THE INIT SETTERS on the copy — but the init setters require special access semantics (they're accessible from the `with` lowering and from object initializers, nowhere else). The doc says "property setter calls" — that bypasses the init-only guard. The transpiler must emit the init setters using the same special privilege that Roslyn uses (see Roslyn's `WellKnownMemberNames.CloneMethodName`).

Additionally, for record *structs* (C# 10+) `with` calls the parameterless `default(Foo)` constructor + memberwise initialization, NOT the copy constructor. Different lowering. The doc doesn't distinguish.

**Fix.** Rewrite normalization to:
- `record class with { ... }` → invoke `<Clone>$()` (XIL2CPP-emitted; per Roslyn's synthesized method); then invoke init-setter-with-privilege on the clone.
- `record struct with { ... }` → memberwise-copy the value; mutate the named members on the copy.
- Document the init-setter privilege as "synthesized accessor visible only inside `with` lowering and inside object initializers".

**Severity.** MEDIUM — wrong `with` emit silently breaks init-only invariants.

---

### FIX-B-HIGH-05 — Lambdas emitted with `noexcept` is incorrect

**Offending Rev 1 quote (§5.9 line 1465, 1479, 1515):**
> ```c++
> auto square = [](int32_t x) noexcept -> int32_t { return x * x; };
> // ...
> int32_t operator()(int32_t x) const noexcept { return x + _captured_n; }
> // ...
> void operator()() const noexcept { ... }
> ```

**Bug.** `noexcept` is a hard promise that the function does not throw. Lambdas in C# **CAN throw**: `Func<int, int> f = x => x > 0 ? x : throw new ArgumentException();`. Emitting `noexcept` on every lambda body is incorrect — if the body throws, `std::terminate` fires instead of unwinding to the caller. The same Tier 1/Tier 2 classification logic that decides whether each method is `noexcept` MUST run on each lambda body. Lambdas that throw or whose call-graph contains a throw must NOT carry `noexcept`. The Tier classification in §3 / Pass 4 does not list lambdas as a unit of classification — they're nested inside the enclosing method, but the lambda's `noexcept`-ness can differ from the enclosing method's.

**Fix.** Pass 4 (tier classification) must classify every lambda body independently. Emit `noexcept` only when the body provably does not throw (same Tier 2 proof as for methods). Lambdas that throw and are called from a Tier 1 shim are fine; lambdas that throw and are called from a Tier 2 site force the enclosing site to Tier 1.

**Severity.** HIGH — silently turns thrown exceptions into `std::terminate`.

---

### FIX-B-HIGH-06 — Lambda capture: by-value vs by-reference vs nested

**Offending Rev 1 quote (§5.9 line 1469-1481):**
> "Capturing lambda (no XObject captures). Emits as a closure struct with by-value captures."

**Bug.** C# lambdas capture **by reference** for locals, not by value. If a local `int n` is captured by a lambda and the local is then modified *after* the lambda's creation, the lambda sees the new value:
```csharp
int n = 5;
Func<int> f = () => n;
n = 10;
Console.WriteLine(f()); // prints 10, not 5
```
The doc's "by-value captures" emit produces `f() == 5`, which is wrong. C# implements this by hoisting the captured local into a synthesized closure class **at the point of first capture**, and the local itself becomes a field-load through that class. The Roslyn lowering creates a `<>c__DisplayClass` per scope.

This compounds with the XObject-capture case (§5.9 line 1492+): the doc shows a "by-value" XPtr capture, which is fine for a *single* lambda, but if the same `XActor owner` is captured by multiple lambdas in the same scope, all four lambdas must share the same hoisted slot — so the closure becomes shared state, not per-lambda.

**Fix.** Re-design closure emit to follow Roslyn's `DisplayClass` hoisting:
1. At Pass 3, dataflow-analyze each method body to identify each scope's captured-locals set.
2. Emit one `<>c__DisplayClass_<MethodName>_<ScopeIndex>` struct per scope with the captured locals as fields.
3. Each local that is captured becomes a *field load* through the display-class struct, both inside and outside the lambda.
4. The XGCRootSpan goes on the display-class struct, not on the individual lambda.

This is a substantial rewrite of §5.9; the current text is structurally wrong.

**Severity.** HIGH — wrong capture semantics is a correctness bug for every multi-lambda body and every lambda that observes mutation of its capture.

---

### FIX-B-MEDIUM-03 — `default(T)` expression has no emit rule

**Offending Rev 1 quote:** None.

**Bug.** `default(int)`, `default(MyStruct)`, `default(XActor)` (which is `null`) is one of the most common C# expressions and has no §5 rule. The natural emit:
- `default(value_type T)` → `T{}` (value-initialize) — for `int` this is `0`, for `FVector` this is `FVector{0,0,0,0}`.
- `default(XObject_derived T)` → `nullptr` (cast to `T*`).
- `default(struct_with_XObject_field T)` → value-initialize, which means the embedded XObject* is `nullptr`; the stack-map must still register the slot.

Missing rule means the transpiler has undefined behavior here.

**Fix.** Add §5.3 sub-rule for `default(T)`: per-T resolution per the bullets above. Also handle the C# 7.1 target-typed `default` (the bare `default` literal that uses target type from context).

**Severity.** MEDIUM — common construct, easy to specify.

---

### FIX-B-MEDIUM-04 — `default` in pattern context vs expression context

**Offending Rev 1 quote:** None.

**Bug.** `obj is default(int)` (pattern context) is illegal in C#; `obj is 0` (constant pattern) is the canonical form. The `obj is default` (bare-default pattern) IS legal in C# 9+ and matches the default value of `obj`'s static type. Pattern matching in §5.6 lists "positional, property, list, type, relational, logical" but not the `default` pattern. List the variant explicitly: it is a constant pattern that resolves at parse time to the static type's `default` literal.

**Fix.** Add to §5.6 (pattern matching emit): `expr is default` → `expr == T{}` for value types, `expr == nullptr` for ref types.

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-05 — `is null` vs `== null` codegen distinction not specified

**Offending Rev 1 quote:** None.

**Bug.** `obj is null` and `obj == null` are NOT equivalent in C#. The latter calls the user-defined `operator==`; the former bypasses any user-defined operator and emits an `Ldnull/Cgt.Un` IL pair (semantically: pointer comparison). For an XObject with a user-defined `operator==` that does field-by-field equality (rare but legal), `obj == null` could call the operator with a null arg and dereference it; `obj is null` would not. The §5.3 row "Comparison operators — Supported" lumps them together. The §5.6 pattern-matching row implicitly handles `is null`, but the doc never says the two emit differently.

**Fix.** Add to §5.3 sub-rule: `expr is null` always emits a bare pointer comparison (or `XOptional<T>::HasValue() == false` for nullable value); `expr == null` emits a call to the user-defined `operator==` if one exists, falling back to pointer comparison.

**Severity.** MEDIUM — silent semantic divergence.

---

### FIX-B-MEDIUM-06 — `is { Prop: 5 }` property pattern emit missing

**Offending Rev 1 quote (§5.6):**
> "Property patterns, list patterns, type patterns, relational patterns, logical patterns lower to nested if/switch chains."

**Bug.** This is a list of names without emit rules. For a property pattern `obj is { X: > 0, Y: 5 }`, the lowering must (a) null-check `obj`, (b) read `obj.X`, (c) test it, (d) read `obj.Y`, (e) test it. The doc shows no example. For an XObject property where the getter is a *free function* (per §5.2: getters emit as free functions with explicit self), the lowering must invoke the getter, not access a member directly. The §5.6 example only shows a type pattern; no property/list/relational example is given.

**Fix.** Add per-pattern-kind sub-rules to §5.6:
- Property pattern → emit null-check (if `obj` is ref type) + per-property getter call + per-property predicate.
- List pattern `is [first, .., last]` → emit `.Count` check + indexer calls; slice/range patterns use `Span`-style API; on a sim-path TU, list patterns over an unordered collection (HashSet) emit XIL2CPP041 like foreach.
- Relational pattern `is > 0` → emit `>` comparison.
- Logical patterns (`and`/`or`/`not`) → short-circuit `&&`/`||`/`!` over sub-patterns.
- Discard `_` → no emit (just true).
- Var pattern `is var x` → emit assignment to a new local + always-true.

**Severity.** MEDIUM — pattern matching is one of the most pervasive C# 12 features; "lowers to nested if/switch chains" is not a spec.

---

### FIX-B-HIGH-07 — `switch` expression vs `switch` statement emit difference unspecified

**Offending Rev 1 quote (§4.1):**
> "switch statement — Supported.
> switch expression — Supported."

**Bug.** The §4.1 matrix lists both but §5.6 only shows one switch-expression example (the `o switch { ... }` pattern). The C++ emit of a `switch` *statement* is straightforward (`switch (e) { case k: ...; break; ... }` for integral and switch on constant patterns); the emit of a `switch` *expression* (`var s = e switch { 0 => "zero", _ => "other" }`) is structurally different — it must produce a value, not a statement, so the canonical lowering is either an immediately-invoked lambda (IIL) or a sequence of `?:` ternaries. For switch expressions over patterns, the lowering is a cascade of `if` statements that assign to a result local. No example given.

**Fix.** Add to §5.6 an explicit emit for `switch` expression: lower to a `_switch_result` local + cascade of `if` statements + final `return _switch_result;`. For switch over `string` (relational patterns on strings), document the `XCSharpString::Equals` call sequence.

**Severity.** HIGH — switch expressions are pervasive in modern C# code; the emit should not be implicit.

---

### FIX-B-MEDIUM-07 — Switch-statement-on-`string` semantics

**Offending Rev 1 quote:** None.

**Bug.** C# allows `switch (s) { case "hello": ... }` where `s` is `string`. The compiler lowers to either a hash-table dispatch (large switch) or a sequence of `String.Equals` calls (small switch). XPact's `XCSharpString` is an 8-byte handle storing a `const FString*`; with literal interning (per §5.5), the dispatch becomes a pointer-equality compare. But the doc does not specify whether `switch (s)` emits the pointer-equality form (correct given the interning rule) or a content comparison (slower but more compatible). And what about `switch (s) { case null: ... }` — a literal `null` case label?

**Fix.** Add §5.6 sub-rule: switch on `string` emits pointer-equality compares against the interned `const FString*` for each case label. A `case null:` label emits an `s.m_storage == nullptr` test. This is faster than `Equals` AND lets the compiler use a jump-table over pointer values.

**Severity.** MEDIUM — without specification, the transpiler may emit slower content-compare.

---

## Section 2 — Semantic gaps in C# core features

### FIX-B-HIGH-08 — `readonly` field emit unspecified

**Offending Rev 1 quote:** None.

**Bug.** C# `readonly` fields can be assigned in the declaration or in the constructor only. They are *not* C++ `const`. The §5.4 doc covers `init` setters, `required`, nullable, but does not discuss `readonly` fields. The natural emit is C++ `const T` (preventing post-ctor assignment), which works for value types but breaks for reference-typed readonly fields where the *pointer* should be const but the *pointee* may be mutated through it. E.g., `private readonly XActor owner;` — the `owner` reference cannot be reassigned, but `owner.position = ...` is fine. C++ `XActor* const owner` (const pointer, mutable pointee) is the correct emit; the doc does not say so.

**Fix.** Add §5.4 sub-rule: `readonly T` where T is value → `T const` member; `readonly T` where T is ref → `T* const` (or `XPtr<T> const`, which is C++ const-XPtr); init-time write inside the constructor body is permitted (the const-qualification is applied post-construction by the language semantics, not at the C++ level — XIL2CPP can rely on Roslyn to reject post-ctor writes and emit C++ without the C++ const-qualifier IF the analysis already catches the violation).

**Severity.** HIGH — mutable readonly reference is a common pattern; emitting `const T*` instead of `T* const` is a real bug.

---

### FIX-B-HIGH-09 — Auto-property backing field naming convention

**Offending Rev 1 quote (§5.2 line 1138):**
> ```c++
> extern "C" int32_t _v1..._Foo__get_Foo_P_R_Foo(::Foo* self) noexcept { return self->_FooBackingField; }
> ```

**Bug.** C# generates auto-property backing field names of the form `<X>k__BackingField` (with angle brackets and double-underscore). The mangled name in IL/metadata IS this exact identifier; reflection tooling that walks the IL relies on it. The XIL2CPP emit uses `_FooBackingField` (underscore-prefixed C-identifier-safe form). This is a *cross-tool* naming break: XHT, which emits the FProperty descriptor, must agree with XIL2CPP on the backing field name; serialization round-trip (criterion (f)) walks FProperty entries by name. If XHT uses `<X>k__BackingField` (the C# IL convention) and XIL2CPP uses `_XBackingField` (the C++ identifier-safe form), the reflection round-trip will fail to bind the property to the field.

**Fix.** Either:
1. Define a canonical XPact naming convention for auto-property backing fields (e.g., `__BackingField_<X>` or `m_<X>BackingField`) and assert XHT and XIL2CPP both use it; OR
2. Map the C#-source `<X>k__BackingField` identifier to a C++-identifier-safe form (`_M0X_K0__BackingField` with deterministic escape) and use that on both tools.

Document the rule under §10.5 (the shared binding table) and add `XIL2CPP145 — backing-field naming mismatch between XHT and XIL2CPP for property '<X>'`.

**Severity.** HIGH — cross-tool symbol contract bug; manifests as silent reflection-round-trip failure.

---

### FIX-B-HIGH-10 — Indexers (`this[int i]`) emit unspecified

**Offending Rev 1 quote (§4.1):**
> "Indexers — Supported."

**Bug.** Indexers appear *twice* in the §4.1 table (lines 607 and 611, both rows say "Supported — §5.4"), but §5.4 does not show indexer emit. C# indexers are syntactic sugar over a property named `Item` with a get/set pair that takes index arguments. They:
- Can be overloaded with multiple index types: `this[int]` and `this[string]` co-exist.
- Can have multi-index forms: `this[int x, int y]`.
- Can be init-only on records.
- Can use `params` indices.

The IL surfaces them as methods `get_Item(int)` / `set_Item(int, T)`. The C++ emit would naturally be `operator[]`, but C++'s `operator[]` is single-argument; multi-argument indexers don't translate to `operator[]`. Multi-index indexers must emit as named methods (`Get_Item` / `Set_Item`) plus an overloaded `operator[]` where possible.

**Fix.** Add §5.4 sub-rule for indexers:
- Single-index `T this[U i]` → emit free `_v1..._Foo__get_Item_P_R_Foo_V_U(::Foo*, U)` + setter; emit a C++ `operator[]` *inside* the class body for ergonomics that tail-calls the free function.
- Multi-index `T this[U a, V b]` → emit free functions; no C++ `operator[]` (cannot multi-arg).
- Init-only indexers (record indexers) → emit setter with init-only privilege same as init-only auto-property.

**Severity.** HIGH — indexers are not exotic; `Dictionary<K,V>` indexers, FString character access via `this[int]`, and TArray-style index access are all indexers.

---

### FIX-B-MEDIUM-08 — User-defined `operator implicit` / `operator explicit` emit

**Offending Rev 1 quote (§4.1):**
> "User-defined conversions (implicit / explicit) — Supported. §5.4"

**Bug.** §5.4 does not show conversion-operator emit. C# user-defined conversions (`public static implicit operator Foo(Bar b) => ...`) lower in IL to static methods named `op_Implicit` / `op_Explicit`. The C++ emit must:
1. Emit the free function with mangled name per the `op_Implicit_T_to_U` / `op_Explicit_T_to_U` convention.
2. ALSO emit a C++ conversion operator (or constructor) on the destination type so C++ call sites can invoke implicitly. Without (2), the implicit conversion exists in C# but the C++ emit at the use-site has to call the free function explicitly, breaking the abstraction.

Additionally: `operator explicit` in C# requires the call site to write `(T)x`; without a corresponding C++ `explicit` constructor, the C++ emit might silently call it implicitly. Wrong semantics.

**Fix.** Add §5.4 sub-rule:
- `public static implicit operator U(T x)` → emit free function `op_Implicit_T_to_U` + emit C++ `U::U(T x)` non-`explicit` constructor (or `T::operator U() const` member if U is XObject-derived and can't be modified).
- `public static explicit operator U(T x)` → same but C++ constructor is `explicit`.

**Severity.** MEDIUM — silent semantic drift on implicit conversion sites.

---

### FIX-B-MEDIUM-09 — Constructor chaining `: this(...)` / `: base(...)` emit

**Offending Rev 1 quote:** None.

**Bug.** C# constructor delegation `public Foo(int x) : this(x, 0) { }` and `public Bar(int x) : base(x) { }` is mentioned nowhere. The natural C++ emit is C++ delegating constructors:
```c++
Foo::Foo(int x) : Foo(x, 0) { }
Bar::Bar(int x) : Base(x) { }
```
But the doc's §5.2 line 1091-1097 shows constructors emitted as `extern "C" void Foo__$ctor(self, ...)` free functions. With free-function ctors, "constructor chaining" must be expressed as `_Foo__$ctor` calling `_Foo__$ctor_overload` directly — not via the C++ initializer list. The doc does not describe this.

Worse: `: base(args)` for XObject-derived classes is partially intercepted by the `NewObject<T>` factory — the user can never call a base ctor *directly* (XIL2CPP001 forbids `new Base()`), but they can write `: base(args)` syntax to forward args. How are those args passed through `NewObject<T>(outer, name, flags)` to the user-class constructor? The factory takes 3 args; the user class takes whatever the user wrote. There is a mismatch the doc does not address.

**Fix.** Specify:
- `: this(args)` — emit as a free-function tail-call to the other ctor variant.
- `: base(args)` — for non-XObject base, emit as direct call to base ctor at the start of the derived ctor body. For XObject-derived base, the args are NOT passed through `NewObject<T>` (which only takes outer/name/flags); the user's `: base(args)` syntax must be re-routed to a separate "user-init" method called by `Z_PostInitProperties_<Type>` or rejected at parse time with a diagnostic.

**Severity.** MEDIUM (or HIGH for the XObject base-args case).

---

### FIX-B-HIGH-11 — Extension methods emit unspecified

**Offending Rev 1 quote:** None.

**Bug.** Extension methods (`public static class Ext { public static int Sum(this List<int> xs) { ... } }`) are pervasive in C# (most of LINQ is extension methods; many ergonomic helpers are). They lower in IL to a static method on a static class. At the call site `xs.Sum()`, the compiler resolves the call to `Ext.Sum(xs)`. The §5 mapping rules do not mention extension methods.

C# 12's *extensions everywhere* preview is .NET 9, not 12 — but the regular `this`-parameter extension method has been C# 3.0 forever. The doc ignores them.

**Fix.** Add §5.2 sub-rule: extension method `static R Ext.Method(this T self, A a) {...}` → emit as a free function with `self` first arg (matching the normal method emit shape). Call sites `obj.Method(a)` resolve at Pass 1 (semantic model) and emit as the free-function call.

**Severity.** HIGH — even ignoring LINQ, extension methods are pervasive in C# code.

---

### FIX-B-MEDIUM-10 — Tuple field names lost in emit

**Offending Rev 1 quote (§5.7 line 1424-1428):**
> ```c++
> struct _Tuple_int_XCSharpString {
>     int32_t X;
>     ::XCSharpString Y;
> };
> _Tuple_int_XCSharpString tuple{5, ::XCSharpString{&_String_hello}};
> ```

**Bug.** This is partially correct (the tuple struct exists), but the emit name `_Tuple_int_XCSharpString` is positional — it does NOT preserve the field names `X` / `Y`. C# tuple names are part of the **declared type**: `(int X, string Y)` is the same runtime as `(int, string)` but the names live in the `TupleElementNamesAttribute` metadata. Two C# tuples with same shape but different names should map to:
- (a) **The same C++ struct** — name is "decoration" — and the field access uses C#-level names that emit the appropriate member access, OR
- (b) **Different C++ structs** — fields are part of the type signature — and `(int X, string Y)` ≠ `(int A, string B)`.

The doc shows option (a) but does not say so. Without explicit handling, the transpiler:
- Could fail to dispatch `tuple.X` vs `tuple.A` if the source declares the latter.
- Could fail to deconstruct positionally if the user writes `var (x, y) = t` and the tuple was declared with `(int A, int B)`.

**Fix.** Decide and document:
- (Recommended) (a): emit positional tuple structs `_Tuple_<T1>_<T2>` and the C# field accesses lower to positional members `t.Item1`, `t.Item2` regardless of the tuple's declared names (matching the IL behavior — C# tuples have `Item1`/`Item2` storage and the named fields are alias attributes).
- For named field accesses (`t.X`), the AST normalization maps `t.X` to `t.Item1` based on the declared name-position binding.

**Severity.** MEDIUM — undocumented behavior; specific user code that depends on tuple-name-based dispatch will silently fail.

---

### FIX-B-MEDIUM-11 — Tuple equality emit unspecified

**Offending Rev 1 quote:** None.

**Bug.** C# 7.3+ supports tuple equality: `(1, "a") == (2, "b")` evaluates per-element. This requires either (a) the C++ tuple struct to provide `operator==` per-member, or (b) the lowering to expand `(1,"a") == (2,"b")` into `1 == 2 && "a" == "b"` at AST normalization. The doc says neither.

**Fix.** Add to §5.7 (tuple emit): emit `operator==` per-tuple-struct that compares fields positionally; also add `operator!=`. For tuple types containing XObject* refs, comparison is pointer-comparison (same as `ReferenceEquals`); for tuple types containing strings, comparison is pointer-equality on the interned `const FString*` (per §5.5).

**Severity.** MEDIUM — silently missing equality semantics.

---

### FIX-B-MEDIUM-12 — `init` accessors: how is "settable from constructor / object initializer" enforced?

**Offending Rev 1 quote (§5.4 line 1258-1262):**
> "XIL2CPP emits the setter visible only to the constructor. Outside the constructor, the setter is mangled with a different suffix that the AST-normalization pass rejects."

**Bug.** This sketches a mangling-based approach but the design has issues:
1. "Mangled with a different suffix" is not specified — how does the AST-normalization pass distinguish "constructor context" from "non-constructor context" at the call site? The mangled name is fixed at emit time, but the *callability* depends on the *caller*, not the *callee*.
2. The `with`-expression context for records is also legal — see FIX-B-MEDIUM-02. The init setter must be callable from `with` lowering AND from constructors AND from object initializers, but NOT from regular code.
3. The Roslyn semantic model already enforces this at parse time (calling an init setter from non-init context is a CS0200/CS8852 compile error). XIL2CPP can rely on Roslyn for the check rather than re-implement it in mangling.

**Fix.** Simplify: emit the init setter as a regular free function `_v1..._Foo__set_X_init_*`. Trust Roslyn's CS8852 to reject illegal call sites. The mangling does NOT need a "callability" discriminator; the language already enforces.

Add diagnostic XIL2CPP018 only for the *edge case* where the C# source uses `unsafe`-style reflection to call the init setter through `MethodInfo.Invoke` — banned per §5.11 already.

**Severity.** MEDIUM — current design is over-engineered; simplify.

---

### FIX-B-HIGH-12 — `volatile` keyword has no emit rule

**Offending Rev 1 quote:** None.

**Bug.** C# `volatile T field;` is the canonical way to declare a memory location that may be accessed concurrently and must use acquire/release semantics. In a multi-threaded engine, this is unavoidable for any field touched from multiple threads (config flags, telemetry counters, hot-reload signal flags, etc.). The doc does not mention `volatile`. The natural emit is `std::atomic<T>` (the C++ analog) or `T volatile` (the C++ keyword, which has weaker semantics than C#'s).

Critically: C#'s `volatile` is acquire-on-read / release-on-write (memory_order_acquire / memory_order_release). C++'s `volatile` is *not* this — it suppresses certain compiler optimizations but does NOT impose acquire/release ordering. The naive transcription `volatile T field` is WRONG.

**Fix.** Map C# `volatile T field` → `std::atomic<T> field` with all reads using `load(std::memory_order_acquire)` and all writes using `store(value, std::memory_order_release)`. Note: this changes layout (atomic may have different alignment than T); on x86_64 it's identical for word-sized T, but on ARM64 the alignment may differ. Add `XIL2CPP019 — volatile field type 'T' has incompatible alignment for std::atomic; consider Interlocked APIs or a different design`.

**Severity.** HIGH — silent concurrency-correctness bug.

---

### FIX-B-MEDIUM-13 — Static class emit unspecified

**Offending Rev 1 quote (§4.1):**
> "Static classes — Supported. §5.1"

**Bug.** §5.1 does not show static class emit. C# static classes (`public static class Math { public static float Lerp(float a, float b, float t) => ... }`) have these constraints: cannot be instantiated, cannot have instance members, can only contain static members. C++ has no direct "static class" concept; the natural emit is a namespace or an `abstract` class with deleted constructor. The §5.2 example for `static float Lerp(...)` shows a free function, not a class. Are static-class methods emitted as namespace-scoped free functions? If so, the namespace name comes from the class name; that namespace name must not collide with other classes.

**Fix.** Add §5.1 sub-rule: C# `static class X { ... }` → emit C++ `namespace X { ... }` (or `struct X { static ... }` for nested-static-class case). Static methods inside the class become free functions in that namespace.

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-14 — `partial` method semantics in transpile context

**Offending Rev 1 quote (§4.1):**
> "partial class/struct/interface — Supported (merged at AST normalization)."

**Bug.** Partial *classes* merging is straightforward. Partial *methods* (introduced C# 3, expanded C# 9) are different: a partial method is a declaration that may or may not have a body. If unbodied, the method call is removed at compile time. C# 9 added "non-void / non-private" partial methods which can have non-void return + access modifiers + must have a body. The doc says only "partial class/struct/interface". Partial *methods* are not mentioned; partial methods on source generators (which Rev 1 already bans) and partial methods declared explicitly by users (which Rev 1 allows) need a defined behavior.

**Fix.** Add §5.1 sub-rule: partial methods with bodies emit normally; partial methods without bodies (the "declaration only" form) emit nothing AND the call site is elided. Diagnostic if the call site cannot be safely elided (e.g., if the call has a side effect on its arguments).

**Severity.** MEDIUM — partial methods are how Roslyn source generators communicate; even with source-gen banned, users may write partial methods directly.

---

### FIX-B-MEDIUM-15 — Nested types: name mangling

**Offending Rev 1 quote (§4.1):**
> "nested types — Supported. §5.1"

**Bug.** C# nested classes/structs are named `Outer.Nested`; their IL name is `Outer/Nested` (with `/` separator); the Itanium-style mangling per Contract §2.2 separates type names by `::`. Multi-level nesting (`Outer.Middle.Inner`) needs a deterministic emit. The §5.1 example does not show nested-type emit. Open questions:
- Does `Outer.Nested` emit as C++ `Outer::Nested` (C++ nested class) or as a peer top-level `Outer__Nested`?
- For generic outer with generic nested (`Outer<T1>.Inner<T2>`), what's the closed-instantiation form?
- For nested types referencing outer's type parameters (`class Outer<T> { class Inner { T value; } }`), the inner type must capture `T` — C++ requires `Outer<T>::Inner` so the dependency is explicit.

**Fix.** Specify: emit nested types as C++ nested classes (preserving the structural relationship). Generic outers + nested types require the closed-instantiation walk to enumerate per-outer-instantiation per-inner-instantiation combinations.

**Severity.** MEDIUM — common pattern, especially for builder patterns and DSL-style APIs.

---

### FIX-B-HIGH-13 — `new` (hiding) vs `override` semantics not distinguished

**Offending Rev 1 quote (§4.1):**
> "Override methods — Supported (must match base signature exactly).
> Sealed override methods — Supported (devirtualization opportunity)."

**Bug.** Missing the `new` keyword case: `class Derived : Base { public new void Foo() { ... } }`. This is *hiding*, not overriding — `Derived d = new Derived(); ((Base)d).Foo()` calls `Base.Foo()`, NOT `Derived.Foo()`. The doc lists override but not hiding. The natural emit:
- `override` → goes through the FClass vtable dispatcher (§5.2 line 1054+).
- `new` (hiding) → emits as a **separate** free function with a different mangled name; static dispatch resolves at the use site based on the *static* type.

Without distinguishing, the transpiler would emit `Derived.Foo` as if it were an override of `Base.Foo`, breaking the C# semantic.

**Fix.** Add §5.2 sub-rule: `new` modifier on a method (hiding) → emit as a fully-distinct mangled function; the closed-class vtable contains the base's slot pointing to `Base.Foo`, not `Derived.Foo`; the static dispatch at the use site routes to the derived form only when the static type is `Derived` (or below in the chain).

**Severity.** HIGH — subtle but real semantic bug.

---

### FIX-B-MEDIUM-16 — Sealed class devirtualization opportunity unspecified

**Offending Rev 1 quote (§4.1):**
> "Sealed classes — Supported (devirtualization opportunity)."

**Bug.** "Devirtualization opportunity" is mentioned without a rule. When a class is `sealed`, a virtual call to a method on that class can be devirtualized to a direct call. The doc does not say whether XIL2CPP performs this devirtualization or relies on the C++ compiler. C++ devirtualization works at the compile level only when the *static* type is known to be `final`; the C++ class declaration must use `final`. The doc does not mention emitting C++ `final` for sealed C# classes.

**Fix.** Add §5.1 sub-rule: sealed C# class → emit C++ class with `final` keyword. Sealed override methods → emit C++ method with `final`. This allows the C++ compiler to devirtualize where possible.

**Severity.** MEDIUM — perf opportunity gone unrealized.

---

### FIX-B-HIGH-14 — Multiple interface implementation emit

**Offending Rev 1 quote:** None.

**Bug.** C# allows a class to implement multiple interfaces: `class XPickup : XActor, IDamageable, IInteractable`. C++ allows multiple inheritance, but XPact's per-FClass vtable model (FakeVTable per §5.2 line 1076) is single-base. The doc shows interface emit as a pure-virtual C++ class (§5.1 line 927-931), which implies multiple inheritance for multiple interfaces. But the FClass vtable model has no slots for the interface methods; how are they dispatched?

Open questions:
- Does the interface emit go through the FClass FakeVTable or through a real C++ vtable?
- If a real C++ vtable, then `XPickup` has two vtables (its own + the interface's) — does this break the FClass identity model where `obj->GetClass()` returns one FClass?
- Are interface methods callable via `XCore::Reflect::Cast<IDamageable>(obj)` → FClass-based dispatch, or via a normal C++ static_cast/dynamic_cast?

**Fix.** Specify the interface dispatch model:
- (Recommended) Interface methods are C++ pure-virtual; the class has a real C++ vtable per interface. The FClass FakeVTable is for XObject-lifecycle slots only (PostInitProperties, etc.); interface dispatch goes through C++.
- The XCore::Reflect::Cast<IDamageable>(obj) resolves through C++ dynamic_cast (RTTI must be enabled in the toolchain matrix).
- Hot-reload class replacement updates vtables — the FakeVTable update is "just" the FClass; the C++ vtable is patched separately via XLiveCoding.

**Severity.** HIGH — interface dispatch is one of the most important C# idioms; current doc does not specify it.

---

### FIX-B-HIGH-15 — Explicit interface implementation (`IFoo.Bar()`) missing

**Offending Rev 1 quote:** None.

**Bug.** C# supports *explicit interface implementation*: `class XPickup : IDamageable { void IDamageable.TakeDamage(float amount) { ... } }`. The method is callable ONLY through an `IDamageable`-typed reference, not on a `XPickup`-typed reference. The doc does not mention explicit interface implementation. Disambiguation is important when the same class implements two interfaces with conflicting method names.

**Fix.** Add §5.2 sub-rule: explicit interface implementation → emit method with mangled name including the interface name (e.g., `_v1..._XPickup__IDamageable__TakeDamage_...`); the C++ class implements the interface methods using the explicit qualifier (`void IDamageable::TakeDamage(float) override` outside the class body, or use `using` to disambiguate).

**Severity.** HIGH — name-conflict resolution requires this.

---

### FIX-B-HIGH-16 — Generic constraint emit: `where T : new()` defeats Locked Commitment 3

**Offending Rev 1 quote (§2.3 line 247 / §2.3 line 256):**
> ```csharp
> public static T New<T>(XObject outer, FName name, EObjectFlags flags) where T : XObject, new();
> ```
> "The `where T : new()` constraint is a Roslyn-side hint for the analyzer; the runtime constructor invocation is via the FClass's ClassConstructorFn slot (NOT through C# reflection)."

**Bug.** `where T : new()` is NOT a "Roslyn-side hint" — it is a binding constraint that compels the compiler to validate that `T` has a public parameterless constructor accessible at every closed instantiation. If Locked Commitment 3 makes `new Foo()` a compile-time error (XIL2CPP001), then `Foo` cannot be a valid `T` argument to `XObject.New<Foo>` because Roslyn's *own* analyzer will reject the constraint satisfaction. Specifically: the call site `XObject.New<HealthPickup>(outer, name, flags)` will be diagnosed by Roslyn as CS0310 (no public parameterless constructor) once you ban `new Foo()` for XObject-derived `Foo`.

The doc says the diagnostic is suppressed for the factory via `[XObjectInternalConstructor]`, but **the constraint check happens at the call site**, not at the factory body. The suppression attribute on the factory does not propagate to the closure-of-callers.

This is the most important issue in the audit: the `where T : new()` constraint and the `XIL2CPP001 new-expression on XObject` diagnostic are mutually contradictory.

**Fix (option A — recommended).** Remove `where T : new()` from the `XObject.New<T>` signature; constrain only `where T : XObject`. The Roslyn analyzer cannot verify a parameterless ctor exists, but XIL2CPP doesn't need it to — at emit time, XIL2CPP knows whether `T` has a default ctor (it walks the type symbol). If it doesn't, emit `XIL2CPP003 — XObject.New<T> requires T to have a default constructor; please add 'public Foo() { }'`. The default ctor is callable internally by the FClass::ClassConstructorFn — XIL2CPP001 bans `new Foo()` at the *source* level but the *placement-new* inside the factory IS allowed (via `[XObjectInternalConstructor]`).

**Fix (option B).** Define an XPact-specific generic constraint marker `where T : XObjectFactoryConstructible` (a magic interface that the analyzer pattern-matches), and remove `new()`. This avoids Roslyn's CS0310 entirely.

**Severity.** CRITICAL — design contradiction in the locked-commitment section.

---

### FIX-B-HIGH-17 — Generic instantiation walk: closed instantiations across module boundaries

**Offending Rev 1 quote (§5.8 line 978):**
> "Cross-module generic instantiations require the depending module to emit the FClass (per Constraint §2.14)."

**Bug.** This rule is incomplete and creates a duplication problem. If `Container<T>` is declared in module M1 and instantiated as `Container<XActor>` in both module M2 and M3, the rule says "the depending module" emits the FClass. Both M2 and M3 depend; both will emit the FClass for the same closed type. That's:
1. **Double-registration** of `Container<XActor>_Class` at module load — `XReflectionRuntime::RegisterClass` will see two registrations for the same name.
2. **Linker collision** — both modules export the same singleton-getter `Z_Construct_FClass_M2_Container_XActor` (or `Z_Construct_FClass_M3_Container_XActor` — the doc is unclear on whether the module qualifier is M (the declaring module) or M2/M3 (the consuming module)). If the qualifier is the declaring module, both M2 and M3 emit symbols with the SAME name — multiple definition errors.

**Fix.** Pick a single emit owner:
- (Recommended) The closed-instantiation FClass is emitted by **the module where the closed instantiation is first referenced**, with the symbol prefixed by THAT module's name. Modules that re-reference the same closed type link against the existing emission via `extern "C"`. Requires a manifest-level mechanism to identify "first reference" — likely the alphabetically-first dependent module.
- (Alternative) Emit per-closed-instantiation in **every** referencing module as `inline` so the linker deduplicates. Requires `inline` on `constinit const FClass`, which is allowed in C++17+.

Add `XIL2CPP125 — cross-module duplicate FClass for closed instantiation '<T>'; expected single emit by module '<M>'`.

**Severity.** HIGH — link-time bug, manifests only on multi-module projects.

---

### FIX-B-MEDIUM-17 — Self-referential generic constraints (`where T : IComparable<T>`)

**Offending Rev 1 quote (§5.8 line 1448-1450):**
> "where T : SomeBase — recipient may upcast to SomeBase.
> where T : ISomeInterface — recipient may dispatch through interface vtable."

**Bug.** Doesn't cover self-referential constraints (`where T : IComparable<T>` or `where T : IEquatable<T>`). These are extremely common and are how `Sort<T>` and `OrderBy` are typically constrained. The closed-instantiation walk must handle the recursive substitution (the constraint references T itself).

**Fix.** Add §5.8 sub-rule: self-referential constraints are resolved at closed-instantiation time. The walk does NOT recurse into the constraint type itself for cycle detection purposes (the constraint's reference to `T` is "the same `T` we're currently instantiating", not a new instantiation to walk).

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-18 — Variance (`in T` / `out T`) emit overstated

**Offending Rev 1 quote (§5.8 line 1453):**
> "Generic variance. `in T` / `out T` annotations on interfaces propagate to the C++ template parameters; no runtime cost. Variance-related operations (e.g., upcasting a `List<Derived>` to `IEnumerable<Base>`) are emitted as static_cast at the call site."

**Bug.** C++ templates do NOT have variance — `List<Derived>` is NOT a `List<Base>` in C++, period (modulo the unrelated `is_base_of` test). The C# variance rule that lets you assign `IEnumerable<Derived> e = new List<Derived>(); IEnumerable<Base> b = e;` is implemented in IL by **the assignment being a no-op cast** because `IEnumerable<out T>` is variant. In C++, there is no equivalent — the cast `IEnumerable<Base>* = static_cast<IEnumerable<Base>*>(p_derived)` is **undefined behavior** unless `IEnumerable<Derived>` and `IEnumerable<Base>` share the same vtable layout.

The "static_cast at the call site" wording in §5.8 is at best incomplete and at worst silently wrong: a `static_cast` between unrelated template instantiations is UB.

**Fix.** Either:
- (a) Implement covariance via inheritance: emit `IEnumerable<Derived>` as inheriting from `IEnumerable<Base>` when `T` is covariant. This requires the closed-instantiation walk to detect variance and emit `template<>` partial-specializations with the inheritance chain.
- (b) Disallow C# variance assignments at the source level; force users to call an explicit `.Cast<Base>()` extension that is implemented as a generator. Add `XIL2CPP026 — variance-based assignment not supported; use explicit .Cast<Base>()`.

Option (a) is closer to "the right thing" per Prime Directive but is complex to emit.

**Severity.** MEDIUM-HIGH (depends on how common variance is in user code; likely common for `IEnumerable<T>` use).

---

### FIX-B-HIGH-18 — `IEnumerable<T>` / `foreach` over user-iterator emit

**Offending Rev 1 quote (§5.3 / §4.1):**
> "foreach — Supported (NOT over HashSet<T> — non-det iteration order; emits warning)."

**Bug.** The §4.1 row is single-sentence; the §5.3 examples only show foreach over `TArray`. `foreach` works on anything with `GetEnumerator()` returning a type with `MoveNext()` and `Current` properties (the C# duck-typed enumerable protocol). Custom user iterators (classes that implement `IEnumerator<T>` directly) and yield-iterator state machines (§5.9 — partial; banned post-MVP for sim-path) need a defined emit. The doc says foreach over `List<T>` becomes `for (auto& item : list.AsRangeView())`, which requires `AsRangeView()` to exist on TArray. The natural C++ form would be range-based `for` directly over `TArray`'s `begin()`/`end()`. The doc invents `AsRangeView()` without specifying its semantics; what about user-typed iterators?

**Fix.** Add §5.3 sub-rule:
- `foreach (var x in expr)` where `expr.GetEnumerator()` exists → emit C++ range-based `for` over `expr.GetEnumerator()`; the enumerator's `MoveNext()` becomes the iterator's `operator++` and `Current` becomes `operator*`.
- Custom `IEnumerator<T>` implementations on XObject — handled as regular instance methods; allocates a struct.
- `LINQ`-style chained extension methods banned (already covered in §5.19).

**Severity.** HIGH — every foreach over a non-TArray container needs this.

---

### FIX-B-HIGH-19 — Comparison-based sorts: `where T : IComparable<T>` not deconstructed

**Offending Rev 1 quote:** None.

**Bug.** `List<T>.Sort()` requires `T : IComparable<T>` (or an explicit `Comparer<T>` arg). The transpiler must emit a TArray.Sort that knows how to dispatch through the IComparable interface; the §5.7 container-mapping table does not mention sort/order operations. Whether `TArray<T>` has `.Sort()` at all is unspecified; if it doesn't, user code that calls `list.Sort()` will fail at C++ compile.

**Fix.** Define TArray's sort surface: emit calls to `TArray<T>::Sort(Comparison<T>)` where `Comparison<T>` is a function pointer / delegate type. For default `Sort()`, the closed-instantiation walk verifies that `T` implements `IComparable<T>` and emits a default comparison wrapper.

**Severity.** HIGH — Sort is a routine container op.

---

### FIX-B-MEDIUM-19 — `where T : notnull` and `where T : default` missing from constraints list

**Offending Rev 1 quote (§5.8):**
> "Generic constraints (where T : XObject, struct, unmanaged, new(), etc.) — Supported."

**Bug.** The list omits `notnull` (C# 8+) and `default` (C# 9+). These constraints affect nullable-reference-type analysis, which interacts with the `CPF_NullableReferenceType` bit (§5.4 line 1264). Open: when a generic method has `where T : notnull` and receives a nullable arg, does the transpiler emit a runtime null check? It currently doesn't say.

**Fix.** Add to §5.8 the missing constraints with their treatments:
- `notnull` — emit runtime null check at the start of the method body in Dev mode; trust the analyzer in Shipping. Matches Roslyn's `[NotNull]` attribute behavior.
- `default` — declaratory; affects nullable-flow analysis; no emit.

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-20 — `unmanaged` constraint emit: what about types with `XPtr<T>` member?

**Offending Rev 1 quote (§5.8 line 1448):**
> "where T : unmanaged — recipient may rely on POD layout; emit can use direct memcpy."

**Bug.** `unmanaged` in C# means: no reference-type members anywhere transitively. The doc says "may rely on POD layout; emit can use direct memcpy". But `XPtr<T>` in C++ — does the transpiler consider it `unmanaged`? `XPtr<T>` is a wrapper around `T*`, which IS POD. But conceptually it's a managed reference. If the transpiler treats `XPtr<T>` as unmanaged, then a struct `struct Foo { XPtr<XActor> a; }` would qualify as unmanaged, but copying it via memcpy bypasses the XPACT_GC_STORE barrier.

**Fix.** Clarify: the `unmanaged` constraint in C# is **not** satisfied by types containing XObject references. The XPact mapping treats `XPtr<T>` as a managed reference for constraint purposes, even though the C++ type is POD. memcpy of structs containing XPtr<T> is BANNED — the constraint-satisfaction analysis must reject it.

Add diagnostic `XIL2CPP028 — type 'T' contains an XObject reference; cannot satisfy 'where U : unmanaged' constraint`.

**Severity.** MEDIUM-HIGH — silently bypassing the write barrier on memcpy is a GC-correctness bug.

---

### FIX-B-MEDIUM-21 — Generic delegate types (`Action<T>`, `Func<T,U>`) emit deferred but pattern unspecified

**Offending Rev 1 quote (§5.14):**
> "When Contract §1.1 lands the [XDelegate] amendment, Action<T> and Func<T> emit as a multi-cast delegate type with FXDelegate value-type {InstanceXObject*, FunctionPointer}..."

**Bug.** This commits a structure (`FXDelegate = {InstanceXObject*, FunctionPointer}`) without resolving:
1. **What's the call signature** of the function pointer for `Action<int>` vs `Func<int, string>` vs `Action<XActor, FVector>`? Each closed instantiation needs a distinct C++ delegate type, OR a type-erased pointer + per-call dispatch.
2. **Closure-bound lambdas as delegates.** A delegate value can capture state via a lambda: `Action a = () => x++;`. The captured state isn't `InstanceXObject*` — it's the closure struct. The `{InstanceXObject*, FunctionPointer}` model doesn't accommodate this. C# implements this by giving each lambda a synthesized class that holds the captures, and the delegate's instance becomes the synthesized class instance.
3. **Multicast** semantics: `Action a = ...; a += other; a -= still_other;` — the multi-cast delegate is a linked list / array of single-cast delegates. The "value-type" claim contradicts this — multicast cannot be a value type if it has variable-length state.

**Fix.** Redesign the delegate emit pattern as:
- Per closed delegate type (`Action<int>`, `Func<int, string>`), emit a wrapper struct that holds `{XPtr<XObject> target, void(*invoke)(void*, args...)}` for the single-cast case, OR `XArray<...>` for multi-cast.
- The capture problem is solved by the closure struct (FIX-B-HIGH-06) — the target is the closure struct, the invoke is the closure's `operator()`.
- Multicast addition allocates a new array; subtraction allocates a new shorter array (functional / immutable, matching C#'s `Delegate.Combine` / `Delegate.Remove` semantics).

This belongs in §5.14, which currently defers to post-Contract amendment.

**Severity.** MEDIUM (since deferred to post-amendment); HIGH if/when the amendment lands.

---

## Section 3 — Exception handling edge cases

### FIX-B-HIGH-20 — `try-catch-when (filter)` exception filters unspecified

**Offending Rev 1 quote (§5.12):**
> Shows `try { ... } catch (FileNotFoundException ex) { ... }` example only.

**Bug.** C# 6+ supports `catch (Exception ex) when (ex.Code == 500) { ... }` exception filters — the `when` clause runs *before* the stack is unwound, can re-throw, and affects which catch block fires. The doc shows no example of `when` and no lowering rule. Native C++ has no equivalent; the natural emit is to nest the `when` check inside the catch but only re-throw if the filter fails (which differs in semantics — the C# `when` filter does NOT unwind first; native C++ rethrow does).

**Fix.** Add §5.12 sub-rule: `catch (T ex) when (filter)` lowers to `catch (T ex) { if (!(filter)) throw; ... }`. Document that the unwind ordering is approximate (C++ rethrow unwinds; C# filter does not) and note the diagnostic for any code that relies on the non-unwinding semantics: `XIL2CPP033 — exception filter relies on non-unwinding semantics; consider restructuring`.

For the XResult lowering (Shipping), the filter check happens inline in the discriminator branch — no unwind issue.

**Severity.** HIGH — exception filters are subtle; silent misemit is dangerous.

---

### FIX-B-HIGH-21 — `finally` semantics + Tier 2 non-throwing requirement contradiction

**Offending Rev 1 quote (§5.12 line 1640-1670):**
> The Dev/Test lowering shows `try { ... } catch { ... goto finally_block; } finally_block: Cleanup();` pattern.

**Bug.** This `goto finally_block` pattern fails in the following case:
```csharp
try { SomethingMightThrow(); } finally { CleanupAlways(); }
```
There's no catch; the finally must run whether or not an exception fires. The doc's example has both catch and finally. The pure-finally case lowering needs:
```c++
try { Body(); } catch (...) { Cleanup(); throw; }
Cleanup();   // also run on non-exception path
```
This requires `Cleanup()` to run twice in the source code (once on exception path, once on normal path) — equivalent to RAII or `goto` cleanup. The doc shows neither pattern fully.

Additionally: `return` inside a `try` block that has a `finally` block requires the finally to run before the return. The doc does not show this either. Roslyn's standard lowering involves a synthesized local + return-after-finally; XIL2CPP must match.

**Fix.** Specify the canonical lowering for all three cases:
1. `try / catch / finally` — current doc text.
2. `try / finally` (no catch) — emit RAII guard via scope-exit, OR emit duplicated finally code on both paths, OR use a `__try / __finally` SEH-style macro.
3. `try / catch` (no finally) — emit catch only.
4. `return` inside try with finally — store return value in local, run finally, then return local.

Recommend a `scope_exit` RAII helper (`ScopedGuard`) as the canonical lowering — it generalizes to all four cases and integrates with `using` statement.

**Severity.** HIGH — `try / finally` (no catch) is the most common form and is missing from the example.

---

### FIX-B-MEDIUM-22 — `using` statement / `using` declaration emit semantics

**Offending Rev 1 quote (§5.15 line 1751-1775):**
> Shows lowering of `using (var x = new Disposable()) { ... }` to try/catch + Dispose.

**Bug.** The lowering shown is *incorrect* for the case where the body throws AND Dispose itself throws:
```c++
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
If `x.Dispose()` in the catch block throws, the original exception is **lost** (replaced by Dispose's exception). The C# spec says the original exception wins; Dispose-time exceptions should be swallowed or chained.

Additionally: C# 8's `using` declaration (`using var x = expr;`) extends through the end of the enclosing scope. The lowering must wrap the *rest of the scope* in the try/finally, not a new block. The doc says "Supported" in §4.1 but does not show the emit. The Roslyn lowering rewrites the function body — this is non-trivial.

**Fix.**
1. Replace the lowering with a Dispose-safe pattern (RAII scope guard that catches exceptions from Dispose and either swallows them or chains them via `std::nested_exception`).
2. For `using var x = expr;`, document the scope-extension rule: the entire remainder of the enclosing scope is the try/finally body.

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-23 — `Exception.Message` / `Exception.StackTrace` runtime support

**Offending Rev 1 quote (§5.12 line 1028-1034):**
> "outResult->error.stackTrace = ex.captureStack();"

**Bug.** This implies a `captureStack()` method on `XCSharpException`, but no such infrastructure is documented. The stack-walking machinery in a pure-transpile, no-managed-runtime engine is non-trivial:
- Win64 → `RtlCaptureStackBackTrace` + PDB-based symbolication.
- Linux/Quest 3 → `_Unwind_Backtrace` + DWARF.

Each requires per-arch helpers that the doc doesn't mention. Same for `Exception.Message` (a C# property) — does it bind to a `const FString*` field on `XCSharpException`? The exception type's surface is not described.

Additionally: stack traces should be symbolicated (line + file). The symbolication source is the PDB / DWARF — those must ship to the player's machine (production), or be uploaded to a crash service (telemetry). Master Plan probably constrains this; the doc should reference.

**Fix.** Either:
1. Define `XCSharpException`'s C# surface mapping fully (Message → `XCSharpString`; StackTrace → opaque `XStackTraceHandle` with a stringifier; Source / TargetSite — banned post-MVP).
2. Defer Exception class details to a separate XException.html spec and reference it.

**Severity.** MEDIUM.

---

## Section 4 — Reflection / AOT / Source generators

### FIX-B-HIGH-22 — `Activator.CreateInstance` warning-on-non-sim-path is wrong

**Offending Rev 1 quote (§5.11 line 1637):**
> "System.Activator.CreateInstance → XIL2CPP051 on sim-path; warning on non-sim-path."

**Bug.** `Activator.CreateInstance(Type t)` requires runtime metadata to invoke an arbitrary constructor by Type token. In a pure-transpile, no-managed-runtime engine, the metadata that would enable this does not exist at runtime; the FClass has a `ClassConstructorFn` slot but it's static-known (filled in at emit time). `Activator.CreateInstance(typeof(SomeKnownType))` could in principle map to `NewObject<SomeKnownType>(...)`, but `Activator.CreateInstance(someRuntimeType)` (where `someRuntimeType` is computed at runtime) has no mapping — you cannot dispatch to an unknown FClass without registering every possible type at the emit-time set.

The doc demoting to "warning" on non-sim-path is misleading: this should be a hard ban (`XIL2CPP051` as error, not warning). The XPact model genuinely cannot host runtime-driven type construction.

Same issue applies to `MakeGenericType` / `MakeGenericMethod` — the doc bans `Type.GetMethods` / `MethodInfo.Invoke` (§5.11 line 1634), but doesn't mention these two. They're equally fatal — making a generic type at runtime requires the runtime to substitute type parameters in a way that XPact cannot do without a hosted CLR.

**Fix.**
1. Promote `Activator.CreateInstance` to error on all TUs (not just sim-path). Diagnostic `XIL2CPP051 — Activator.CreateInstance requires runtime type metadata; use a typed factory or compile-time T parameter`.
2. Add `XIL2CPP053 — Type.MakeGenericType / Type.MakeGenericMethod are not supported (no runtime generic instantiation)`.
3. Suggest the user use `XObject.New<T>` with a compile-time T.

**Severity.** HIGH — silently allowing on non-sim-path leaves the user thinking it works, but the runtime cannot back it.

---

### FIX-B-MEDIUM-24 — `[ModuleInitializer]` attribute unspecified

**Offending Rev 1 quote:** None.

**Bug.** C# 9 introduced `[ModuleInitializer]` — a static method attribute that runs at module load. Used for one-time DI registration, runtime setup, etc. Common in production code. The doc does not mention it.

Natural emit: a C++ static initializer in the `.cs.cpp` that calls the attributed method at DLL load time (or via the `Z_Construct_FClass_*` aggregation).

**Fix.** Add §5.4 sub-rule: methods carrying `[ModuleInitializer]` emit as anonymous-namespace static-initializer functions that call the method at DLL load. Honors C# semantics (runs before any user code).

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-25 — `[Conditional("DEBUG")]` attribute behavior

**Offending Rev 1 quote:** None.

**Bug.** `[Conditional("DEBUG")]` attribute on a method causes the C# compiler to *omit calls to the method* in builds that don't define `DEBUG`. This is conditional-compilation by attribute, common for `Debug.Assert`-style helpers. The doc does not mention how the transpiler handles it. The Roslyn semantic model knows which symbols are defined; XIL2CPP needs to respect the conditional based on the per-target manifest's conditional defines.

**Fix.** Add §5.4 sub-rule: `[Conditional("X")]` on a method → at every call site, check if `X` is in the module's `conditional_symbols` list; if not, elide the entire call (matching Roslyn's behavior). This needs to interact with the per-module manifest's `conditional_symbols` field which is not currently in the manifest schema (§7.6).

**Severity.** MEDIUM — extends to debug-only assertions; missing this means Shipping builds carry debug overhead.

---

### FIX-B-MEDIUM-26 — Source generators "banned" but pre-generated code is allowed?

**Offending Rev 1 quote (§1.2):**
> "A Roslyn source-generator hosting environment. ... XIL2CPP reads C# source as-written; it does NOT execute source generators at transpile time."

**Bug.** This says XIL2CPP doesn't run source generators. But if the user's build pipeline (outside XPact) runs generators to produce `.cs` files (e.g., Cocona / Mediator emit `.g.cs` files), those generated files are normal `.cs` from XPact's perspective. The doc doesn't say whether generator-emitted files are allowed or rejected. The natural answer: allowed (they're just `.cs` files).

But common generated patterns (e.g., generators that emit partial-method bodies for `[GeneratedRegex(...)]`) rely on the C# runtime's reflection — XIL2CPP can't handle them.

**Fix.** Clarify: any `.cs` file in the module's `csharp_sources` is parsed by XIL2CPP regardless of origin. Generated files that use reflection / runtime metadata will fail at semantic analysis with the relevant diagnostic. Recommend the build pipeline not run generators whose output depends on the runtime.

**Severity.** MEDIUM — clarification needed; user confusion likely.

---

### FIX-B-MEDIUM-27 — `typeof(T)` over open generic type unspecified

**Offending Rev 1 quote (§5.11 line 1626):**
> "typeof(T) → ::XCore::Reflect::XReflectionRuntime::FindClass(::FName("T")) or the directly-emitted T::StaticClass() when T is statically known."

**Bug.** `typeof(List<>)` (the *open* generic — note the missing `T`) is legal C# and returns a `Type` representing the open generic. Within a generic method, `typeof(T)` (where T is a method's type parameter) resolves at runtime to the closed type — but how does XIL2CPP emit it? The fallback ("FindClass with FName") implies a runtime lookup, which means the closed-instantiation walk must register every closed type by FName so it can be discovered. The doc says `XReflectionRuntime::FindClass` exists; whether it can find a *closed instantiation* of a *generic* type by name requires an FName-based lookup scheme that the doc does not specify.

**Fix.**
- `typeof(SomeClosedType)` → `SomeClosedType::StaticClass()` — direct.
- `typeof(SomeOpenGeneric<>)` → `FindClass("SomeOpenGeneric")` returning a "generic-type-definition" FClass that's distinct from closed instantiations.
- `typeof(T)` inside a generic method → at the closed instantiation, T is known; emit `T::StaticClass()` after type substitution.

Add `XIL2CPP054 — typeof(T) at open-generic site cannot be resolved at compile time`.

**Severity.** MEDIUM.

---

### FIX-B-MINOR-01 — `typeof(T).FullName` is interned string — but does it include nesting?

**Offending Rev 1 quote (§5.11 line 1628):**
> "typeof(T).FullName → interned const FString* matching the literal 'Namespace.T'."

**Bug.** What about nested types? `Outer.Inner` in C# has FullName `"Outer+Inner"` per the IL convention (with `+` separator). The doc says `"Namespace.T"` — does that include nesting? What about closed-instantiation generics? `List<int>` has FullName `"System.Collections.Generic.List`1[[System.Int32, mscorlib]]"`. The doc's "literal 'Namespace.T'" is too short to match the conventional `.NET` FullName format.

**Fix.** Clarify the FullName format. Either:
- Match .NET conventions exactly (with `+` for nesting and `[[...]]` for generic args).
- Use a simplified XPact-canonical form and document it (`Outer.Inner<T>` or `Outer::Inner<T>`).

**Severity.** MINOR — depends on user code's dependence on the exact format.

---

## Section 5 — Strings, characters, encoding

### FIX-B-HIGH-23 — `char` (UTF-16) vs `wchar_t` (16 or 32 bit) cross-arch ambiguity

**Offending Rev 1 quote:** None. The §4.1 matrix lists `string` mapped to `XCSharpString` but does NOT list `char` at all.

**Bug.** C# `char` is a 16-bit UTF-16 code unit. C++ `wchar_t` is 16-bit on Windows but **32-bit on Linux/Android** (Quest 3 is Android). A naive emit `wchar_t` for `char` is wrong on Quest 3. The doc never says what C++ type `char` maps to.

**Fix.** Map C# `char` → `uint16_t` (or `char16_t` from C++11). Operations like `char.IsDigit(c)` map to XPact helpers that don't depend on the platform's `wchar_t` model. Document explicitly in §5.5.

**Severity.** HIGH — silent cross-arch divergence.

---

### FIX-B-HIGH-24 — String interpolation lowered to mutable builder is wrong semantic

**Offending Rev 1 quote (§3.2 / §5.5):**
> "String interpolation — $"hello {name}" → String.Format("hello {0}", name) → XPact-specific FString builder per §5.5.
> AST-normalized form:
> var __sb = new XCSharpStringBuilder();
> __sb.Append("hello ");
> __sb.Append(name);
> var result = __sb.Build();"

**Bug.** This is wrong on two counts:
1. C# 10 introduced `InterpolatedStringHandler` and `DefaultInterpolatedStringHandler` — interpolation lowers to a sequence of `AppendLiteral` / `AppendFormatted` calls on a HANDLER, which can be customized per-target (`[InterpolatedStringHandlerArgument]` etc.). The "lower to StringBuilder" pattern is the pre-C#-10 lowering; C# 12 doesn't use it directly.
2. The "create a builder, append, build" pattern allocates the builder. The doc doesn't say whether the builder is stack-allocated or heap-allocated. If heap-allocated, every interpolation site incurs an alloc — unacceptable on sim-path.

**Fix.** Match C# 10's lowering: lower interpolation to a `DefaultInterpolatedStringHandler` pattern with a stack-allocated handler. Emit the handler's stack-allocation per-interpolation site. On sim-path, the handler must NOT escape to the heap.

Alternatively, document the simplified XPact lowering (the §5.5 builder approach) and confirm the builder is stack-allocated; OR ban interpolation on sim-path and require manual concatenation.

**Severity.** HIGH — interpolation is on every log line.

---

### FIX-B-MEDIUM-28 — `String.Equals` ordinal-by-default vs operator==

**Offending Rev 1 quote:** None.

**Bug.** C# `==` on strings calls `String.op_Equality`, which calls `String.Equals(a, b)` using **ordinal** comparison (NOT culture-sensitive). The doc does not say whether `==` on `XCSharpString` follows this. If `XCSharpString::operator==` just checks `m_storage` pointer equality, and `m_storage` is interned (so identical strings have identical pointers), then `==` works for literal-equal strings BUT not for content-equal strings constructed from different sources (e.g., one from a literal, one from a builder).

**Fix.** Document XCSharpString's `==` semantics: pointer-equality on `m_storage` IS sufficient if the literal-interning invariant holds AND if any non-literal string construction goes through the same intern table. If non-literal strings (e.g., builder output) don't intern, `==` must fall back to content comparison.

Recommend: builder output also interns (same intern table) → `==` always equals pointer-equality → fast.

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-29 — `String.IsNullOrEmpty` / `IsNullOrWhiteSpace` not mapped

**Offending Rev 1 quote:** None.

**Bug.** These are the most commonly used string utility methods. The doc maps `s.AsSpan() / s.IndexOf / s.Substring / s.AsCodepoints` (§5.5 line 1316) but doesn't mention `IsNullOrEmpty` (a static method on `String`) or `IsNullOrWhiteSpace`. Without an explicit mapping, the transpiler might fall through to the .NET 8 BCL implementation, which doesn't exist in the XPact runtime — emitting XIL2CPP010 (BCL not in mapped subset).

**Fix.** Add to the binding table: `String.IsNullOrEmpty(s)` → `XCSharpString::IsNullOrEmpty(s)`; same for `IsNullOrWhiteSpace`. Update §5.5.

**Severity.** MEDIUM — very common idiom.

---

## Section 6 — LINQ / iterators / collections (partially covered)

### FIX-B-MEDIUM-30 — `yield return` sim-path status unclear

**Offending Rev 1 quote (§4.1 / §5.9):**
> "yield return iterators — Supported (state-machine + XGCRootSpan). Both columns."

**Bug.** §4.1 lists yield-return as supported on sim-path AND non-sim-path. §5.9 line 1583 says "Same treatment as async: state-machine struct with XGCRootSpan over captures." But yield iterators allocate the state machine on first `GetEnumerator()` call, which is heap allocation — same as async, which IS banned on sim-path. Why is yield-return allowed on sim-path?

The non-determinism risk is different: yield iterators are deterministic given a deterministic input, unlike async (which depends on thread scheduling). So the determinism rationale doesn't apply. But heap allocation per iterator instance is still a sim-path cost.

**Fix.** Decide: either ban yield-return on sim-path (consistency with async), or allow it explicitly because deterministic (current Rev 1 position). If the latter, document the heap-allocation cost and the per-frame budget; require sim-path yield-iterators to use a pool (pre-allocated state machines).

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-31 — LINQ banned on sim-path but allowed in MVP non-sim-path is unfounded

**Offending Rev 1 quote (§4.1 line 691):**
> "System.Linq.* — BANNED (sim-path); Post-MVP (non-sim-path)."

**Bug.** Saying LINQ is "post-MVP" on non-sim-path means the MVP cannot use LINQ at all (since "post-MVP" means after MVP ships). But the doc also says (§2.2 line 189) that LINQ post-MVP "Non-sim-path LINQ is post-MVP" — so MVP is LINQ-less. This is a substantial limitation that users may not expect; LINQ is one of the most common C# idioms.

The §2.2 rationale ("the deferred-execution semantics need a careful design") is sound, but the MVP scope should be clearer. Currently §4.1 has it as "Post-MVP" — meaning MVP users get `XIL2CPP100` ban on every LINQ usage.

**Fix.** Either:
1. Promote a subset of LINQ to MVP (the synchronous, non-deferred operators: `.ToList()`, `.Sum()`, `.Count()`, `.First()`, `.Any()`, `.All()` over an enumerable input). Document that deferred operators (`.Where`, `.Select` returning IEnumerable) are post-MVP.
2. Confirm MVP is LINQ-less and add a high-visibility deprecation/migration story in the docs.

Add diagnostic `XIL2CPP100 — LINQ is post-MVP; use explicit foreach + TArray helpers (TArray<T>::Filter, TArray<T>::Map)` with the recommended replacement.

**Severity.** MEDIUM — major ergonomic regression that should be planned, not stumbled into.

---

### FIX-B-MEDIUM-32 — Custom IEnumerator implementations allowed?

**Offending Rev 1 quote:** None directly; §4.1 lists `foreach` as supported.

**Bug.** A user can write `class MyIterator : IEnumerator<int> { public bool MoveNext() {...} public int Current => ...; public void Dispose() {...} public void Reset() {...} object IEnumerator.Current => Current; }`. The transpiler must handle:
- The `IEnumerator<T>` C# interface — explicit interface implementation (FIX-B-HIGH-15).
- `Dispose()` and `Reset()` — IDisposable behavior on a non-XObject.
- `object IEnumerator.Current` — this is the boxing problem (FIX-B-CRIT-02). Same dependency.

Without addressing the boxing problem, custom IEnumerator implementations are broken.

**Fix.** Resolve FIX-B-CRIT-02 first. Then add §5.9 (or §5.7) sub-rule: custom IEnumerator emit follows the interface mapping.

**Severity.** MEDIUM (blocked by CRIT-02).

---

## Section 7 — Hard contradictions revisited

### FIX-B-CRIT-05 — `XObject.New<T>` constraint `where T : XObject, new()` makes `XObject` an unsatisfied receiver

(Same root cause as FIX-B-HIGH-16; restated to highlight the structural contradiction.)

**Offending Rev 1 quote (§2.3 line 247):**
> `public static T New<T>(XObject outer, FName name, EObjectFlags flags) where T : XObject, new();`

**Bug.** Roslyn enforces `where T : new()` at the *closure-of-callers* — every closed call site must show `T` has an accessible public default constructor. With XIL2CPP001 banning every `new Foo()` for XObject-derived `Foo`, the compiler analyzer fires CS0310 (`The type 'Foo' must have a public parameterless constructor in order to use it as parameter 'T'`). But wait — XIL2CPP001 only bans `new Foo()` *expression statements*; the ctor still EXISTS in the class. So Roslyn's CS0310 may not fire. But then the factory's body can call `new T()` via the constraint — bypassing XIL2CPP001. So:
- Either the diagnostic fires at the call site (`new T()` inside the factory body) — but the factory has `[XObjectInternalConstructor]`, so it should be suppressed → the factory works → users can call `XObject.New<Foo>(...)`.
- Or the diagnostic fires at the source-level `new Foo()` — but the factory's `new T()` is NOT a `new Foo()` (it's `new T()` over a type parameter) and might bypass the syntactic check.

The Pass 3 check in §2.3 line 261-272 walks `ObjectCreationExpressionSyntax` nodes — `new T()` IS an `ObjectCreationExpressionSyntax`, so the check would fire. But then the factory itself is broken. The `[XObjectInternalConstructor]` attribute suppresses for the factory, but how does suppression work for `new T()` inside the factory? The semantic model would still need to resolve `T` (it's open) and the diagnostic would fire at every closed instantiation.

This is a tangled state that needs unwinding. See FIX-B-HIGH-16 for the fix.

**Severity.** CRITICAL.

---

### FIX-B-CRIT-06 — `Object[]` / `List<object>` over XObject — boxing problem extends

**Offending Rev 1 quote (§5.7 table line 1358-1359):**
> "List<object> → TArray<void*> with XGCRootSpan Conservative + warning XIL2CPP070."

**Bug.** Already partially handled (Conservative span warns on sim-path), but the bigger issue is: `List<object> l = new(); l.Add(5);` boxes the `int`. There's no boxing in XPact (CRIT-02 above). So `List<object>` can hold:
- XObject* references (works, gets conservatively traced).
- Boxed value types — UNDEFINED in XPact.

If a user writes `List<object> l = new(); l.Add(new XActor()); l.Add(42);`, the second `Add(42)` has no defined emit. The doc says `TArray<void*>` — a `void*` cannot hold a value type at all. The conservative-span trace would walk every `void*` looking for valid XObject pointers; the `42` would either misidentify as a pointer (a randomly-located XObject) or fail validation.

**Fix.** This is the boxing problem (CRIT-02) restated for containers. Same resolution: pick a boxing strategy or ban `List<object>` with value type entries. Recommend ban: diagnostic `XIL2CPP073 — value type stored in object-typed container requires boxing, which is not supported`.

**Severity.** CRITICAL — silent data corruption / GC crash.

---

## Section 8 — Mangling and ABI alignment

### FIX-B-HIGH-25 — Mangled name uses `::` for namespace boundary; example contradicts

**Offending Rev 1 quote (§5.2 line 991):**
> `extern "C" void _v1ab12cd34__Simgenics__XPact__GameFramework__Valve__SetOpenFraction_P_R_Simgenics__XPact__GameFramework__Valve_V_float`

**Bug.** Contract §2.2 line 487 says namespaces use `::`. The example uses double underscore `__` — this is the LINKER-visible form (per §2.3 line 517: `::` → `__`). The doc is using the linker-visible form throughout the example, which is fine, but the mangling rule said "{Namespace}: dots → ::" — so the *internal mangled name* uses `::`, the *linker symbol* uses `__`. The example uses `__` directly, mixing the two. Reader confusion.

Worse: the double-underscore form is RESERVED by the C/C++ standards for the implementation. Using `__` as a separator may collide with platform-reserved symbols.

**Fix.**
1. Clarify the example uses the linker-visible form (add a comment).
2. Consider switching the linker-form separator from `__` to something less collision-prone (e.g., `_o_` for "outer separator"). This is a Contract amendment, so flag for cross-doc coordination.

**Severity.** HIGH — `__`-prefixed names are reserved by the C/C++ standard; collision possible.

---

### FIX-B-MEDIUM-33 — Contract version `v1ab12cd34` is a placeholder; how does it propagate?

**Offending Rev 1 quote (§5.2 line 991):**
> `_v1ab12cd34__Simgenics__...`

**Bug.** The "Contract version hash" `v1ab12cd34` is shown as a placeholder. In production, this hash changes with every Contract revision. The doc does not specify the propagation:
- XHT and XIL2CPP must both use the SAME contract version hash at emit time (otherwise XHT's emitted singleton-getter declaration and XIL2CPP's emitted definition have different mangled names — link failure).
- How is this hash distributed? Per Contract §10.2 the manifest carries `contract_version`. But the actual hash bytes — are they SHA-256 of the contract HTML file? BLAKE3 of a normalized data form?

**Fix.** Specify the contract-version hash computation: SHA-256 (or BLAKE3) of a canonical-form `XContractV1.fbs` (the FlatBuffers schema). Both tools compute it identically. Mismatch is caught by the `XIL2CPP141 — manifest's contract version mismatches XIL2CPP-supported set` diagnostic.

**Severity.** MEDIUM.

---

### FIX-B-MINOR-02 — Mangling of method name `op_Equality` etc. needs full list

**Offending Rev 1 quote (Constraint §2.2 line 454):**
> "C# operator overload → CLR-convention method name: op_Add, op_Equality, etc."

**Bug.** The "etc." is unspecified. CLR defines: `op_Implicit`, `op_Explicit`, `op_Addition`, `op_Subtraction`, `op_Multiply`, `op_Division`, `op_Modulus`, `op_BitwiseAnd`, `op_BitwiseOr`, `op_ExclusiveOr`, `op_LeftShift`, `op_RightShift`, `op_Equality`, `op_Inequality`, `op_GreaterThan`, `op_LessThan`, `op_GreaterThanOrEqual`, `op_LessThanOrEqual`, `op_Increment`, `op_Decrement`, `op_True`, `op_False`, `op_OnesComplement`, `op_LogicalNot`, `op_UnaryPlus`, `op_UnaryNegation`. Plus C# 11: `op_UnsignedRightShift`. Plus checked variants: `op_CheckedAddition` etc. The doc should enumerate.

**Fix.** Add a §5.2 sub-section listing the full CLR operator-name set, and a per-operator C++ binding (most are `operator+` etc., but `op_Implicit` and `op_Explicit` are special per FIX-B-MEDIUM-08).

**Severity.** MINOR.

---

## Section 9 — Concurrency / volatile / memory

### FIX-B-HIGH-26 — `Interlocked.*` APIs unspecified

**Offending Rev 1 quote (§7.4 line 2097 / §5.20):**
> "System.Threading.* — BANNED on sim-path; Post-MVP (non-sim-path)."

**Bug.** `Interlocked.Increment` / `Interlocked.CompareExchange` are the canonical atomic primitives in C#. They're in `System.Threading`. The doc bans `System.Threading.*` wholesale, which removes Interlocked. But Interlocked is the ONLY way to safely modify a shared field — without it, multi-threaded C# code cannot be correct. (Even on sim-path: the single-threaded discipline still uses Interlocked for memory model reasons.)

**Fix.** Carve Interlocked out of the ban:
- `Interlocked.*` allowed on sim-path AND non-sim-path; maps to `std::atomic<T>::fetch_add` / `compare_exchange_strong` / etc.
- Other `System.Threading.*` members (Thread.Start, ThreadPool, Task.Run) remain banned per §5.20.

Add §5.21 sub-section mapping Interlocked APIs to std::atomic operations.

**Severity.** HIGH — without Interlocked, no thread-safe field updates are possible.

---

### FIX-B-MEDIUM-34 — `Span<T>` lifetime restrictions

**Offending Rev 1 quote (§5.13 line 1721-1737):**
> `template <typename T> struct TSpan { T* m_data; size_t m_count; ... };`

**Bug.** Span lifetime in C# is bounded by stack frame discipline — `ref struct` rules forbid storing Span in heap-allocated objects, capturing in lambdas, returning across `async` boundaries, etc. The doc doesn't mention this; the C++ emit as a plain struct doesn't enforce the rule. A user writing:
```csharp
Func<Span<int>> f = () => { Span<int> s = stackalloc int[4]; return s; };
```
is illegal in C# (CS8347), but the C++ emit (a plain struct) would compile and return a dangling pointer.

**Fix.** Document that `Span<T>` / `ReadOnlySpan<T>` MUST be passed only to non-capturing contexts; the analyzer relies on Roslyn's `ref struct` ruleset to enforce; XIL2CPP does not need to re-check at emit time (CS8347 fires at parse). Add diagnostic if Roslyn doesn't catch (e.g., post-XIL2CPP analyzer): `XIL2CPP082 — Span<T> escapes its stack frame; ref struct rules violated`.

**Severity.** MEDIUM.

---

### FIX-B-MEDIUM-35 — `stackalloc` size limit unenforced

**Offending Rev 1 quote (§5.13 line 1717):**
> "For dynamically-sized stackalloc: Span<int> buf = stackalloc int[n]; emits via alloca: int32_t* _stack_buf = static_cast<int32_t*>(alloca(n * sizeof(int32_t)));"

**Bug.** Unbounded `alloca` is a stack-overflow risk. C# does not enforce a limit; `stackalloc int[1_000_000]` will stack-overflow at runtime. The doc does not mention this. Defensive practice: emit a runtime check against a per-target stack-size limit; bail with a recoverable allocation if the size exceeds.

**Fix.** Add §5.13 sub-rule: `stackalloc T[n]` emits a runtime check `if (n * sizeof(T) > XPACT_MAX_STACKALLOC_BYTES) { /* fallback to heap or abort */ }`. Default `XPACT_MAX_STACKALLOC_BYTES = 64KB`.

**Severity.** MEDIUM.

---

## Section 10 — Miscellaneous

### FIX-B-MEDIUM-36 — `goto case` / `goto default` inside switch

**Offending Rev 1 quote (§4.1):**
> "goto (and goto case / goto default) — Supported (banned in async per dataflow)."

**Bug.** "Banned in async per dataflow" is correct but unspecified — what's the diagnostic? Also `goto case` / `goto default` are switch-statement-specific gotos that require their own emit (which the C++ switch supports natively via case labels, but only if the switch is preserved in lowering; if XIL2CPP lowers switch to if-chains for pattern matching, `goto case` no longer makes sense).

**Fix.** Add §5.3 sub-rule: `goto case X;` only legal inside a switch-statement (not switch-expression); maps to C++ `goto _case_X;` where `_case_X` is a synthesized label inserted at the case body's start.

**Severity.** MEDIUM.

---

### FIX-B-MINOR-03 — `out var x` declaration pattern

**Offending Rev 1 quote:** None.

**Bug.** `bool ok = dict.TryGetValue(k, out var v);` declares `v` as a new local. The §5 mapping doesn't cover this. Natural emit: a forward-declaration of the local + the out-arg pass.

**Fix.** Add §5.3 sub-rule: `out var x` declares `x` at the call site's enclosing scope (or smaller if `x` is then used only in the if-branch); the variable is forward-declared if needed.

**Severity.** MINOR.

---

### FIX-B-MINOR-04 — `is var x` declaration pattern

**Offending Rev 1 quote:** None.

**Bug.** `if (obj is SomeType x) { ... }` is the type-pattern with declaration. Listed as supported in §4.1 but no explicit emit example. Should emit a downcast + a local that's typed to the more-specific type.

**Fix.** Add §5.6 sub-rule per FIX-B-MEDIUM-06.

**Severity.** MINOR (redundant with FIX-B-MEDIUM-06).

---

### FIX-B-MINOR-05 — Discard `_` semantics

**Offending Rev 1 quote:** None.

**Bug.** `_` in tuple deconstruction (`var (a, _) = t`), in is-patterns (`obj is (_, var b)`), in out-args (`Foo(out _)`), and in lambdas (`(_, _) => 0`) is a discard — no binding. Doc doesn't mention.

**Fix.** Add §5.3 (and §5.6, §5.7) sub-rules: `_` is recognized syntactically as a discard; no local emitted; the value is dropped.

**Severity.** MINOR.

---

### FIX-B-MINOR-06 — Local function emit duplication with lambda

**Offending Rev 1 quote (§5.9):**
> "Local functions. Captured-variable analysis lowers them to a generated class with the captures as fields, similar to lambdas."

**Bug.** Lowers local functions to "a generated class similar to lambdas" — but local functions are NOT first-class delegates in C# (you can't assign them to a `Func<>` variable directly without conversion). Wrapping them in a class is overkill if they're never captured by-reference. Non-capturing local functions per §3.2 should be straight free functions. The doc mentions both treatments (one in §3.2, one in §5.9) but the discrimination is unclear.

**Fix.** Clarify: non-capturing local function → free function in anonymous namespace. Capturing local function → display-class struct same as lambda (FIX-B-HIGH-06 applies here too). Don't say "similar to lambdas" — say "uses the same display-class lowering".

**Severity.** MINOR.

---

### FIX-B-MINOR-07 — `params` array allocation

**Offending Rev 1 quote (§4.1):**
> [None — params parameter is not in the matrix.]

**Bug.** `void Log(params object[] args)` is called as `Log("a", "b", "c")` — the compiler synthesizes an `object[]` allocation at the call site. In a no-boxing engine, `params object[]` is problematic. Even `params T[]` requires a fresh array allocation per call site, which is hot-path-unfriendly.

**Fix.** Document `params T[]` allocates a temp array; on sim-path, recommend `params ReadOnlySpan<T>` (post-MVP) or fixed-arity helpers; warning diagnostic on sim-path for `params T[]` use: `XIL2CPP083 — params T[] allocates per-call; consider params ReadOnlySpan<T> (post-MVP) or pass an explicit array`.

**Severity.** MINOR.

---

### FIX-B-MINOR-08 — `unsafe` block emit example doesn't address GC interaction

**Offending Rev 1 quote (§5.18 line 1826-1839):**
> Shows `Marshal.AllocHGlobal` example only.

**Bug.** The example is for non-managed-memory unsafe. The interesting case — `unsafe { int* p = &someInt; }` where someInt is a stack local — is not shown. What about `fixed (XActor* p = &actor) { ... }`? `&actor` is illegal in C# (you can't take address of a class instance), but `fixed (int* p = &actor.HealthAmount) { ... }` is legal and creates a pointer into an XObject. If GC runs and the cell pool re-binds, the pointer is invalidated. The doc says "fixed is no-op on non-moving GC" (§5.17) but here the GC is also non-cell-rebinding-during-runtime, which the doc never directly states. Worth a clarification.

**Fix.** Clarify: the non-moving GC does NOT relocate cells, BUT it CAN free them (mark-and-sweep). A `fixed`-acquired pointer into an XObject's field is valid only until the XObject is collected. The user MUST keep a strong reference to the XObject while using the pointer. Document explicitly in §5.17 and §5.18.

**Severity.** MINOR.

---

### FIX-B-MINOR-09 — `Span<XActor>` ban — what about `Span<XPtr<XActor>>`?

**Offending Rev 1 quote (§5.7 line 1366):**
> "Span<XActor> → BANNED in MVP (XIL2CPP080)"

**Bug.** `Span<XActor>` is banned. But what about `Span<XPtr<XActor>>` — the wrapper that emits to a managed slot? The XPtr is conceptually the slot view of an XObject reference; a Span of those would be a view over a sequence of slots. The ban presumably also applies here, but the doc doesn't say.

**Fix.** Clarify in §5.7: any Span where the element type is or contains an XObject reference is banned in MVP, regardless of whether it's directly `Span<XActor>` or indirectly `Span<XPtr<XActor>>` or `Span<SomeStructWithXActorField>`. Same XIL2CPP080.

**Severity.** MINOR.

---

### FIX-B-MINOR-10 — Diagnostic numbering — gaps and reusage

**Offending Rev 1 quote (§12 table):**
> The catalog lists XIL2CPP001-XIL2CPP159 in numbered groups.

**Bug.** Some gaps: XIL2CPP015-019 (mentioned but unallocated in §12); XIL2CPP022-029 (gap); XIL2CPP034-039 (gap); XIL2CPP048-049 (gap); XIL2CPP053-059 (gap); XIL2CPP061-069 (gap); XIL2CPP073-079 (gap); XIL2CPP082-089 (gap); XIL2CPP093-099 (gap); XIL2CPP101-119 (large gap); XIL2CPP125-139 (large gap); XIL2CPP143-149 (gap); XIL2CPP152-159 (gap).

The audit's findings above use several gaps for new diagnostics (XIL2CPP015, 016, 017, 018, 019, 026, 028, 033, 053, 054, 073, 082, 083, 092, 125, 145, 154). Need cross-checking with the central allocation table.

**Fix.** Reserve narrower diagnostic-code ranges per concern; document the allocation reserve more rigorously. Cross-check the audit's proposed codes against the §12 table to avoid collisions.

**Severity.** MINOR (housekeeping).

---

### FIX-B-MINOR-11 — Generic method count: depth-16 may be too low

**Offending Rev 1 quote (§5.8 line 1442):**
> "The walk recursion bound is depth 16; exceeding emits XIL2CPP123."

**Bug.** Depth 16 is borrowed from XCoreXObject FIX-A-MIN; whether it's enough for real codebases is unverified. Recursive generic types like Linq's expression-tree builders can blow past depth 16. The diagnostic message could carry a guide: "to increase the depth, set `[XGenericMaxDepth = 32]` on the module" — IF the doc designs an override mechanism.

**Fix.** Either confirm depth 16 is correct (benchmark on a real codebase) or add a per-module override. Either way, document the rationale.

**Severity.** MINOR (parameter tuning).

---

### FIX-B-MINOR-12 — `with` expression on structs (vs records)

**Offending Rev 1 quote (§3.2):**
> "Record with expressions — record1 with { X = 10 } → ..."

**Bug.** C# 10+ extended `with` to *all* struct types (not just records). `MyStruct s = ...; var s2 = s with { X = 10 };` works on any struct. The doc says "Record with expressions" — implying restricted to records. The lowering for non-record structs is simpler (memberwise copy + setter) and worth documenting.

**Fix.** Generalize §3.2 to "with-expressions" (not "Record with-expressions") and document the struct case.

**Severity.** MINOR.

---

## Section 11 — Final Synopsis

### Severity totals

| Severity   | Count | Examples |
|------------|-------|----------|
| CRITICAL   | 6     | `where T : new()` constraint contradiction (HIGH-16 / CRIT-05); boxing for `o switch { int n => ... }` (CRIT-02); plain-class reference identity (CRIT-03); `using X = ...` alias missing (CRIT-04); `List<object>` boxing (CRIT-06) |
| HIGH       | 21    | static abstract members, params ReadOnlySpan, ref readonly, nameof on generic param, lambda noexcept, lambda capture, lambdas not in tier classification, switch expression emit, readonly fields, auto-property backing names, indexers, indexers (duplicate), volatile, new vs override, multiple interface emit, explicit interface impl, cross-module generic FClass duplication, Sort/IComparable, IEnumerable<T>/custom iterators, exception filters, finally with no catch, char vs wchar_t, string interpolation handler, Activator.CreateInstance, Interlocked, mangling `__` reserved |
| MEDIUM     | 23    | various |
| MINOR      | 12    | various |

### Top 3 most impactful findings

1. **FIX-B-CRIT-05 / HIGH-16** (where T : new() constraint defeats Locked Commitment 3): the factory signature is mutually contradictory with the XIL2CPP001 diagnostic.
2. **FIX-B-CRIT-02** (boxing of value types into `object`): structurally undefined emit path; cascades to FIX-B-CRIT-06 (object-typed containers), FIX-B-MEDIUM-32 (custom IEnumerator), most LINQ paths, and explicit interface impl.
3. **FIX-B-HIGH-06** (lambda capture semantics): by-value capture as currently shown is semantically wrong; mutations after capture are silently dropped. Requires Roslyn-style display-class hoisting, which is a substantial §5.9 rewrite.

### Rev 1 structural soundness

The umbrella is sound; the pipeline (§3), tier classification (§3.3), GC integration (§6), hot-reload integration (§8), and XBT integration (§9) are well-specified and consistent with the locked commitments. Sections 1, 2, 7 (sim-path), 9-10 (XHT/XBT boundaries), 11 (acceptance), 12 (diagnostics), 13-15 (FP harness, implementation phases) are well-structured for a Rev 1 design draft.

§5 (the §IL/AST → C++ mapping) is the section with the most findings; most are incremental (specify the rule, no design change needed). The structural issues are in:
- Boxing / object (CRIT-02, CRIT-06).
- Plain-class reference identity (CRIT-03).
- Generic constraint / factory contradiction (CRIT-05).
- Lambda capture (HIGH-06).
- `using` alias (CRIT-04).

These 5 are the priorities. Once resolved, the §5 mapping rules become spec-grade. The remaining 57 findings are detail-level and can be addressed in a Rev 2 polish pass.

### C# 12 features missing entirely (vs incomplete)

- **Missing entirely:**
  - `using X = SomeType;` (alias-any-type) — C# 12 — FIX-B-CRIT-04.
  - `static abstract` interface members — C# 11 — FIX-B-HIGH-01.
  - `ref readonly` parameters — C# 12 — FIX-B-HIGH-03.
  - `volatile` keyword — pre-C# — FIX-B-HIGH-12.
  - `Interlocked.*` carveout — pre-C# — FIX-B-HIGH-26.
  - `[ModuleInitializer]` attribute — C# 9 — FIX-B-MEDIUM-24.
  - `[Conditional]` attribute — pre-C# — FIX-B-MEDIUM-25.
  - `default(T)` expression — pre-C# — FIX-B-MEDIUM-03.
  - Extension methods — C# 3 — FIX-B-HIGH-11.
  - Explicit interface implementation — pre-C# — FIX-B-HIGH-15.
  - `char` type mapping — pre-C# — FIX-B-HIGH-23.
- **Incomplete:**
  - Pattern matching (positional, property, list, type, relational, logical) — listed but no emit examples per kind — FIX-B-MEDIUM-06.
  - Switch expression — listed but no example — FIX-B-HIGH-07.
  - `new` (hiding) method modifier — FIX-B-HIGH-13.
  - Lambda capture — FIX-B-HIGH-06.
  - `try / finally` (no catch) — FIX-B-HIGH-21.
  - Auto-property backing field naming — FIX-B-HIGH-09.
  - User-defined conversions — FIX-B-MEDIUM-08.
  - Constructor chaining — FIX-B-MEDIUM-09.
  - Indexers — listed twice, examples missing — FIX-B-HIGH-10.
  - Tuple names — FIX-B-MEDIUM-10.
  - Generic delegate types — deferred but pattern needs work — FIX-B-MEDIUM-21.

### Structurally wrong IL/AST → C++ mappings

(Cross-referencing the executive summary):
1. Boxing of value types into `object` — no emit story (CRIT-02).
2. Plain (non-XObject) C# class reference identity defaulting to value-semantics (CRIT-03).
3. Lambdas emitted with `noexcept` (HIGH-05).
4. Lambda capture semantics: by-value instead of Roslyn-style display class (HIGH-06).
5. String interpolation lowered to mutable builder pattern instead of `InterpolatedStringHandler` (HIGH-24).
6. Missing `IComparable<T>` constraint for `Sort` and friends (HIGH-19).
7. C# generic variance "static_cast at call site" — undefined behavior in C++ (MEDIUM-18).
8. `volatile` keyword mapped naively to C++ `volatile` (HIGH-12).
9. `try / finally` (no catch) lowering missing (HIGH-21).
10. `where T : new()` on `XObject.New<T>` defeats locked commitment 3 (CRIT-05 / HIGH-16).

These 10 structural mappings should be fixed in Rev 2 before any implementation begins; if they're not corrected, the implementation will be wrong from day one.

---

*End of Audit B (C# 12 Language Coverage + IL/AST → C++ Mapping Correctness).
Auditor B is read-only; this document does not modify XIL2CPP.html.
62 findings produced; ready for Rev 2 ingestion.*
