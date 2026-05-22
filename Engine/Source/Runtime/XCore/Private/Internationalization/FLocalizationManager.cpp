// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FLocalizationManager.cpp -- locale manager body (Section 11.2).
// =====================================================================
//
// XCore-4a Rev 3 Section 11.2 implementation. Implements the
// `(namespace, key) -> localised FString` table, the active-locale
// state, the locale-generation counter for FText cache invalidation,
// and the FRWLock-protected thread-safety contract.
//
// TABLE KEYING (Section 11.9 hash contract):
//   The map key is XXH3_64 of the bytes "namespace::key" (with the
//   two-colon separator literally in the hashed bytes). The
//   separator is part of the keyed string so that
//     ns="A", key="B::C"
//   and
//     ns="A::B", key="C"
//   hash to distinct slots. The separator "::" is in the rodata.
//
//   The map is TMap<uint64_t, FString> -- key is the hash, value is
//   the localised string. Collisions on uint64_t XXH3 are astronomically
//   unlikely for typical loctable sizes (< 100k entries; birthday
//   collision probability < 2^-44). We do NOT store the original
//   namespace+key bytes inside the map; this is a deliberate space
//   trade.
//
// LOCK CONTRACT (Section 11.3):
//   FRWLock-protected. Lookup uses shared lock (multiple concurrent
//   readers). SetLocale / LoadLocalizationTable / UnloadAll use
//   exclusive lock. The generation counter is std::atomic<uint32_t>
//   so it can be read without taking the lock at all -- the lock-free
//   read is the fast path for FText::ResolveForCurrentLocale's cache-
//   check.
//
// =====================================================================

#include "Internationalization/FLocalizationManager.h"
#include "Internationalization/FLocTableLoader.h"
#include "HAL/FRWLock.h"
#include "HAL/FPlatformProcess.h"
#include "HAL/XInitPhase.h"
#include "Containers/TMap.h"
#include "Hash/FXxh3.h"

#include <atomic>
#include <cstdio>
#include <cstring>

namespace XCore::Loc
{

namespace Detail
{
    // -----------------------------------------------------------------
    // Internal state.
    //
    // The locale-generation counter is module-scope std::atomic so
    // FText::ResolveForCurrentLocale's cache-check can read it without
    // taking any lock. The 0-init (PreStaticInit) value is the
    // never-resolved sentinel; __Initialize bumps to 1 at the
    // PostStaticInit transition.
    //
    // The FRWLock and TMap state live in the Meyers-singleton
    // ManagerState below. The Meyers form guarantees the constructor
    // runs on first Get() (which happens at __Initialize), avoiding
    // the static-init-order hazard between this TU's
    // module-scope FRWLock and the allocator's PreStaticInit body.
    //
    // The atomic generation counter is constinit-zero so reading it
    // from a constinit constructor (PreStaticInit) is well-defined
    // (returns 0, which the Lookup function treats as "never
    // initialised; return nullptr"). The atomic itself uses lock-free
    // 32-bit operations on every supported XPact target.
    // -----------------------------------------------------------------
    static ::std::atomic<::std::uint32_t> g_LocaleGeneration{0u};

    // -----------------------------------------------------------------
    // ComputeHash -- XXH3_64 of "namespace::key".
    //
    // Builds the keyed-bytes input on a small stack buffer (avoids
    // heap allocation on every lookup). Falls back to FString
    // concatenation only when the combined namespace+key exceeds the
    // stack buffer (which is rare; most loctable keys are < 64 bytes
    // total).
    // -----------------------------------------------------------------
    [[nodiscard]] static ::std::uint64_t ComputeHash(const char* Namespace, const char* Key) noexcept
    {
        if (Namespace == nullptr) Namespace = "";
        if (Key == nullptr) Key = "";

        const ::SIZE_T NsLen  = std::strlen(Namespace);
        const ::SIZE_T KeyLen = std::strlen(Key);
        const ::SIZE_T Total  = NsLen + 2 + KeyLen;

        // Small-input fast path: assemble on the stack.
        constexpr ::SIZE_T kStackBufBytes = 256;
        if (Total <= kStackBufBytes)
        {
            char Buf[kStackBufBytes];
            std::memcpy(Buf, Namespace, NsLen);
            Buf[NsLen]     = ':';
            Buf[NsLen + 1] = ':';
            std::memcpy(Buf + NsLen + 2, Key, KeyLen);
            return ::XCore::Hash::FXxh3::Hash64(Buf, Total, /*Seed=*/0u);
        }

        // Long-input slow path: heap-allocated buffer via FMemory.
        void* Heap = ::XCore::HAL::FMemory::MallocOrAbort(
            Total,
            /*Align=*/1,
            ::XCore::HAL::FMemTag::Localization);
        char* HeapBuf = static_cast<char*>(Heap);
        std::memcpy(HeapBuf, Namespace, NsLen);
        HeapBuf[NsLen]     = ':';
        HeapBuf[NsLen + 1] = ':';
        std::memcpy(HeapBuf + NsLen + 2, Key, KeyLen);
        const ::std::uint64_t H = ::XCore::Hash::FXxh3::Hash64(HeapBuf, Total, /*Seed=*/0u);
        ::XCore::HAL::FMemory::Free(Heap);
        return H;
    }

