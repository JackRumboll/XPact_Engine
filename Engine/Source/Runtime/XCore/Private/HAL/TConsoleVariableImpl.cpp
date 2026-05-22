// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TConsoleVariableImpl.cpp -- concrete IConsoleVariable bodies.
// =====================================================================
//
// XCore-4a Rev 3, Section 9.1 (Public API) + Section 9.2 (Threading).
//
// Implements:
//   * TConsoleVariableInt32   -- int32 concrete type.
//   * TConsoleVariableFloat   -- float concrete type.
//   * TConsoleVariableString  -- FString concrete type.
//
// Each concrete type's setter performs the SetByPriority cascade
// arbitration under the per-CVar FRWLock exclusive lock; the getter
// uses the lock-free std::atomic_ref path for the primitive types
// and the shared RWLock path for the string type.
//
// =====================================================================

#include "HAL/TConsoleVariableImpl.h"

#include "Containers/FString.h"
#include "HAL/FRWLock.h"
#include "HAL/ECVarSetByPriority.h"
#include "Macros/XPactMacros.h"

namespace XCore::Misc
{

    // =================================================================
    // TConsoleVariableInt32
    // =================================================================

    TConsoleVariableInt32::TConsoleVariableInt32(const ::XCore::FString& InName,
                                                 ::int32 Default,
                                                 const ::XCore::FString& InHelp,
                                                 ECVarFlags InFlags) noexcept
        : m_name(InName)
        , m_help(InHelp)
        , m_flags(InFlags)
        , m_data(Default)
    {
    }

    ::XCore::FString TConsoleVariableInt32::GetString() const noexcept
    {
        // Snapshot the value under the shared lock then format outside.
        const ::int32 V = m_data.LoadAcquire();
        return ::XCore::FString::FromInt32(V);
    }

    void TConsoleVariableInt32::SetInt(::int32 Value, ECVarSetByPriority Priority) noexcept
    {
        ::XCore::HAL::FScopedWriteLock L(m_lock);

        // ReadOnly CVars: only the registration-time default applies.
        // Set calls after registration are ignored regardless of
        // priority.
        if (HasFlag(m_flags, ECVarFlags::ReadOnly))
        {
            return;
        }

        // Cascade rule: incoming priority must be >= current priority.
        // Ties resolve by recency (>=, not strict >).
        const ECVarSetByPriority Current = m_data.GetSetBy();
        if (static_cast<::uint8>(Priority) < static_cast<::uint8>(Current))
        {
            return;
        }

        m_data.StoreRelease(Value, Priority);
    }

    void TConsoleVariableInt32::SetString(const ::XCore::FString& Value, ECVarSetByPriority Priority) noexcept
    {
        // Parse the string; on parse error keep the existing value.
        // Parse errors are silent at the SetString surface (the
        // console parser is the appropriate place to surface a
        // diagnostic to the user).
        const auto R = Value.ToInt32();
        if (R.has_value())
        {
            SetInt(R.value(), Priority);
        }
    }

    // =================================================================
    // TConsoleVariableFloat
    // =================================================================

    TConsoleVariableFloat::TConsoleVariableFloat(const ::XCore::FString& InName,
                                                 float Default,
                                                 const ::XCore::FString& InHelp,
                                                 ECVarFlags InFlags) noexcept
        : m_name(InName)
        , m_help(InHelp)
        , m_flags(InFlags)
        , m_data(Default)
    {
    }

    ::XCore::FString TConsoleVariableFloat::GetString() const noexcept
    {
        const float V = m_data.LoadAcquire();
        return ::XCore::FString::FromFloat(V);
    }

    void TConsoleVariableFloat::SetFloat(float Value, ECVarSetByPriority Priority) noexcept
    {
        ::XCore::HAL::FScopedWriteLock L(m_lock);

        if (HasFlag(m_flags, ECVarFlags::ReadOnly))
        {
            return;
        }

        const ECVarSetByPriority Current = m_data.GetSetBy();
        if (static_cast<::uint8>(Priority) < static_cast<::uint8>(Current))
        {
            return;
        }

        m_data.StoreRelease(Value, Priority);
    }

    void TConsoleVariableFloat::SetString(const ::XCore::FString& Value, ECVarSetByPriority Priority) noexcept
    {
        const auto R = Value.ToFloat();
        if (R.has_value())
        {
            SetFloat(R.value(), Priority);
        }
    }

    // =================================================================
    // TConsoleVariableString
    // =================================================================

    TConsoleVariableString::TConsoleVariableString(const ::XCore::FString& InName,
                                                   const ::XCore::FString& Default,
                                                   const ::XCore::FString& InHelp,
                                                   ECVarFlags InFlags) noexcept
        : m_name(InName)
        , m_help(InHelp)
        , m_flags(InFlags)
        , m_value(Default)
        , m_setBy(ECVarSetByPriority::Default)
    {
    }

    ::int32 TConsoleVariableString::GetInt() const noexcept
    {
        ::XCore::HAL::FScopedReadLock L(m_lock);
        const auto R = m_value.ToInt32();
        return R.has_value() ? R.value() : 0;
    }

    float TConsoleVariableString::GetFloat() const noexcept
    {
        ::XCore::HAL::FScopedReadLock L(m_lock);
        const auto R = m_value.ToFloat();
        return R.has_value() ? R.value() : 0.0f;
    }

    ::XCore::FString TConsoleVariableString::GetString() const noexcept
    {
        ::XCore::HAL::FScopedReadLock L(m_lock);
        // Deep copy of the FString -- the shared lock guarantees we
        // are not racing a concurrent setter.
        return m_value;
    }

    void TConsoleVariableString::SetInt(::int32 Value, ECVarSetByPriority Priority) noexcept
    {
        SetString(::XCore::FString::FromInt32(Value), Priority);
    }

    void TConsoleVariableString::SetFloat(float Value, ECVarSetByPriority Priority) noexcept
    {
        SetString(::XCore::FString::FromFloat(Value), Priority);
    }

    void TConsoleVariableString::SetString(const ::XCore::FString& Value, ECVarSetByPriority Priority) noexcept
    {
        ::XCore::HAL::FScopedWriteLock L(m_lock);

        if (HasFlag(m_flags, ECVarFlags::ReadOnly))
        {
            return;
        }

        if (static_cast<::uint8>(Priority) < static_cast<::uint8>(m_setBy))
        {
            return;
        }

        m_value = Value;   // FString copy assign
        m_setBy = Priority;
    }

} // namespace XCore::Misc
