// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Mangling;

/// <summary>
/// One row of the per-module <see cref="ManglingTable"/>: the join of a
/// function's deterministic <see cref="Tiering.StableId"/> (the human key,
/// shared with the Pass-4 <see cref="Tiering.TierTable"/>) to its
/// contract-versioned mangled forms (the ABI key), per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (Pass 5) +
/// <c>/Documents/XToolchainContract.html</c> Section 2.2 / 2.3.
/// </summary>
/// <remarks>
/// <para>
/// The classification flags (<see cref="IsStaticMethod"/> /
/// <see cref="IsConstructor"/> / etc.) describe the symbol the row was built
/// from so the downstream emit pass (Pass 6) can decide which C++ shape to
/// emit (a constructor body, a property accessor, an operator, a static free
/// function) without re-querying the Roslyn symbol -- the
/// <see cref="ManglingTable"/> is serializable to a JSON sidecar and the
/// symbol is not available after the compilation is disposed.
/// </para>
/// </remarks>
/// <param name="Id">The deterministic per-function stable id (the table's sort key + the join key to the tier table).</param>
/// <param name="CanonicalForm">The conceptual mangled name per Contract Section 2.2.</param>
/// <param name="LinkerSymbol">The linker-visible mangled symbol per Contract Section 2.3.</param>
/// <param name="IsStaticMethod">True iff the symbol is a static method (no implicit <c>self</c> first parameter).</param>
/// <param name="IsConstructor">True iff the symbol is an instance constructor (<c>$ctor</c>).</param>
/// <param name="IsDestructor">True iff the symbol is a finalizer / destructor.</param>
/// <param name="IsPropertyGetter">True iff the symbol is a property / indexer get accessor.</param>
/// <param name="IsPropertySetter">True iff the symbol is a property / indexer set / init accessor.</param>
/// <param name="IsOperator">True iff the symbol is a user-defined operator or conversion operator (<c>op_*</c>).</param>
public readonly record struct ManglingRecord(
    StableId Id,
    string CanonicalForm,
    string LinkerSymbol,
    bool IsStaticMethod,
    bool IsConstructor,
    bool IsDestructor,
    bool IsPropertyGetter,
    bool IsPropertySetter,
    bool IsOperator);
