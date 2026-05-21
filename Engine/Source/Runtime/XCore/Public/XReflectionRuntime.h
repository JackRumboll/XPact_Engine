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
// xpact_compile_time_streq: constexpr string-literal equality used by
// XHT-emitted static_asserts that pin the GC root / exception ABI /
// mangling scheme tags from the manifest to the runtime defines
// (Round-2 audit C1). The function is C++17-constexpr so it works
// uniformly across gcc / clang / MSVC without relying on a builtin.
// ---------------------------------------------------------------------

namespace XPactDetail
{
    constexpr bool CompileTimeStrEq(const char* a, const char* b)
    {
        // No nullptr inputs accepted; the XHT-emit always passes string
        // literals so the constexpr path is the only path.
        if (a == nullptr || b == nullptr) { return false; }
        while (*a != '\0' && *b != '\0')
        {
            if (*a != *b) { return false; }
            ++a;
            ++b;
        }
        return *a == '\0' && *b == '\0';
    }
}

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
// XPACT_GC_ROOT_ABI_TAG / XPACT_EXCEPTION_ABI_TAG / XPACT_MANGLING_SCHEME_TAG:
// string-literal ABI identifiers XHT emits as static_assert pins at .gen.cpp
// scope (Section 8.2 + Round-2 audit C1). These match the manifest fields
// XBT writes (Contract Section 10.2). The Phase-1 defaults below MUST match:
//   - XBT.Manifest.ManifestSchema's default TargetInfo values
//   - XHT.Manifest.XbtManifestReader's expected ABI strings
// XCore-4b will redefine these based on the active runtime configuration.
// ---------------------------------------------------------------------

#ifndef XPACT_GC_ROOT_ABI_TAG
    #define XPACT_GC_ROOT_ABI_TAG "Span-based v1"
#endif

#ifndef XPACT_EXCEPTION_ABI_TAG
    #define XPACT_EXCEPTION_ABI_TAG "Tier1-Shim/Tier2-Direct"
#endif

#ifndef XPACT_MANGLING_SCHEME_TAG
    #define XPACT_MANGLING_SCHEME_TAG "Itanium-LengthPrefixed-v1"
#endif

// ---------------------------------------------------------------------
// XPACT_PROPERTY_HAS_ACCESSORS: flag indicating XPropertyDescriptor now
// carries Getter / Setter function-pointer slots per Round-2 audit
// M-XIL2CPP-Accessor. Phase 1 XHT-emit fills these as nullptr; XIL2CPP
// Phase 2 wires up real C# property getters/setters into these slots.
// The XHT-emitted .gen.cpp static_asserts the flag is 1 so a future
// runtime that drops accessor support fails the build loudly.
// ---------------------------------------------------------------------

#ifndef XPACT_PROPERTY_HAS_ACCESSORS
    #define XPACT_PROPERTY_HAS_ACCESSORS 1
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

// XPropertyDescriptor (Round-2 audit M-XIL2CPP-Accessor):
// adds Getter / Setter function-pointer slots so C# auto-properties and
// computed properties can be reflected. Phase 1 XHT-emit fills Getter /
// Setter as nullptr; direct-field properties (the C++ case + C# auto-
// property case) use Offset for member access. The XIL2CPP Phase 2 emit
// path will populate Getter / Setter with thunks into managed code so
// reflection can invoke C# property accessors uniformly.
//
// Layout is provisional per XCore-4b's eventual byte-layout freeze; do
// not persist the struct shape to disk.
struct XPropertyDescriptor
{
    const char* Name;
    const char* TypeName;
    uint32_t Offset;       // Used for direct-field access (C++ field / C# auto-property).
    uint32_t Size;
    uint32_t Flags;
    // Optional accessor slots (Round-2 audit M-XIL2CPP-Accessor). Phase 1 fills
    // nullptr; XIL2CPP Phase 2 wires up managed-side getter/setter thunks here.
    void* (*Getter)(const void* instance);
    void (*Setter)(void* instance, const void* value);
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
