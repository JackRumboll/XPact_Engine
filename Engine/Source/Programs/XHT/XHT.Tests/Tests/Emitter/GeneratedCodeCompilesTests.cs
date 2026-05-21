// Copyright Simgenics. All Rights Reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Emitter;
using Xunit;
using Xunit.Abstractions;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// XHT.html Section 21.2 compile-test fixture (C5 audit). Generates a
/// synthetic <c>.gen.h</c> + <c>.gen.cpp</c> pair via the real emitter,
/// then invokes a system C++ compiler against the .gen.cpp with
/// <c>-c</c> (compile only, no link). Asserts the compile succeeds
/// with zero errors. Skips when no compiler is on PATH.
/// </summary>
/// <remarks>
/// Skip behaviour: the test searches for <c>g++</c>, <c>clang++</c>,
/// then <c>cl.exe</c>. If none is found the test is marked as a
/// successful no-op with a warning written via <see cref="ITestOutputHelper"/>.
/// On CI we expect at least one of these to be available.
///
/// The compiler is invoked with <c>-std=c++17 -fsyntax-only</c> (gcc /
/// clang) or <c>/Zs /std:c++17</c> (cl) -- syntax-only mode catches all
/// declaration / type errors without producing object files. The
/// <c>XReflectionRuntime.h</c> stub is pulled in via <c>-I</c> pointing
/// at <c>Engine/Source/Runtime/XCore/Public</c>.
/// </remarks>
[Collection(nameof(GeneratedCodeCompilesTests))]
[CollectionDefinition(nameof(GeneratedCodeCompilesTests), DisableParallelization = true)]
public sealed class GeneratedCodeCompilesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public GeneratedCodeCompilesTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(),
            "XHT.Tests-GenCppCompile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, recursive: true); } }
        catch { /* best-effort */ }
    }

    [Fact]
    public void EmittedGenCpp_CompilesWithSystemCxxCompiler()
    {
        string? compiler = FindCxxCompiler(out string compilerFlags, out bool isMsvc);
        if (compiler is null)
        {
            _output.WriteLine("No C++ compiler found on PATH (g++/clang++/cl); skipping integration compile test.");
            return;
        }

        // Locate XReflectionRuntime.h. The test runner's cwd is the test
        // assembly bin/ folder; walk up to the repo root.
        string includeRoot = FindXCoreIncludeRoot();
        Assert.True(Directory.Exists(includeRoot),
            $"XCore include root not found at '{includeRoot}'. Check Engine/Source/Runtime/XCore/Public/.");
        Assert.True(File.Exists(Path.Combine(includeRoot, "XReflectionRuntime.h")),
            "XReflectionRuntime.h stub missing under Engine/Source/Runtime/XCore/Public/.");

        // Build a synthetic fixture exercising every emitted descriptor
        // shape: class with super + property + function; struct with
        // property; enum with values; interface with function.
        EmitterContext ctx = EmitterTestHarness.MakeContext(_tempDir);

        XhtProperty prop = EmitterTestHarness.MakeProperty("Health", "int32");
        XhtFunction fn = EmitterTestHarness.MakeFunction("Open", "void",
            parameters: new[] { EmitterTestHarness.MakeParam("fraction", "float") });
        XhtClass valve = EmitterTestHarness.MakeClass(
            name: "XValve",
            sourcePath: "Public/XValve.h",
            super: "XActor",
            properties: new[] { prop },
            functions: new[] { fn });

        XhtProperty sp = EmitterTestHarness.MakeProperty("X", "float");
        XhtStruct vec = EmitterTestHarness.MakeStruct("XVector", properties: new[] { sp });

        XhtEnumValue ev1 = new("Closed", 0,
            Array.Empty<Specifier>(), new SourceSpan("X.h", 1, 1, 6));
        XhtEnumValue ev2 = new("Open", 1,
            Array.Empty<Specifier>(), new SourceSpan("X.h", 2, 1, 4));
        XhtEnum state = EmitterTestHarness.MakeEnum("EValveState",
            values: new[] { ev1, ev2 });

        XhtFunction ifFn = EmitterTestHarness.MakeFunction("Tick", "void");
        XhtInterface iface = EmitterTestHarness.MakeInterface("IPickable",
            functions: new[] { ifFn });

        HeaderEmitter hdr = new(ctx);
        SourceEmitter src = new(ctx);

        string genH = hdr.Render("Public/XValve.h",
            new XhtTypeBase[] { valve, vec, state, iface });
        string genCpp = src.Render("Public/XValve.h",
            new XhtTypeBase[] { valve, vec, state, iface });

        string genHPath = Path.Combine(_tempDir, "XValve.gen.h");
        string genCppPath = Path.Combine(_tempDir, "XValve.gen.cpp");
        File.WriteAllText(genHPath, genH);
        File.WriteAllText(genCppPath, genCpp);

        // The .gen.cpp includes "Public/XValve.h" (the source header) +
        // "XValve.gen.h" + "XCore/Public/XReflectionRuntime.h". Stub the
        // source header (empty) and provide an include path for the
        // gen.h sibling.
        File.WriteAllText(Path.Combine(_tempDir, "Public_XValve.h"),
            "// stub original source header\n");
        // The emit's #include "Public/XValve.h" looks for a "Public"
        // subdirectory; mirror it.
        string publicDir = Path.Combine(_tempDir, "Public");
        Directory.CreateDirectory(publicDir);
        File.WriteAllText(Path.Combine(publicDir, "XValve.h"), "// stub original source header\n");

        // Compose the compile command.
        string args;
        if (isMsvc)
        {
            args = $"/nologo /std:c++17 /Zs /I \"{_tempDir}\" /I \"{Path.Combine(includeRoot, "..", "..")}\" \"{genCppPath}\"";
        }
        else
        {
            // gcc / clang
            args = $"-std=c++17 -fsyntax-only {compilerFlags} -I \"{_tempDir}\" -I \"{Path.Combine(includeRoot, "..", "..")}\" \"{genCppPath}\"";
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

        Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);

        if (proc.ExitCode != 0)
        {
            _output.WriteLine("===== compiler stdout =====");
            _output.WriteLine(stdout);
            _output.WriteLine("===== compiler stderr =====");
            _output.WriteLine(stderr);
            _output.WriteLine("===== .gen.cpp =====");
            _output.WriteLine(File.ReadAllText(genCppPath));
            _output.WriteLine("===== .gen.h =====");
            _output.WriteLine(File.ReadAllText(genHPath));
        }
        Assert.True(proc.ExitCode == 0,
            $"System C++ compiler ({compiler}) failed to compile the generated .gen.cpp. " +
            $"ExitCode={proc.ExitCode}. See test output for compiler diagnostics.");
    }

    /// <summary>Locate XCore/Public by walking up from the test binary directory.</summary>
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
        return "Engine/Source/Runtime/XCore/Public"; // fallback; test will then assert
    }

    /// <summary>
    /// Locate a usable C++ compiler. Preference order: g++ (most likely
    /// present on Windows via MinGW), clang++, cl.exe (Visual Studio).
    /// </summary>
    private static string? FindCxxCompiler(out string extraFlags, out bool isMsvc)
    {
        extraFlags = string.Empty;
        isMsvc = false;
        foreach (string name in new[] { "g++", "g++.exe", "clang++", "clang++.exe" })
        {
            string? p = FindOnPath(name);
            if (p is not null)
            {
                return p;
            }
        }
        string? clPath = FindOnPath("cl.exe");
        if (clPath is not null)
        {
            isMsvc = true;
            return clPath;
        }
        return null;
    }

    private static string? FindOnPath(string exe)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) { return null; }
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) { continue; }
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
}
