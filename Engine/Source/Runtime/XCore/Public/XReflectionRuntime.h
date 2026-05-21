// Copyright Simgenics. All Rights Reserved.

#pragma once

// =====================================================================
// XReflectionRuntime.h -- STAGE-A stub for XHT-emitted code compilation.
// =====================================================================
//
// This header is the minimum-viable XCore surface that XHT-generated
// .gen.h / .gen.cpp files compile against. It exists so:
//
//   1. The XHT C5 audit fix (ship a compilable .gen.cpp) is honoured
//      without waiting for XCore-4a / XCore-4b.
//   2. Downstream module authors writing reflected classes can include
//      this stub and get a working build now; the eventual XCore-4a
//      replacement is a drop-in upgrade.
//   3. XHT's §21.2 compile-test fixture (XHT.Tests' GenCppCompiles)
//      has something to point a real compiler at.
//
// Full implementation lands at:
//   * XCore-4a (Step 3): XReflectionRuntime, XClass, XStruct, XEnum,
//     XInterface, descriptor structs, RegisterType(), GetXClassFromConstInit().
//   * XCore-4b (Step 4): final ConstInit byte layout (XCONSTINIT, opaque
//     descriptors, runtime fixup mechanism).
//
// Per the master plan §4 schedule and Contract Section 7.1 ConstInit
// posture. This stub MUST NOT acquire features beyond the minimum
// surface XHT-emit needs to compile -- it is intentionally bare.
//
// =====================================================================

#include <cstdint>

// ---------------------------------------------------------------------
// XCONSTINIT: the constinit-equivalent attribute the .gen.cpp uses.
// In C++20 mode, constinit ensures static-initialization happens at
// compile time. In older modes we degrade to a no-op so the code still
// compiles. The XCore-4b ConstInit posture relies on this attribute
// to guarantee the descriptor data is rodata-allocated.
// ---------------------------------------------------------------------

#if defined(__cpp_constinit) && __cpp_constinit >= 201907L
    #define XCONSTINIT constinit
#elif __cplusplus >= 202002L
    #define XCONSTINIT constinit
#else
    #define XCONSTINIT
#endif

// ---------------------------------------------------------------------
// XPACT_WITH_CONSTINIT_XOBJECT: the static_assert pin XHT emits at
// .gen.cpp scope (Section 8.2 step 3). Set to 1 in the stub so the
// generated TUs static-assert true. XCore-4b will redefine this based
// on the active object format at full build.
// ---------------------------------------------------------------------

#ifndef XPACT_WITH_CONSTINIT_XOBJECT
    #define XPACT_WITH_CONSTINIT_XOBJECT 1
#endif

// ---------------------------------------------------------------------
// Forward-declared opaque types. XHT-emitted code holds pointers to
// these; runtime functions in XCore-4b dereference them.
// ---------------------------------------------------------------------

// The Section-10 emit symbols are flat (no namespace) so XHT-generated
// extern "C" declarations like `const struct XClass* ...` resolve as
// global-scope types. We define the opaque tag types AT global scope
// here so the `struct XClass` spelling that XHT-emit uses is
// well-formed. XCore-4a will move these into a namespace and re-alias.
struct XClass {};
struct XStruct {};
struct XEnum {};
struct XInterface {};
struct XDelegateFunction {};

// ---------------------------------------------------------------------
// Descriptor structs. These are POD records the XHT-generated .gen.cpp
// populates per type. XCore-4b will replace this surface; the fields
// here are the minimum needed for the Stage-A descriptor emit
// (Section 8.2). Layout is documented as "provisional" -- DO NOT
// persist these structs to disk.
// ---------------------------------------------------------------------

struct XPropertyDescriptor
{
    const char* Name;
    const char* TypeName;
    uint32_t Offset;
    uint32_t Size;
    uint32_t Flags;
};

struct XFunctionDescriptor
{
    const char* Name;
    const char* ReturnTypeName;
    uint32_t NumParameters;
    const char** ParameterNames;
    const char** ParameterTypeNames;
    uint32_t Flags;
};

struct XEnumValueDescriptor
{
    const char* Name;
    int64_t Value;
};

struct XClassDescriptor
{
    const char* Name;
    const char* SuperName;
    const XPropertyDescriptor* Properties;
    uint32_t NumProperties;
    const XFunctionDescriptor* Functions;
    uint32_t NumFunctions;
    uint32_t Flags;
};

