// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Emit;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;
using Xunit.Abstractions;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// THE Phase 6.e gate (per /Documents/XIL2CPP.html Rev 4 Section 3.2 + 5.1 +
/// the XIL2CPP.html Section 21.2-style compile-test discipline mirrored from
/// XHT's <c>GeneratedCodeCompilesTests</c>): drive a synthetic
/// <c>[XClass]</c>-derived C# class (a couple of methods + an auto-property)
/// through the FULL pipeline (Pass 1 -&gt; Pass 2 -&gt; Pass 3 -&gt; Pass 4 -&gt;
/// <see cref="EmitDriver"/>) to a temp directory, write syntax-only STUB XCore
/// headers (the 24 layout-tag macros + the envelope tags with their Contract
/// Rev 13.9 content + the byte-sized reflection-type stubs sized to the sizeof
/// pins), then invoke the system C++ compiler in syntax-check-only mode at
/// C++20 with the CORRECTED two-path include set, and assert it compiles clean.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corrected include paths (vs the XHT test's bug).</b> XHT's
/// <c>GeneratedCodeCompilesTests</c> passes a single <c>-I</c> at
/// <c>&lt;XCore/Public&gt;/../..</c> (= <c>Engine/Source/Runtime</c>), which
/// resolves <c>#include "XCoreXObject/..."</c> but NOT a bare
/// <c>#include "XReflectionRuntime.h"</c> (that header lives under
/// <c>XCore/Public</c>, not <c>Runtime</c>). This gate passes BOTH
/// <c>-I Engine/Source/Runtime</c> AND
/// <c>-I Engine/Source/Runtime/XCore/Public</c>, plus <c>-I &lt;tempDir&gt;</c>
/// FIRST so the hermetic stub headers win.
/// </para>
/// <para>
/// <b>Skip, not fail, when no compiler is present.</b> The gate searches PATH
/// for g++ / clang++ / cl.exe; with none found it logs + returns a no-op
/// success (mirroring the XHT test). On CI at least one is expected.
/// </para>
/// <para>
/// <b>The sample compiles clean -- now over real identifier bodies.</b> The
/// synthetic sample exercises the baseline identifier-lowering surface
/// (<see cref="Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules.IdentifierLoweringRule"/>):
/// a literal-returning method, an empty-body method, an auto-property, AND
/// genuine identifier-using bodies -- a parameter return
/// (<c>Echo(int n) { return n; }</c> to <c>return n;</c>), an instance-field
/// return (<c>ReadCount() { return Count; }</c> to <c>return self-&gt;Count;</c>),
/// and an instance-property return (<c>ReadHealth() { return Health; }</c> to
/// the <c>get_</c> accessor call on <c>self</c>). These now lower to compilable
/// C++ instead of a TODO-comment-in-expression-position hole, so this gate
/// proves the baseline lowers + compiles a real (non-literal) method body.
/// </para>
/// </remarks>
[Collection(nameof(EmittedCodeCompilesTests))]
[CollectionDefinition(nameof(EmittedCodeCompilesTests), DisableParallelization = true)]
public sealed class EmittedCodeCompilesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public EmittedCodeCompilesTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(
            Path.GetTempPath(), "XIL2CPP.Tests-EmitCompile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void EmittedCsCpp_CompilesWithSystemCxxCompiler()
    {
        string? compiler = FindCxxCompiler(out bool isMsvc);
        if (compiler is null)
        {
            _output.WriteLine(
                "No C++ compiler found on PATH (g++/clang++/cl); skipping the 6.e compile gate.");
            return;
        }

        // -------------------------------------------------------------
        // 1. Drive the synthetic [XClass] sample through the full pipeline.
        // -------------------------------------------------------------
        const string attrStub =
            "namespace XPact.CoreXObject { public sealed class XClassAttribute : System.Attribute { } }";
        // The sample now exercises the identifier-lowering surface: a parameter
        // return, an instance-field return, and an instance-property return --
        // alongside the original literal-returning + empty-body + auto-property
        // members -- so the emitted bodies lower over real (non-literal)
        // identifiers and must still compile clean.
        const string sample =
            "namespace Game { [XPact.CoreXObject.XClassAttribute] public class XWidget { "
            + "public int Count; "
            + "public int Health { get; set; } "
            + "public int Answer() { return 42; } "
            + "public int Echo(int n) { return n; } "
            + "public int ReadCount() { return Count; } "
            + "public int ReadHealth() { return Health; } "
            + "public void Reset() { } } }";

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, attrStub, sample);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new CrossModuleNoThrowAnalyzer() });
        TierTable tierTable = Pass4Driver.Run(unit, pass3);

        string emitRoot = Path.Combine(_tempDir, "emit");
        Directory.CreateDirectory(emitRoot);

        EmitDriverResult emit = EmitDriver.Run(
            unit, pass3, tierTable, EmitTestHelpers.ContractVersionTag, emitRoot);
        Assert.False(emit.HasErrors, "Emit reported error diagnostics.");

        string transpiled = Path.Combine(emitRoot, "Transpiled");
        string[] sources = Directory.GetFiles(transpiled, "*.cs.cpp", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);

        // The .cs.cpp that carries the XWidget XClass bodies is the one to compile.
        string widgetCpp = sources.FirstOrDefault(
            p => File.ReadAllText(p).Contains("XWidget::StaticClass()", StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException("No emitted .cs.cpp contains the XWidget bodies.");

        // -------------------------------------------------------------
        // 2. Write the syntax-only stub XCore headers into the temp tree.
        // -------------------------------------------------------------
        WriteStubHeaders(_tempDir);

        // -------------------------------------------------------------
        // 3. Invoke the compiler in syntax-check-only mode at C++20.
        // -------------------------------------------------------------
        string includeRoot = FindXCoreIncludeRoot();
        string runtimeDir = Path.Combine(includeRoot, "..", "..");      // Engine/Source/Runtime
        string xcorePublicDir = includeRoot;                            // Engine/Source/Runtime/XCore/Public

        string stdFlag = isMsvc ? "/std:c++20" : SelectStdFlag(compiler);

        // Stub include dir FIRST so the hermetic stubs win; then the two
        // CORRECTED real include paths (the XHT-test bug fix); then the
        // Transpiled dir so the .cs.cpp resolves its sibling .cs.h.
        string args;
        if (isMsvc)
        {
            args = string.Join(" ",
                "/nologo", stdFlag, "/Zs",
                Quote("/I" + _tempDir),
                Quote("/I" + transpiled),
                Quote("/I" + runtimeDir),
                Quote("/I" + xcorePublicDir),
                Quote(widgetCpp));
        }
        else
        {
            args = string.Join(" ",
                stdFlag, "-fsyntax-only",
                "-I", Quote(_tempDir),
                "-I", Quote(transpiled),
                "-I", Quote(runtimeDir),
                "-I", Quote(xcorePublicDir),
                Quote(widgetCpp));
        }

        ProcessStartInfo psi = new(compiler, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _tempDir,
        };

        _output.WriteLine($"Invoking: {compiler} {args}");

        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);

        if (proc.ExitCode != 0)
        {
            _output.WriteLine("===== compiler stdout =====");
            _output.WriteLine(stdout);
            _output.WriteLine("===== compiler stderr =====");
            _output.WriteLine(stderr);
            _output.WriteLine("===== .cs.cpp =====");
            _output.WriteLine(File.ReadAllText(widgetCpp));
        }

        Assert.True(proc.ExitCode == 0,
            $"System C++ compiler ({compiler}) failed to compile the emitted .cs.cpp. "
            + $"ExitCode={proc.ExitCode}. See test output for diagnostics.");
    }

    // =================================================================
    // Stub header tree.
    // =================================================================

    private static void WriteStubHeaders(string root)
    {
        // ---- The umbrella runtime header the .cs.cpp includes. ----
        Write(Path.Combine(root, "XReflectionRuntime.h"), BuildReflectionRuntimeStub());

        // ---- XObject + Internal headers the .cs.h / .cs.cpp include. ----
        Write(Path.Combine(root, "XCoreXObject", "XObject.h"),
            "#pragma once\n#include \"XReflectionRuntime.h\"\n");
        Write(Path.Combine(root, "XCoreXObject", "Internal", "XObjectAllocator.h"),
            "#pragma once\n");
        Write(Path.Combine(root, "XCoreXObject", "Internal", "XGCWriteBarrier.h"),
            "#pragma once\n");

        // ---- The Reflection/*.h family the sizeof-pin block __has_include-s
        //      and includes. They are empty stubs: the umbrella already
        //      declared every ::XCore::Reflect type before this block, so the
        //      sizeof asserts resolve. Their mere existence makes the
        //      __has_include("Reflection/FClass.h") guard true so the sizeof
        //      pin family actually compiles + validates. ----
        foreach (string include in AbiPins.SizeofPinIncludes)
        {
            Write(Path.Combine(root, include.Replace('/', Path.DirectorySeparatorChar)),
                "#pragma once\n#include \"XReflectionRuntime.h\"\n");
        }
    }

    /// <summary>
    /// Build the umbrella stub: the layout-tag + envelope + sentinel macros
    /// (the 24 layout tags with their EXACT Contract Rev 13.9 content), the
    /// safe-point / check-SL no-op macros, <c>XPactDetail::CompileTimeStrEq</c>,
    /// and the byte-sized <c>::XCore::Reflect</c> / <c>::XCore::Exception</c> /
    /// <c>::XCore::Container</c> type stubs (sized to match the sizeof pins).
    /// </summary>
    private static string BuildReflectionRuntimeStub()
    {
        StringBuilder sb = new();
        sb.Append("#pragma once\n");
        sb.Append("#include <cstdint>\n");
        sb.Append("#include <cstddef>\n");
        sb.Append("#include <new>\n\n");

        // constinit portability shim: the emitted .cs.cpp uses the C++20
        // `constinit` keyword (gcc 10+ / clang 10+ / MSVC 19.29+). On an older
        // toolchain whose -std=c++2a does not yet implement the keyword (e.g.
        // gcc 8/9), degrade it to empty so the syntax-check still validates the
        // rest of the TU. This mirrors the real XReflectionRuntime.h's XCONSTINIT
        // fallback; a full C++20 compiler keeps the real `constinit` semantics.
        sb.Append("#if !defined(__cpp_constinit)\n");
        sb.Append("#  define constinit\n");
        sb.Append("#endif\n\n");

        // ConstInit / accessor sentinels.
        sb.Append("#define XPACT_WITH_CONSTINIT_XOBJECT 1\n");
        sb.Append("#define XPACT_PROPERTY_HAS_ACCESSORS 1\n");

        // No-op safe-point + check-SL macros (per the 6.e gate spec).
        sb.Append("#define XPACT_SAFEPOINT_CHECK() do{}while(0)\n");
        sb.Append("#define XPACT_CHECK_SL(c,m) do{(void)(c);}while(0)\n\n");

        // Envelope tags with their Contract Phase-1 content.
        sb.Append("#define XPACT_GC_ROOT_ABI_TAG ")
          .Append(CppLiteral(EmitDriver.GcRootAbi)).Append('\n');
        sb.Append("#define XPACT_EXCEPTION_ABI_TAG ")
          .Append(CppLiteral(EmitDriver.ExceptionAbi)).Append('\n');
        sb.Append("#define XPACT_MANGLING_SCHEME_TAG ")
          .Append(CppLiteral(EmitDriver.ManglingScheme)).Append("\n\n");

        // The 24 layout-tag macros with their EXACT Contract Rev 13.9 content.
        foreach ((string macro, string content) in AbiPins.LayoutTags)
        {
            sb.Append("#define ").Append(macro).Append(' ')
              .Append(CppLiteral(content)).Append('\n');
        }
        sb.Append('\n');

        // XPactDetail::CompileTimeStrEq -- constexpr C-string equality. The
        // implementation is ITERATIVE (a while loop), matching the real
        // XReflectionRuntime.h: a recursive form would recurse once per
        // character and blow the compiler's default constexpr-evaluation depth
        // (~512) on the ~870-character FClass layout-tag content string.
        sb.Append(
            "namespace XPactDetail {\n"
            + "  constexpr bool CompileTimeStrEq(const char* a, const char* b) {\n"
            + "    if (a == nullptr || b == nullptr) { return false; }\n"
            + "    while (*a != '\\0' && *b != '\\0') {\n"
            + "      if (*a != *b) { return false; }\n"
            + "      ++a; ++b;\n"
            + "    }\n"
            + "    return *a == '\\0' && *b == '\\0';\n"
            + "  }\n"
            + "}\n\n");

        // ::XCore::Container::FString (the interned-literal + string-property type).
        sb.Append(
            "namespace XCore { namespace Container {\n"
            + "  struct FString {\n"
            + "    const char* _data = nullptr; int _len = 0;\n"
            + "    constexpr FString() = default;\n"
            + "    constexpr FString(const char* d, int n) : _data(d), _len(n) {}\n"
            + "  };\n"
            + "} }\n\n");

        // ::XCore::Reflect type family (byte-sized to the sizeof pins).
        sb.Append("namespace XCore { namespace Reflect {\n");

        // The lifecycle table: aggregate-init shape { u32, u32, void(*[8])() } = 72 bytes.
        sb.Append(
            "  struct FXObjectLifecycleTable {\n"
            + "    uint32_t Capabilities; uint32_t _padHeader; void(*Slots[8])();\n"
            + "  };\n");

        // XObject base: 56 bytes; a GetClass() FakeVTable accessor for dispatchers.
        sb.Append("  struct FClass;\n");
        sb.Append(
            "  struct XObject {\n"
            + "    char _pad[56];\n"
            + "    const FClass* GetClass() const noexcept;\n"
            + "  };\n");

        // FClass: 240 bytes; aggregate with a LifecycleTable member (designated init).
        sb.Append(
            "  struct FClass {\n"
            + "    const FXObjectLifecycleTable* LifecycleTable;\n"
            + "    char _pad[232];\n"
            + "  };\n");

        // FXObjectInitializer: opaque (ClassConstructor takes a ref to it).
        sb.Append("  struct FXObjectInitializer { char _pad[8]; };\n");

        // The reflection runtime registrar.
        sb.Append(
            "  struct XReflectionRuntime {\n"
            + "    static void RegisterClass(const FClass*) noexcept {}\n"
            + "    static const FClass* FindClass(const char*) noexcept { return nullptr; }\n"
            + "  };\n");

        // Serialize-slot helper types (referenced by the Serialize lifecycle slot).
        sb.Append("  struct FArchive { char _pad[8]; };\n");
        sb.Append("  struct FArchiveContext { char _pad[8]; };\n");
        sb.Append("  struct FXGrayQueue { char _pad[8]; };\n");
        sb.Append("  struct FObjectPreSaveContext { char _pad[8]; };\n");

        // The remaining sizeof-pinned types (byte-sized to their pins).
        foreach ((string type, int bytes) in AbiPins.TypeSizes)
        {
            if (IsAlreadyDefinedReflectType(type))
            {
                continue;
            }
            sb.Append("  struct ").Append(type).Append(" { char _pad[")
              .Append(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append("]; };\n");
        }

        sb.Append("} }\n\n");

        // ::XCore::Exception::{XResult, XCSharpException} for the Tier-1 shim.
        sb.Append(
            "namespace XCore { namespace Exception {\n"
            + "  struct XManagedException { char _pad[8]; };\n"
            + "  struct XResult {\n"
            + "    enum EDiscriminator { Success = 0, Error = 1 };\n"
            + "    int discriminator = Success;\n"
            + "    XManagedException* exception = nullptr;\n"
            + "  };\n"
            + "  struct XCSharpException {\n"
            + "    XManagedException* GetManagedException() const { return nullptr; }\n"
            + "  };\n"
            + "} }\n\n");

        // The XObject::GetClass() out-of-line stub (after FClass is complete).
        sb.Append(
            "namespace XCore { namespace Reflect {\n"
            + "  inline const FClass* XObject::GetClass() const noexcept { return nullptr; }\n"
            + "} }\n");

        return sb.ToString();
    }

    /// <summary>
    /// The sizeof-pinned type names already given a hand-written stub above (so
    /// the byte-array fallback does not re-define them).
    /// </summary>
    private static bool IsAlreadyDefinedReflectType(string type) => type switch
    {
        "FClass" or "FXObjectLifecycleTable" or "XObject" => true,
        _ => false,
    };

    // =================================================================
    // Compiler discovery + flags (mirrors the XHT test discipline).
    // =================================================================

    /// <summary>
    /// Locate a usable C++ compiler. Preference order: g++ / clang++ (likely on
    /// Windows via MinGW / LLVM), then cl.exe (MSVC).
    /// </summary>
    private static string? FindCxxCompiler(out bool isMsvc)
    {
        isMsvc = false;
        foreach (string name in new[] { "g++", "g++.exe", "clang++", "clang++.exe" })
        {
            string? p = FindOnPath(name);
            if (p is not null)
            {
                return p;
            }
        }
        string? cl = FindOnPath("cl.exe");
        if (cl is not null)
        {
            isMsvc = true;
            return cl;
        }
        return null;
    }

    /// <summary>
    /// Choose the C++20 std flag a gcc / clang accepts: prefer
    /// <c>-std=c++20</c> (gcc 10+ / clang 10+), falling back to
    /// <c>-std=c++2a</c> for an older toolchain (e.g. gcc 8/9) that names the
    /// same standard <c>c++2a</c>. The probe compiles a one-line TU to test
    /// the flag.
    /// </summary>
    private static string SelectStdFlag(string compiler)
    {
        if (StdFlagAccepted(compiler, "-std=c++20"))
        {
            return "-std=c++20";
        }
        return "-std=c++2a";
    }

    private static bool StdFlagAccepted(string compiler, string flag)
    {
        string probe = Path.Combine(Path.GetTempPath(), "xil2cpp-stdprobe-" + Guid.NewGuid().ToString("N") + ".cpp");
        try
        {
            File.WriteAllText(probe, "int main(){return 0;}\n");
            ProcessStartInfo psi = new(compiler, flag + " -fsyntax-only \"" + probe + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(probe)) { File.Delete(probe); } } catch { /* best-effort */ }
        }
    }

    private static string? FindOnPath(string exe)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }
            try
            {
                string candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Malformed PATH entry; skip.
            }
        }
        return null;
    }

    /// <summary>Locate Engine/Source/Runtime/XCore/Public by walking up from the test binary directory.</summary>
    private static string FindXCoreIncludeRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "Engine", "Source", "Runtime", "XCore", "Public");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine("Engine", "Source", "Runtime", "XCore", "Public");
    }

    // =================================================================
    // Helpers.
    // =================================================================

    private static void Write(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>Render a C++ string literal matching the emitter's encoder.</summary>
    private static string CppLiteral(string s) => CppWriter.EncodeCStringLiteral(s);
}
