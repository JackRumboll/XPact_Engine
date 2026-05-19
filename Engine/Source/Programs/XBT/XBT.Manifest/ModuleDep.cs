// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Typed dependency entry per Toolchain Contract Rev 13 Section 9.1.
/// Replaces the bare-string dependency in UE's
/// <c>ModuleRules</c>; the <c>InterfaceModule = true</c> flag replaces
/// the dropped <c>PublicIncludePathModuleNames</c> (Rev 11 audit
/// finding #9).
/// </summary>
/// <param name="Name">Module name (matches the producing module's <c>Name</c>).</param>
/// <param name="InterfaceModule">
/// True if the dependency is header-only / no link. XBT validates that
/// no TU references a symbol from an interface-only module.
/// </param>
public readonly record struct ModuleDep(string Name, bool InterfaceModule)
{
    /// <summary>
    /// Implicit conversion from a bare string -- used for the common
    /// case of a link-time dependency. TOML descriptors accept both
    /// <c>"XCore"</c> (this shorthand, <c>InterfaceModule = false</c>)
    /// and <c>{ name = "XCore", interface_module = false }</c> (full
    /// form) per Toolchain Contract Section 9.1.
    /// </summary>
    public static implicit operator ModuleDep(string name)
        => new(name, false);
}