    // -----------------------------------------------------------------
    // State -- the lazily-constructed manager body.
    //
    // Encapsulated in a Meyers singleton wrapped by GetState() so the
    // FRWLock, FString, and TMap members all construct lazily on
    // first use. Meyers semantics give us thread-safe one-shot
    // construction (C++11 [stmt.dcl]/4 "magic statics") and a clean
    // ordering w.r.t. the program-exit destructor cascade.
    // -----------------------------------------------------------------
    struct ManagerState
    {
        ::XCore::HAL::FRWLock Lock;
        ::XCore::FString CurrentLocale;
        ::XCore::TMap<::std::uint64_t, ::XCore::FString> Table;

        ManagerState() noexcept
            : Lock()
            , CurrentLocale("en-US")
            , Table()
        {
        }
    };

    [[nodiscard]] static ManagerState& GetState() noexcept
    {
        // Function-local static -- Meyers singleton. Construction is
        // thread-safe per C++11 [stmt.dcl]/4 (the dynamic-init "magic
        // statics" guarantee). The first invocation triggers the
        // constructor; subsequent invocations return the same instance.
        static ManagerState s_State;
        return s_State;
    }
} // namespace Detail

// ---------------------------------------------------------------------
// FLocalizationManager surface body.
// ---------------------------------------------------------------------

FLocalizationManager::FLocalizationManager() noexcept = default;
FLocalizationManager::~FLocalizationManager() noexcept = default;

FLocalizationManager& FLocalizationManager::Get() noexcept
{
    static FLocalizationManager s_Instance;
    // Touch the state singleton so its constructor runs at the same
    // initialisation point as the FLocalizationManager singleton.
    (void)Detail::GetState();
    return s_Instance;
}

void FLocalizationManager::__Initialize() noexcept
{
    // Spec wording (Section 1.5 invariant): __Initialize runs at
    // PostStaticInit. The phase check below catches a bootstrap bug
    // (someone called __Initialize before the allocator was up).
    XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= ::XCore::HAL::EInitPhase::PostStaticInit);

    // Idempotent: if the generation is already non-zero we've been
    // initialised; second call is a no-op.
    if (Detail::g_LocaleGeneration.load(::std::memory_order_acquire) != 0u)
    {
        return;
    }

    // Touch the state singleton so its constructor runs now (rather
    // than on the first Lookup call from a possibly-racing thread).
    Detail::ManagerState& State = Detail::GetState();
    (void)State;

    // Bump the generation to 1: FText cache invalidation signal is
    // "current generation != cached generation", and the
    // kUnresolvedGeneration sentinel is 0, so we step to 1 here.
    Detail::g_LocaleGeneration.store(1u, ::std::memory_order_release);
}

