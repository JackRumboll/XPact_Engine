// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCSharpString.h -- IL2CPP transpilation handle facade.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1.5.
//
// XCSharpString is structurally inseparable from FString (its only
// data member is `const FString* m_storage`), so the type itself is
// defined alongside FString in Containers/FString.h. This header
// re-includes FString.h so downstream code that wants to import
// "the XCSharpString surface" by name has a discoverable path.
//
// PER CONTRACT REV 13.7 SECTION 6.1:
//
//   struct XCSharpString {
//       const FString* m_storage;    // points at an interned FString
//                                    // in .rodata OR at a transient
//                                    // FString cached for the call.
//   };
//
//   static_assert(sizeof(XCSharpString)  == sizeof(void*));
//   static_assert(alignof(XCSharpString) == alignof(void*));
//
// USAGE BY XIL2CPP:
//
//   * C# string literals compile to constinit const FString objects
//     placed in the DLL's .rodata. XIL2CPP emits them from the IL
//     string-literal table at transpile time.
//
//   * The build is a hard error if two distinct C# literals would
//     resolve to the same .rodata address (which would break
//     ReferenceEquals).
//
//   * The SSO/heap discriminator of an interned literal is stable
//     across hot-reload sessions; .rodata addresses do not change
//     within a given DLL revision.
//
//   * ReferenceEquals(nameof(X), "X") is a build-time pointer-
//     identity guarantee from XIL2CPP; the transpiler resolves both
//     sides to the same .rodata FString and emits a single pointer
//     compare.
//
//   * XCSharpString never appears in user code directly; it is the
//     IL2CPP transpilation target for C# string. C++ code that wants
//     to interact with a C# string sees a const FString& (the
//     storage pointed to by XCSharpString::m_storage) and treats it
//     as an immutable view. Mutation of an interned literal is
//     undefined behaviour (the storage is const in .rodata).
//
// =====================================================================

#include "Containers/FString.h"

// Re-export XCSharpString in the canonical XCore namespace.
// The type itself is defined in FString.h; this header is the
// spec-named import location.
