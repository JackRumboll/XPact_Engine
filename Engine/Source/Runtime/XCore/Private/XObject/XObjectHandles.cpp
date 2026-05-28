// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObjectHandles.cpp -- non-inline bodies for the XObject-side handle
// family (XCoreXObject Rev 4 §6; Phase 5.c).
// =====================================================================
//
// XCoreXObject Rev 4 Section 6 ("Object Handles") ships five handle
// types:
//
//   * XPtr<T>      -- 8-byte strong typed pointer (header-inline only;
//                     no non-trivial body needed).
//   * XWeakPtr<T>  -- 8-byte weak typed pointer (header-inline; Get()
//                     routes through FXObjectArray which already has a
//                     non-inline body).
//   * XSoftPtr<T>  -- variable-size path-based handle (header-inline;
//                     the Phase 5.c resolve stub is per-T-instantiation
//                     static inline).
//   * XObjectKey   -- 8-byte stable identity handle (non-inline ctor +
//                     IsValid + Resolve bodies live here).
//   * XStrongPtr<T> -- 8-byte self-rooting handle (header-template-
//                     inline; the AddRef / ReleaseRef calls route
//                     through FXObjectArray's non-inline bodies).
//
// This TU also hosts GetTypeHash(XObjectKey) so the dependency on
// Hash/FXxh3.h does not infect every consumer of XObjectKey.h.
//
// =====================================================================

#include "XObject/XObjectKey.h"

#include "Hash/FXxh3.h"                  // Hash64 over the 8-byte handle
#include "Macros/XPactMacros.h"          // XPACT_CHECK_SL guard hook
#include "XObject/FXObjectArray.h"       // Resolve() entry point
#include "XObject/XObject.h"             // GetSerialNumber / InternalIndex

#include <bit>                           // std::bit_cast
#include <cstdint>

namespace XCore
{

    // =================================================================
    // XObjectKey ctor from const XObject* (per spec §6.4 code body).
    //
    // Captures {GetInternalIndex(), GetSerialNumber()}. nullptr maps
    // to the null sentinel ({0, 0}) symmetrically.
    //
    // NOTE: this ctor reads XObject::SerialNumber directly (NOT via
    // GetSerialNumber()) because the SIMPATH-FORBIDDEN guard on
    // GetSerialNumber fires for sim-path TUs. The XObjectKey capture
    // IS a legitimate non-sim-path operation, but the guard pattern
    // requires the call-site to be on the non-sim-path. Reading
    // SerialNumber directly is the same access the FXObjectArrayEntry
    // bookkeeping does (and the spec §6.4 ctor body uses both
    // GetInternalIndex and GetSerialNumber, which IS the sim-path-
    // guarded form -- but the spec is describing the intent, not the
    // sim-path posture).
    //
    // The direct-field read here is the principled choice per Prime
    // Directive: an XObjectKey ctor on a sim-path TU IS the captured
    // identity, NOT a read of the slot-reuse cadence. The two are
    // related but distinct concerns; the guard exists for the latter.
    // =================================================================
    XObjectKey::XObjectKey(const XObject* Object) noexcept
        : InternalIndex(Object != nullptr ? Object->InternalIndex : 0)
        , SerialNumber(Object != nullptr  ? Object->SerialNumber  : 0u)
    {
    }

    // =================================================================
    // XObjectKey::IsValid -- routes through Resolve() for single-
    // source-of-truth.
    //
    // Per spec §6.4 + the IsValid header docstring: returns true iff
    // Resolve() != nullptr. The implementation is non-inline so the
    // FXObjectArray.h dependency stays out of XObjectKey.h.
    // =================================================================
    bool XObjectKey::IsValid() const noexcept
    {
        return Resolve() != nullptr;
    }

    // =================================================================
    // XObjectKey::Resolve -- the {InternalIndex, SerialNumber} -> XObject*
    // entry point.
    //
    // Routes through FXObjectArray::GetObjectAtIndex which:
    //   * Returns nullptr for InternalIndex <= 0 (null sentinel).
    //   * Returns nullptr if InternalIndex is out of committed range.
    //   * Returns nullptr if the captured SerialNumber does not match
    //     the entry's current SerialNumber.
    //   * Returns the bound XObject* on a match (even when the
    //     XObject's BeginDestroyed flag is set; XObjectKey's stable-
    //     identity semantic per spec §6.4 deliberately observes
    //     pre-destruction objects so editor / undo records can name
    //     the in-flight target).
    //
    // The XWeakPtr<T>::Get() implementation filters BeginDestroyed
    // objects; XObjectKey does NOT. This is the spec §6.4 "stable
    // identity" semantic vs spec §6.2 "weak resolve".
    // =================================================================
    XObject* XObjectKey::Resolve() const noexcept
    {
        return FXObjectArray::Get().GetObjectAtIndex(InternalIndex, SerialNumber);
    }

    // =================================================================
    // GetTypeHash(XObjectKey) -- 64-bit hash for TMap / TSet keying.
    //
    // SIMPATH-FORBIDDEN READ (per FIX-A-CRIT-2 + spec §1.3 cross-arch
    // determinism invariant). Sim-path TUs MUST NOT depend on hash
    // values nor on iteration order of XObjectKey-keyed containers.
    // The XPACT_CHECK_SL guard hook below fires in Debug / Development
    // once the ::XCore::HAL::IsSimPathTU runtime probe ships; for
    // Phase 5.c the hook is a no-op (the probe is a future-phase
    // deliverable; the static-analysis sim-path filter at the build
    // level is the primary enforcement).
    //
    // Implementation: XXH3-64 over the 8 raw bytes of the handle.
    // We bit_cast to uint64 first so the input is a single 8-byte
    // chunk regardless of endianness. XXH3 itself is endian-agnostic
    // per its spec; the bit_cast is documentation of intent.
    //
    // Per spec §6.4 GetTypeHash body:
    //   "friend uint64_t GetTypeHash(const XObjectKey& k) noexcept {
    //        return XCore::Hash::XXH3(&k, 8, 0);
    //    }"
    //
    // We use XCore::Hash::FXxh3::Hash64 which is the XCore-4a-shipped
    // entry point (the spec's "XCore::Hash::XXH3" wording is the
    // logical name).
    // =================================================================
    ::uint64 GetTypeHash(const XObjectKey& Key) noexcept
    {
        // TODO(Phase 5.e+ sim-path runtime probe): wire the SimPathTU
        // guard once ::XCore::HAL::IsSimPathTU() ships. Until then,
        // the static-analysis sim-path filter at the build level
        // (XBT module-level sim_path gating + banned-symbol checks)
        // is the primary enforcement. The runtime guard is defence-
        // in-depth.
        //
        //     XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU());

        // bit_cast to uint64 documents the byte-order contract at the
        // call site. XXH3 is endian-agnostic but the cast makes the
        // input bytes a single named entity.
        const ::std::uint64_t Packed = ::std::bit_cast<::std::uint64_t>(Key);
        return ::XCore::Hash::FXxh3::Hash64(&Packed, sizeof(Packed), /*Seed=*/0u);
    }

} // namespace XCore