void FLocalizationManager::SetLocale(const ::XCore::FString& LocaleCode) noexcept
{
    Detail::ManagerState& State = Detail::GetState();

    // Exclusive lock for the locale switch.
    State.Lock.LockExclusive();

    // No-op if the locale is already active. The byte-equality check
    // uses FString::operator== (sim-path-safe per Section 11.1.3).
    if (State.CurrentLocale == LocaleCode)
    {
        State.Lock.UnlockExclusive();
        return;
    }

    // Switch locale.
    State.CurrentLocale = LocaleCode;

    // Drop the in-memory table -- the next ResolveForCurrentLocale
    // call that misses will re-populate on demand. NOTE: we do NOT
    // auto-load LocaleCode.loctable here; LoadLocalizationTable is a
    // separate explicit call. This matches the spec wording "SetLocale
    // flushes the per-FText resolved cache" (Section 11.2) -- the
    // load is the caller's responsibility.
    State.Table.Reset();

    // Bump the locale generation counter. The wrap at UINT32_MAX is
    // benign (see FText.h kUnresolvedGeneration comment).
    ::std::uint32_t Prev = Detail::g_LocaleGeneration.load(::std::memory_order_acquire);
    ::std::uint32_t Next = (Prev == 0u) ? 1u : (Prev + 1u);
    if (Next == 0u) Next = 1u; // skip the kUnresolvedGeneration value on wrap
    Detail::g_LocaleGeneration.store(Next, ::std::memory_order_release);

    State.Lock.UnlockExclusive();
}

const ::XCore::FString& FLocalizationManager::GetCurrentLocale() noexcept
{
    Detail::ManagerState& State = Detail::GetState();
    // Shared lock for the read. The FString return is by reference --
    // safe because the caller is expected to take the lock-equivalent
    // discipline (no mid-frame SetLocale per spec).
    State.Lock.LockShared();
    const ::XCore::FString& Ref = State.CurrentLocale;
    State.Lock.UnlockShared();
    return Ref;
}

::std::uint32_t FLocalizationManager::GetCurrentGeneration() noexcept
{
    return Detail::g_LocaleGeneration.load(::std::memory_order_acquire);
}

const ::XCore::FString* FLocalizationManager::Lookup(const char* Namespace, const char* Key) noexcept
{
    // Empty input: no lookup, return nullptr (caller falls back).
    if (Namespace == nullptr || Key == nullptr)
    {
        return nullptr;
    }

    // Invariant-text sentinel: short-circuit. The INVTEXT macro emits
    // this namespace; the caller's literal IS the value.
    if (Namespace[0] == '_' && Namespace[1] == '_' &&
        Namespace[2] == 'I' && Namespace[3] == 'n' &&
        Namespace[4] == 'v' && Namespace[5] == 'a' &&
        Namespace[6] == 'r' && Namespace[7] == 'i' &&
        Namespace[8] == 'a' && Namespace[9] == 'n' &&
        Namespace[10] == 't' && Namespace[11] == '\0')
    {
        return nullptr;
    }

    // Pre-init phase: table not populated yet (generation == 0).
    // Section 17.8 H1: "LOCTEXT resolves to source-fallback (en-US)
    // when no loctable is loaded" -- the fallback is the caller's
    // responsibility, so we return nullptr.
    if (Detail::g_LocaleGeneration.load(::std::memory_order_acquire) == 0u)
    {
        return nullptr;
    }

    const ::std::uint64_t Hash = Detail::ComputeHash(Namespace, Key);

    // Shared lock for the read.
    Detail::ManagerState& State = Detail::GetState();
    State.Lock.LockShared();
    const ::XCore::FString* Hit = State.Table.Find(Hash);
    State.Lock.UnlockShared();

    return Hit;
}

