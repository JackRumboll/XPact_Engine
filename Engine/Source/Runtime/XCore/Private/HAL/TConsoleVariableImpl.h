// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TConsoleVariableImpl.h -- concrete IConsoleVariable implementations.
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 (Public API) + Section 9.2 (Threading)
// + Section 9.5 (Lifetime / fix B-C3 typed handle).
//
// This header lives in Private/HAL because the concrete types are
// internal to the CVar subsystem; user code consumes the abstract
// IConsoleVariable interface and the FAutoConsoleVariable<T> /
// TConsoleVariableHandle<T> typed surfaces.
//
// THREE CONCRETE TYPES:
//   * TConsoleVariable<int32>  -- backed by TConsoleVariableData<int32>
//   * TConsoleVariable<float>  -- backed by TConsoleVariableData<float>
//   * TConsoleVariable<FString> -- backed by an FString + an FRWLock
//                                  (FString is non-trivially-copyable
//                                  so std::atomic_ref doesn't apply;
//                                  the string CVar takes the per-CVar
//                                  read lock on GetString and the
//                                  exclusive lock on SetString).
//
// CASCADE ARBITRATION:
//   Each setter (SetInt/SetFloat/SetString) acquires the per-CVar
//   FRWLock exclusively, checks the current SetByPriority against
//   the incoming Priority, and either ignores the call or stores
//   the new value + updates the SetByPriority.
//
//   The cascade rule: a SetByPriority cannot overwrite a value
//   stored with a higher priority. Ties resolve by recency (same-
//   priority later setter wins; the implementation always stores
//   when Priority >= current).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Containers/FString.h"
#include "HAL/ECVarFlags.h"
#include "HAL/ECVarSetByPriority.h"
#include "HAL/FRWLock.h"
#include "HAL/IConsoleVariable.h"
#include "HAL/TConsoleVariableData.h"

namespace XCore::Misc
{

    // =================================================================
    // TConsoleVariable<int32> -- the integer concrete type.
    // =================================================================
    class TConsoleVariableInt32 final : public IConsoleVariable
    {
    public:
        TConsoleVariableInt32(const ::XCore::FString& InName,
                              ::int32 Default,
                              const ::XCore::FString& InHelp,
                              ECVarFlags InFlags) noexcept;

        ~TConsoleVariableInt32() noexcept override = default;

        // --- IConsoleVariable overrides ---

        [[nodiscard]] ::int32 GetInt() const noexcept override
        {
            return m_data.LoadAcquire();
        }

        [[nodiscard]] float GetFloat() const noexcept override
        {
            return static_cast<float>(m_data.LoadAcquire());
        }

        [[nodiscard]] ::XCore::FString GetString() const noexcept override;

        void SetInt(::int32 Value, ECVarSetByPriority Priority) noexcept override;

        void SetFloat(float Value, ECVarSetByPriority Priority) noexcept override
        {
            // Float -> int32 projection: truncate.
            SetInt(static_cast<::int32>(Value), Priority);
        }

        void SetString(const ::XCore::FString& Value, ECVarSetByPriority Priority) noexcept override;

        [[nodiscard]] const ::XCore::FString& GetName() const noexcept override
        {
            return m_name;
        }

        [[nodiscard]] const ::XCore::FString& GetHelp() const noexcept override
        {
            return m_help;
        }

        [[nodiscard]] ECVarFlags GetFlags() const noexcept override
        {
            return m_flags;
        }

        // --- Internal accessors for FAutoConsoleVariable / handle ---

        // GetValueCellPtr -- exposes the value cell address for the
        // cached-handle published-pointer protocol (Section 9.5
        // fix B-C3).
        //
        // Engine-internal accessor; user code MUST NOT call. The
        // FAutoConsoleVariable<int32>::GetHandle() implementation
        // uses this to construct the TConsoleVariableHandle.
        [[nodiscard]] ::int32* __GetValueCellPtr() noexcept
        {
            return m_data.GetValuePtr();
        }

        [[nodiscard]] ECVarSetByPriority GetLastSetBy() const noexcept
        {
            return m_data.GetSetBy();
        }

    private:
        ::XCore::FString m_name;
        ::XCore::FString m_help;
        ECVarFlags       m_flags;
        TConsoleVariableData<::int32> m_data;

