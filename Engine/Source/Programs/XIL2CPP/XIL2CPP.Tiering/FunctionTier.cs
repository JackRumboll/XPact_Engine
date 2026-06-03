// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Tiering;

/// <summary>
/// The calling-convention tier XIL2CPP Pass 4 assigns to an emittable
/// function per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.1 / 3.2
/// (Pass 4) and <c>/Documents/XIL2CPP-Constraints.md</c> Section 2.7 / 2.8.
/// </summary>
public enum FunctionTier
{
    /// <summary>
    /// Tier 1: the ABI-stable <c>extern "C"</c> <c>XResult</c>-shimmed form
    /// (<c>void _Foo_Shim(void* self, ..., XResult* outResult)</c>). A
    /// function is Tier 1 when it can throw or crosses the module DLL boundary
    /// -- it is exported, it is <c>[XFunction(CanThrow = true)]</c> /
    /// <c>[CanThrow]</c>, its body throws, a callee is Tier 1, or it makes an
    /// unsafe / extern C++ call XIL2CPP cannot prove <c>noexcept</c>. Tier 1
    /// is the conservative default: every function that is not provably Tier 2
    /// is Tier 1.
    /// </summary>
    Tier1,

    /// <summary>
    /// Tier 2: the direct call (a plain C++ free function with the C#
    /// method's natural signature, no <c>XResult</c> out-param, zero overhead
    /// vs. handwritten C++). A function is Tier 2 IFF ALL the Tier-2 clauses
    /// hold (Constraint Section 2.8): not exported, not
    /// <c>[XFunction(CanThrow = true)]</c>, every callee Tier 2, body has no
    /// <c>throw</c>, and no unsafe C++ call without a <c>noexcept</c> proof.
    /// </summary>
    Tier2,
}