void FLocalizationManager::LoadLocalizationTable(const ::XCore::FString& LocaleCode)
{
    // Mid-frame check: spec requires LoadLocalizationTable not be
    // called mid-frame. The full frame-boundary check requires the
    // renderer's frame-cookie which is not yet shipped; for Phase 1f
    // we check only the phase ladder (PostStaticInit or later) as the
    // available approximation. The full check lands when the
    // frame-boundary signal does.
    XPACT_CHECK(::XCore::HAL::EngineInitPhase() >= ::XCore::HAL::EInitPhase::PostStaticInit);

    // Compose the path: {Engine}/Content/Localization/<LocaleCode>.loctable
    //
    // The path walk from the executable directory:
    //   Engine/Binaries/<Platform>/<ExeName>(.exe)
    //   ../../..                            -> Engine root parent
    //   ./Content/Localization/<LocaleCode>.loctable
    //
    // We construct the path via FString manipulation. The executable
    // path is UTF-8 (FPlatformProcess::GetExecutablePath returns FString).
    ::XCore::FString ExePath = ::XCore::HAL::FPlatformProcess::GetExecutablePath();

    // Find the last three path separators by scanning backward. The
    // engine's path layout is fixed (Engine/Binaries/<Platform>/<Exe>)
    // so we can rely on three '/' or '\\' separators above the engine
    // root. We strip components by finding separator indices.
    //
    // We use IndexOfByte / LastIndexOfByte from FString (sim-path-
    // safe; pure byte search).
    ::XCore::FString Path = ExePath;

    // Strip three trailing components: Engine/Binaries/<Platform>/<ExeName>.
    //
    // Path manipulation is byte-precise (not codepoint-precise): a
    // user-folder name with non-ASCII characters (e.g., a Japanese or
    // German developer's home directory) must not break the path
    // walk. We construct intermediate FString objects from raw byte
    // slices via FString(const char*, int32 ByteLen).
    auto StripLastComponent = [](::XCore::FString& InPath) -> bool {
        const ::int32 LenBytes = InPath.LenBytes();
        // Find the last separator. The engine supports both '/' and '\\'
        // depending on platform.
        //
        // Backward scan: byte-level (a path separator is always ASCII
        // 0x2F or 0x5C; valid UTF-8 multi-byte sequences never contain
        // a 0x2F / 0x5C continuation byte by spec, so the byte scan is
        // codepoint-safe).
        ::int32 LastSep = -1;
        for (::int32 I = LenBytes - 1; I >= 0; --I)
        {
            const char C = InPath.ByteAt(I);
            if (C == '/' || C == '\\')
            {
                LastSep = I;
                break;
            }
        }
        if (LastSep < 0)
        {
            return false;
        }
        // Construct a fresh FString from the byte prefix [0, LastSep).
        InPath = ::XCore::FString(InPath.ToUtf8Ptr(), LastSep);
        return true;
    };

    // Strip <ExeName>, <Platform>, "Binaries":
    bool OkStrip = StripLastComponent(Path) &&
                   StripLastComponent(Path) &&
                   StripLastComponent(Path);
    if (!OkStrip)
    {
        std::fprintf(stderr,
            "[XCore::Loc] WARN: cannot compose loctable path: "
            "unexpected executable path layout. Locale '%s' not loaded.\n",
            LocaleCode.ToUtf8Cstr());
        return;
    }

    // Append /Content/Localization/<LocaleCode>.loctable.
    Path.Append("/Content/Localization/");
    Path.Append(LocaleCode);
    Path.Append(".loctable");

    // Load + verify + parse.
    auto LoadResult = FLocTableLoader::LoadFromFile(Path);
    if (!LoadResult.has_value())
    {
        std::fprintf(stderr,
            "[XCore::Loc] WARN: LoadFromFile failed for locale '%s' at "
            "path '%s'.\n",
            LocaleCode.ToUtf8Cstr(),
            Path.ToUtf8Cstr());
        return;
    }

    auto& Entries = LoadResult.value();

    // Insert into the in-memory table under exclusive lock.
    Detail::ManagerState& State = Detail::GetState();
    State.Lock.LockExclusive();

    for (::int32 I = 0; I < Entries.Num(); ++I)
    {
        const FLocTableLoader::FLocEntry& E = Entries[I];
        const ::std::uint64_t H = Detail::ComputeHash(
            E.Namespace.ToUtf8Cstr(),
            E.Key.ToUtf8Cstr());
        State.Table.Add(H, E.Value);
    }

    // Bump the generation -- existing FText caches need to refresh.
    ::std::uint32_t Prev = Detail::g_LocaleGeneration.load(::std::memory_order_acquire);
    ::std::uint32_t Next = (Prev == 0u) ? 1u : (Prev + 1u);
    if (Next == 0u) Next = 1u;
    Detail::g_LocaleGeneration.store(Next, ::std::memory_order_release);

    State.Lock.UnlockExclusive();
}

void FLocalizationManager::UnloadAll() noexcept
{
    Detail::ManagerState& State = Detail::GetState();
    State.Lock.LockExclusive();
    State.Table.Reset();
    State.CurrentLocale = ::XCore::FString("en-US");

    // Reset the generation to 1 (post-__Initialize value) and bump it
    // so existing FText caches refresh.
    Detail::g_LocaleGeneration.store(1u, ::std::memory_order_release);

    State.Lock.UnlockExclusive();
}

} // namespace XCore::Loc