struct XStructDescriptor
{
    const char* Name;
    const char* SuperName;
    const XPropertyDescriptor* Properties;
    uint32_t NumProperties;
    uint32_t Flags;
};

struct XEnumDescriptor
{
    const char* Name;
    const XEnumValueDescriptor* Values;
    uint32_t NumValues;
    uint32_t Flags;
};

struct XInterfaceDescriptor
{
    const char* Name;
    const char* SuperName;
    const XFunctionDescriptor* Functions;
    uint32_t NumFunctions;
    uint32_t Flags;
};

struct XDelegateDescriptor
{
    const char* Name;
    const char* ReturnTypeName;
    uint32_t NumParameters;
    const char** ParameterTypeNames;
    bool IsMulticast;
    uint32_t Flags;
};

// XDelegateFunctionDescriptor: the descriptor XHT emits for XDelegate-
// reflected types (RoleToken == "XDelegateFunction", per Section 10.2).
// Same shape as XClassDescriptor so the .Name / .SuperName / .Properties
// / .NumProperties / .Functions / .NumFunctions field block the
// SourceEmitter emits compiles uniformly. STAGE-B will replace this with
// a delegate-shaped descriptor; until then, the unused fields default
// to nullptr / 0 in the generated initializer.
struct XDelegateFunctionDescriptor
{
    const char* Name;
    const char* SuperName;
    const XPropertyDescriptor* Properties;
    uint32_t NumProperties;
    const XFunctionDescriptor* Functions;
    uint32_t NumFunctions;
    uint32_t Flags;
};

// ---------------------------------------------------------------------
// XReflectionRuntime: the runtime registry XHT-emit calls into at DLL
// load. STAGE-A: every entry is a no-op stub that returns nullptr or
// performs no work. XCore-4a will provide the actual registry.
// ---------------------------------------------------------------------

class XReflectionRuntime
{
public:
    // Construct-from-ConstInit getters. Each returns a pointer to a
    // runtime XClass / XStruct / XEnum / XInterface / XDelegateFunction
    // representing the descriptor; STAGE-A returns nullptr so the call
    // compiles and links.
    static const XClass*             GetXClassFromConstInit(const XClassDescriptor* desc);
    static const XStruct*            GetXStructFromConstInit(const XStructDescriptor* desc);
    static const XEnum*              GetXEnumFromConstInit(const XEnumDescriptor* desc);
    static const XInterface*         GetXInterfaceFromConstInit(const XInterfaceDescriptor* desc);
    static const XDelegateFunction*  GetXDelegateFunctionFromConstInit(const XDelegateFunctionDescriptor* desc);

    // RegisterType: per-DLL-load init aggregator calls these to install
    // each reflected type into the runtime registry. STAGE-A: no-op.
    static void RegisterType(const XClass* xclass);
    static void RegisterType(const XStruct* xstruct);
    static void RegisterType(const XEnum* xenum);
    static void RegisterType(const XInterface* xinterface);
    static void RegisterType(const XDelegateFunction* xdelegate);
};

// ---------------------------------------------------------------------
// STAGE-A inline definitions. All functions inlined into the header so
// the stub doesn't require linking against an XCore.lib. XCore-4a will
// move these out-of-line into the actual implementation.
// ---------------------------------------------------------------------

inline const XClass* XReflectionRuntime::GetXClassFromConstInit(const XClassDescriptor* /*desc*/)
{
    return nullptr;
}

inline const XStruct* XReflectionRuntime::GetXStructFromConstInit(const XStructDescriptor* /*desc*/)
{
    return nullptr;
}

inline const XEnum* XReflectionRuntime::GetXEnumFromConstInit(const XEnumDescriptor* /*desc*/)
{
    return nullptr;
}

inline const XInterface* XReflectionRuntime::GetXInterfaceFromConstInit(const XInterfaceDescriptor* /*desc*/)
{
    return nullptr;
}

inline const XDelegateFunction* XReflectionRuntime::GetXDelegateFunctionFromConstInit(const XDelegateFunctionDescriptor* /*desc*/)
{
    return nullptr;
}

inline void XReflectionRuntime::RegisterType(const XClass* /*xclass*/) {}
inline void XReflectionRuntime::RegisterType(const XStruct* /*xstruct*/) {}
inline void XReflectionRuntime::RegisterType(const XEnum* /*xenum*/) {}
inline void XReflectionRuntime::RegisterType(const XInterface* /*xinterface*/) {}
inline void XReflectionRuntime::RegisterType(const XDelegateFunction* /*xdelegate*/) {}