        // Per-CVar lock for the cascade arbiter. Shared with reads of
        // the SetByPriority field (read path); exclusive on Set*.
        mutable ::XCore::HAL::FRWLock m_lock;
    };

    // =================================================================
    // TConsoleVariable<float> -- the float concrete type.
    // =================================================================
    class TConsoleVariableFloat final : public IConsoleVariable
    {
    public:
        TConsoleVariableFloat(const ::XCore::FString& InName,
                              float Default,
                              const ::XCore::FString& InHelp,
                              ECVarFlags InFlags) noexcept;

        ~TConsoleVariableFloat() noexcept override = default;

        [[nodiscard]] ::int32 GetInt() const noexcept override
        {
            return static_cast<::int32>(m_data.LoadAcquire());
        }

        [[nodiscard]] float GetFloat() const noexcept override
        {
            return m_data.LoadAcquire();
        }

        [[nodiscard]] ::XCore::FString GetString() const noexcept override;

        void SetInt(::int32 Value, ECVarSetByPriority Priority) noexcept override
        {
            SetFloat(static_cast<float>(Value), Priority);
        }

        void SetFloat(float Value, ECVarSetByPriority Priority) noexcept override;

        void SetString(const ::XCore::FString& Value, ECVarSetByPriority Priority) noexcept override;

        [[nodiscard]] const ::XCore::FString& GetName() const noexcept override
        {
            return m_name;
        }

        [[nodiscard]] const ::XCore::FString& GetHelp() const noexcept override
        {
            return m_help;
        }

        [[nodiscard]] ECVarFlags GetFlags() const noexcept override
        {
            return m_flags;
        }

        [[nodiscard]] float* __GetValueCellPtr() noexcept
        {
            return m_data.GetValuePtr();
        }

        [[nodiscard]] ECVarSetByPriority GetLastSetBy() const noexcept
        {
            return m_data.GetSetBy();
        }

    private:
        ::XCore::FString m_name;
        ::XCore::FString m_help;
        ECVarFlags       m_flags;
        TConsoleVariableData<float> m_data;

        mutable ::XCore::HAL::FRWLock m_lock;
    };

    // =================================================================
    // TConsoleVariable<FString> -- the string concrete type.
    //
    // Non-trivially-copyable storage; can't use TConsoleVariableData
    // (which gates on std::is_trivially_copyable_v<T>). Holds the
    // FString directly behind the per-CVar RWLock; GetString takes
    // the shared lock and returns a deep copy.
    //
    // No cached handle -- hot-path FString reads go through the
    // virtual.
    // =================================================================
    class TConsoleVariableString final : public IConsoleVariable
    {
    public:
        TConsoleVariableString(const ::XCore::FString& InName,
                               const ::XCore::FString& Default,
                               const ::XCore::FString& InHelp,
                               ECVarFlags InFlags) noexcept;

        ~TConsoleVariableString() noexcept override = default;

        // Numeric projections of an FString CVar parse the current
        // string via FString::ToInt32 / ToFloat. Parse errors map
        // to 0 / 0.0f (the registered-default-projection contract).
        [[nodiscard]] ::int32 GetInt() const noexcept override;

        [[nodiscard]] float GetFloat() const noexcept override;

        [[nodiscard]] ::XCore::FString GetString() const noexcept override;

        void SetInt(::int32 Value, ECVarSetByPriority Priority) noexcept override;
        void SetFloat(float Value, ECVarSetByPriority Priority) noexcept override;
        void SetString(const ::XCore::FString& Value, ECVarSetByPriority Priority) noexcept override;

        [[nodiscard]] const ::XCore::FString& GetName() const noexcept override
        {
            return m_name;
        }

        [[nodiscard]] const ::XCore::FString& GetHelp() const noexcept override
        {
            return m_help;
        }

        [[nodiscard]] ECVarFlags GetFlags() const noexcept override
        {
            return m_flags;
        }

        [[nodiscard]] ECVarSetByPriority GetLastSetBy() const noexcept
        {
            ::XCore::HAL::FScopedReadLock L(m_lock);
            return m_setBy;
        }

    private:
        ::XCore::FString m_name;
        ::XCore::FString m_help;
        ECVarFlags       m_flags;
        ::XCore::FString m_value;
        ECVarSetByPriority m_setBy;
        mutable ::XCore::HAL::FRWLock m_lock;
    };

} // namespace XCore::Misc
