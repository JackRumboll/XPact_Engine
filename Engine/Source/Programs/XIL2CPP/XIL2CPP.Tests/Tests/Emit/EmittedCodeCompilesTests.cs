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
        CompileResult result = CompileSyntaxOnly(compiler, isMsvc, widgetCpp, transpiled);

        if (result.ExitCode != 0)
        {
            _output.WriteLine("===== compiler stdout =====");
            _output.WriteLine(result.Stdout);
            _output.WriteLine("===== compiler stderr =====");
            _output.WriteLine(result.Stderr);
            _output.WriteLine("===== .cs.cpp =====");
            _output.WriteLine(File.ReadAllText(widgetCpp));
        }

        Assert.True(result.ExitCode == 0,
            $"System C++ compiler ({compiler}) failed to compile the emitted .cs.cpp. "
            + $"ExitCode={result.ExitCode}. See test output for diagnostics.");
    }

    /// <summary>
    /// THE Phase 6.f/6.g/6.h GC-emit integration gate: drive a synthetic
    /// <c>[XClass]</c>-derived C# class that exercises EVERY GC-emit seam in one
    /// module &#8211; a container field (Phase 6.f
    /// <c>TArray&lt;XPtr&lt;XActor&gt;&gt;</c> partial spec), an XObject
    /// auto-property (Phase 6.h write-barrier setter), a method holding live
    /// managed references (Phase 6.g <c>_liveRefs[]</c> shadow stack +
    /// file-scope <c>FStackMapRecord</c> / <c>XStackMapTable::Register</c>), a
    /// loop (Phase 6.g <c>XPACT_BACKEDGE_SAFEPOINT_CHECK()</c> back-edge poll),
    /// and an XObject local (Phase 6.g per-local shadow-stack slot + RAII clear
    /// guard) &#8211; through the FULL pipeline (Pass 1 -&gt; Pass 4 -&gt;
    /// <see cref="EmitDriver"/>) to a temp dir, write the GC-aware stub headers,
    /// then compile the per-method GC TU with the real system C++ compiler
    /// (<c>-fsyntax-only</c>, C++20 with the <c>-std=c++2a</c> fallback) and
    /// assert exit 0. Additionally asserts the emitted output CONTAINS each GC
    /// feature so a regression that silently drops one fails the gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pass-3 analyzer set.</b> Unlike the baseline 6.e gate (which runs only
    /// <see cref="CrossModuleNoThrowAnalyzer"/>), this gate ALSO runs
    /// <see cref="ContainerAnalyzer"/> (so the <c>List&lt;XActor&gt;</c> field
    /// records a <see cref="Simgenics.XPact.XIL2CPP.Analysis.ContainerSite"/> and
    /// the Phase-6.f container partial-spec unit is actually emitted),
    /// <see cref="LongLoopAnalyzer"/> (the back-edge safe-point decision input),
    /// and <see cref="ReferenceStoreAnalyzer"/> (the write-barrier analysis
    /// surface). Without <see cref="ContainerAnalyzer"/> the synthetic
    /// container-spec <c>.cs.cpp</c> would never be produced.
    /// </para>
    /// <para>
    /// <b>The compile targets are BOTH GC TUs.</b> The emitter places the
    /// per-method 6.g/6.h scaffolding (shadow stack, stack map, write barrier,
    /// back-edge poll) in the per-FILE <c>.cs.cpp</c>, and the 6.f container
    /// partial specializations in a SEPARATE synthetic module-level
    /// <c>&lt;Module&gt;.ContainerSpecs.cs.cpp</c> (per
    /// <see cref="Simgenics.XPact.XIL2CPP.Emit.Cpp.Pass6Driver"/>). This gate
    /// COMPILES both &#8211; the per-method TU (the &#8220;full GC scaffolding
    /// together in one TU&#8221; surface) AND the 6.f container partial-spec TU
    /// (which now compiles after FIX 1; see below) &#8211; and asserts each GC
    /// seam is present so a regression that silently drops one fails the gate.
    /// </para>
    /// <para>
    /// <b>The sample now uses the FIX-1..4 constructs verbatim (no deviations).</b>
    /// Earlier this gate substituted in-scope stand-ins for constructs that lowered
    /// to a <c>// TODO(6.e)</c> hole; those gaps are now FIXED, so the sample
    /// exercises each fixed construct directly:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>FIX 2 (statement-position field store -&gt; barrier).</b>
    ///     <c>Assign(XActor a) { Boss = a; }</c> &#8211; a statement-position
    ///     XObject field store now routes through the new
    ///     <c>ExpressionStatementLoweringRule</c> to the
    ///     <c>AssignmentLoweringRule</c>, so the Phase-6.h
    ///     <c>XPACT_GC_STORE(self, &amp;(self-&gt;Boss), a);</c> barrier fires from
    ///     the method BODY (previously this lowered to a TODO comment and the body
    ///     barrier never fired).
    ///   </description></item>
    ///   <item><description>
    ///     <b>FIX 3 (for-init declaration).</b>
    ///     <c>Count(int n) { for (int i = 0; i &lt; n; i++) { } ... }</c> &#8211;
    ///     the <c>for</c> init's <c>VariableDeclaration</c> now lowers inline to
    ///     <c>int32_t i = 0</c> (the genuine C++ type spelling) instead of an
    ///     ill-formed TODO comment in the <c>for(...)</c> header; the back-edge
    ///     <c>XPACT_BACKEDGE_SAFEPOINT_CHECK()</c> is still the first body line.
    ///   </description></item>
    ///   <item><description>
    ///     <b>FIX 4 (explicit-typed reference local).</b>
    ///     <c>Pick(XActor fallback) { XActor chosen = fallback; ... }</c> &#8211;
    ///     an explicit-typed XObject-derived local now spells the C++ type as a
    ///     pointer over its qualified name (<c>::Game::XActor* chosen</c>), so it
    ///     agrees with its pointer-typed initializer / return; it roots in the
    ///     shadow stack with the same <c>_liveRefs</c> slot + RAII guard the
    ///     <c>var</c> form used.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>FIX 1: the container partial-spec TU now COMPILES (was assert-present
    /// only).</b> The explicit specialization was previously emitted as
    /// <c>class ::XCore::Container::TArray&lt;XPtr&lt;XActor&gt;&gt;</c> &#8211; a
    /// specialization name carrying a leading global-scope qualifier (<c>::</c>),
    /// which g++ rejects (&#8220;global qualification of class name is invalid
    /// before '{'&#8221;), so this gate could only assert its content was present.
    /// The emitter now wraps the explicit specialization in the primary template's
    /// own namespace and names it UNqualified with the <c>struct</c> class-key
    /// (<c>namespace XCore { namespace Container { template &lt;&gt; struct
    /// TArray&lt;XPtr&lt;XActor&gt;&gt; { ... }; } }</c>), which is well-formed, so
    /// this gate now COMPILES the container TU against the
    /// <c>TArray</c>/<c>TMap</c>/<c>TSet</c> primary-template stubs and asserts
    /// exit 0.
    /// </para>
    /// </remarks>
    [Fact]
    public void EmittedGcScaffold_CompilesWithSystemCxxCompiler()
    {
        string? compiler = FindCxxCompiler(out bool isMsvc);
        if (compiler is null)
        {
            _output.WriteLine(
                "No C++ compiler found on PATH (g++/clang++/cl); skipping the 6.f/6.g/6.h GC compile gate.");
            return;
        }

        // -------------------------------------------------------------
        // 1. Drive the rich GC sample through the full pipeline (with the
        //    container / long-loop / reference-store analyzers wired so the
        //    container partial-spec unit is actually emitted + the loop / store
        //    seams are analyzed).
        // -------------------------------------------------------------
        const string attrStub =
            "namespace XPact.CoreXObject { public sealed class XClassAttribute : System.Attribute { } }";
        const string xobjectStub =
            "namespace XPact.CoreXObject { public class XObject { } }";

        // XSquad exercises ALL GC-emit seams in one module, now over the
        // FIX-1..4 constructs (no documented deviations remain):
        //   Leader (XActor auto-property)              -> 6.h write-barrier setter
        //   Members (List<XActor>)                     -> 6.f TArray<XPtr<XActor>> spec
        //   Assign(XActor a) { Boss = a; }             -> 6.h statement-position
        //                                                 field store -> XPACT_GC_STORE
        //                                                 (FIX 2: ExpressionStatement
        //                                                 routes through the barrier)
        //   Count(int n) { for (int i=0;i<n;i++){} }   -> 6.g loop back-edge safepoint
        //                                                 over a for-INIT declaration
        //                                                 (FIX 3: inline `int32_t i = 0`)
        //   Pick(XActor f) { XActor chosen = f; ...}   -> 6.g XObject-local slot + guard
        //                                                 over an EXPLICIT-typed
        //                                                 reference local (FIX 4:
        //                                                 spelled `::Game::XActor*`)
        const string sample =
            "namespace Game { public class XActor : XPact.CoreXObject.XObject { } }"
            + "namespace Game { [XPact.CoreXObject.XClassAttribute] public class XSquad : XPact.CoreXObject.XObject { "
            + "public XActor Leader { get; set; } "
            + "public XActor Boss; "
            + "public System.Collections.Generic.List<XActor> Members; "
            + "public void Assign(XActor a) { Boss = a; } "
            + "public int Count(int n) { for (int i = 0; i < n; i++) { } return n; } "
            + "public XActor Pick(XActor fallback) { XActor chosen = fallback; return chosen; } } }";

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            isSimPath: false, attrStub, xobjectStub, sample);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit,
            new ISemanticAnalyzer[]
            {
                new CrossModuleNoThrowAnalyzer(),
                new ContainerAnalyzer(),
                new LongLoopAnalyzer(),
                new ReferenceStoreAnalyzer(),
            });
        TierTable tierTable = Pass4Driver.Run(unit, pass3);

        string emitRoot = Path.Combine(_tempDir, "emit");
        Directory.CreateDirectory(emitRoot);

        EmitDriverResult emit = EmitDriver.Run(
            unit, pass3, tierTable, EmitTestHelpers.ContractVersionTag, emitRoot);
        Assert.False(emit.HasErrors, "Emit reported error diagnostics.");

        string transpiled = Path.Combine(emitRoot, "Transpiled");
        string[] sources = Directory.GetFiles(transpiled, "*.cs.cpp", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);

        // The per-method GC TU: the .cs.cpp carrying the XSquad XClass bodies.
        string squadCpp = sources.FirstOrDefault(
            p => File.ReadAllText(p).Contains("XSquad::StaticClass()", StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException("No emitted .cs.cpp contains the XSquad bodies.");
        string squadText = File.ReadAllText(squadCpp);

        // The 6.f container partial-spec TU: the synthetic module-level unit.
        string containerCpp = sources.FirstOrDefault(
            p => p.Replace('\\', '/').EndsWith(".ContainerSpecs.cs.cpp", StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException("No emitted container-spec .cs.cpp was produced.");
        string containerText = File.ReadAllText(containerCpp);

        // -------------------------------------------------------------
        // 2. Assert ALL FIVE GC-emit seams are present (a regression that
        //    silently drops one fails the gate).
        // -------------------------------------------------------------

        // 6.f: the TArray<XPtr<XActor>> partial spec + its Strong root-span
        // registration (in the container-spec TU). FIX 1: the explicit
        // specialization is emitted in-namespace with the UNqualified `struct`
        // template name (no leading global qualifier the old `class
        // ::XCore::Container::TArray<...>` form carried, which g++ rejected).
        Assert.Contains("namespace XCore {", containerText, StringComparison.Ordinal);
        Assert.Contains("namespace Container {", containerText, StringComparison.Ordinal);
        Assert.Contains(
            "struct TArray<XPtr<XActor>>", containerText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "class ::XCore::Container::TArray<XPtr<XActor>>", containerText, StringComparison.Ordinal);
        Assert.Contains(
            "::XCore::Reflect::XGC_RegisterRootSpan(&_rootSpan, GetData(), Num(), sizeof(XPtr<XActor>), ::XCore::Reflect::XGCRootKind::Strong);",
            containerText, StringComparison.Ordinal);

        // 6.g: the per-method shadow stack (the _liveRefs[] array).
        Assert.Contains(
            MethodShadowStackBuilder.SlotElementType + " " + MethodShadowStackBuilder.ArrayName + "[",
            squadText, StringComparison.Ordinal);

        // 6.g: the file-scope FStackMapRecord + XStackMapTable::Register registrar.
        Assert.Contains("::XCore::Reflect::FStackMapRecord _stackMap = {", squadText, StringComparison.Ordinal);
        Assert.Contains("::XCore::Reflect::XStackMapTable::Register(", squadText, StringComparison.Ordinal);

        // 6.h: the write-barrier store. FIX 2: a STATEMENT-position XObject field
        // store (`Boss = a;`) now lowers through the ExpressionStatement ->
        // AssignmentLoweringRule path, so the barrier fires from the method BODY
        // (not just the property setter). The implicit-this store carries
        // parent = self, slot = &(self->Boss), value = a.
        Assert.Contains("XPACT_GC_STORE(", squadText, StringComparison.Ordinal);
        Assert.Contains(
            "XPACT_GC_STORE(self, &(self->Boss), a);", squadText, StringComparison.Ordinal);

        // 6.g: the loop back-edge safe-point poll. FIX 3: the loop is now a
        // for-loop with an INT init clause (`for (int i = 0; i < n; i++)`), whose
        // for-init declaration lowers inline to its genuine C++ type spelling
        // (`int32_t i = 0`) instead of an ill-formed TODO comment in the header.
        Assert.Contains("XPACT_BACKEDGE_SAFEPOINT_CHECK(", squadText, StringComparison.Ordinal);
        Assert.Contains("for (int32_t i = 0;", squadText, StringComparison.Ordinal);

        // 6.g: the XObject-local shadow-stack slot + its RAII clear guard. FIX 4:
        // the local is now EXPLICIT-typed (`XActor chosen = fallback;`), spelled
        // as a pointer over its qualified C++ name (`::Game::XActor* chosen`) so
        // it agrees with the pointer-typed initializer / return.
        Assert.Contains("struct _XilGcGuard_", squadText, StringComparison.Ordinal);
        Assert.Contains("::Game::XActor* chosen = fallback;", squadText, StringComparison.Ordinal);

        // -------------------------------------------------------------
        // 3. Write the GC-aware stub headers + compile BOTH GC TUs:
        //    (a) the per-method 6.g/6.h TU (shadow stack, stack map, write
        //        barrier, for-init back-edge poll, explicit-typed local), and
        //    (b) the 6.f container partial-spec TU -- which (FIX 1) now COMPILES
        //        (was previously assert-present-only because the old leading-`::`
        //        specialization-name form was rejected by g++).
        // -------------------------------------------------------------
        WriteStubHeaders(_tempDir);

        CompileResult result = CompileSyntaxOnly(compiler, isMsvc, squadCpp, transpiled);

        if (result.ExitCode != 0)
        {
            _output.WriteLine("===== compiler stdout =====");
            _output.WriteLine(result.Stdout);
            _output.WriteLine("===== compiler stderr =====");
            _output.WriteLine(result.Stderr);
            _output.WriteLine("===== GC .cs.cpp =====");
            _output.WriteLine(squadText);
        }

        Assert.True(result.ExitCode == 0,
            $"System C++ compiler ({compiler}) failed to compile the emitted GC .cs.cpp. "
            + $"ExitCode={result.ExitCode}. See test output for diagnostics.");

        // (b) The 6.f container partial-spec TU now COMPILES (FIX 1).
        CompileResult containerResult = CompileSyntaxOnly(compiler, isMsvc, containerCpp, transpiled);

        if (containerResult.ExitCode != 0)
        {
            _output.WriteLine("===== container compiler stdout =====");
            _output.WriteLine(containerResult.Stdout);
            _output.WriteLine("===== container compiler stderr =====");
            _output.WriteLine(containerResult.Stderr);
            _output.WriteLine("===== container .cs.cpp =====");
            _output.WriteLine(containerText);
        }

        Assert.True(containerResult.ExitCode == 0,
            $"System C++ compiler ({compiler}) failed to compile the emitted 6.f container "
            + $"partial-spec .cs.cpp. ExitCode={containerResult.ExitCode}. See test output for diagnostics.");
    }

    /// <summary>
    /// Compile <paramref name="cppFile"/> in syntax-check-only mode at C++20
    /// (with the <c>-std=c++2a</c> fallback for an older gcc), with the include
    /// set: the hermetic stub dir (<see cref="_tempDir"/>) FIRST so the stubs
    /// win, then the two CORRECTED real XCore include paths, then
    /// <paramref name="transpiledDir"/> so the <c>.cs.cpp</c> resolves its
    /// sibling <c>.cs.h</c>. Returns the process exit code + captured output.
    /// </summary>
    private CompileResult CompileSyntaxOnly(
        string compiler, bool isMsvc, string cppFile, string transpiledDir)
    {
        string includeRoot = FindXCoreIncludeRoot();
        string runtimeDir = Path.Combine(includeRoot, "..", "..");      // Engine/Source/Runtime
        string xcorePublicDir = includeRoot;                            // Engine/Source/Runtime/XCore/Public

        string stdFlag = isMsvc ? "/std:c++20" : SelectStdFlag(compiler);

        string args;
        if (isMsvc)
        {
            args = string.Join(" ",
                "/nologo", stdFlag, "/Zs",
                Quote("/I" + _tempDir),
                Quote("/I" + transpiledDir),
                Quote("/I" + runtimeDir),
                Quote("/I" + xcorePublicDir),
                Quote(cppFile));
        }
        else
        {
            args = string.Join(" ",
                stdFlag, "-fsyntax-only",
                "-I", Quote(_tempDir),
                "-I", Quote(transpiledDir),
                "-I", Quote(runtimeDir),
                "-I", Quote(xcorePublicDir),
                Quote(cppFile));
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

        return new CompileResult(proc.ExitCode, stdout, stderr);
    }

    /// <summary>The result of a syntax-only compile invocation.</summary>
    private readonly record struct CompileResult(int ExitCode, string Stdout, string Stderr);

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

        // ---- GC-ABI scaffolding stubs (prerequisite for Phases 6.f/6.g/6.h). ----
        AppendGcAbiStub(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Append the GC-ABI scaffolding stub surface the upcoming GC-emit phases
    /// (6.f shadow-stack / 6.g stack-map registration / 6.h container root-span
    /// + write-barrier) will need so the emitted <c>.cs.cpp</c> still compiles
    /// against these stubs. These types/macros are UNUSED by the current 6.e
    /// emit (which still emits the GC seams as <c>TODO(...)</c> comments, not
    /// real code), so this is purely additive: the existing gate output is
    /// unchanged and the new stubs are available-but-unused. Every type / macro
    /// matches the spelling the GC-emit phases will use per
    /// <c>/Documents/XIL2CPP.html</c> Section 5.x + the
    /// <c>/Documents/XCoreXObject.html</c> Section 5.2 / 5.3 surface:
    /// <c>::XCore::Reflect::XGCRootKind { Strong, Conservative }</c>, the
    /// <c>XGC_*RootSpan</c> / <c>XGC_WriteBarrier</c> free functions,
    /// <c>::XCore::Reflect::FStackMapRecord</c> +
    /// <c>::XCore::Reflect::XStackMapTable::Register(...)</c>, the
    /// <c>XPACT_GC_STORE(parent, slotPtr, newVal)</c> write-barrier macro, and
    /// the <c>XPACT_BACKEDGE_SAFEPOINT_CHECK()</c> back-edge safe-point alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotency / no-redefine guard.</b> The byte-sized fallback loop in
    /// <see cref="BuildReflectionRuntimeStub"/> already defines
    /// <c>::XCore::Reflect::XGCRootSpan</c> (32 bytes, the
    /// <c>XPACT_XGC_ROOTSPAN_LAYOUT_TAG</c> sizeof-pin shape) from
    /// <see cref="AbiPins.TypeSizes"/>. We therefore do NOT re-declare it here
    /// -- the GC-ABI functions below reference the already-present
    /// <c>XGCRootSpan</c> stub by name.
    /// </para>
    /// <para>
    /// <b>XPtr is now a CLASS TEMPLATE (Phase 6.g).</b> The GC-emit phases spell
    /// the shadow-stack element as
    /// <c>::XCore::Reflect::XPtr&lt;::XCore::Reflect::XObject&gt;</c>, and the
    /// AbiPins sizeof pin now emits the canonical templated form
    /// <c>sizeof(::XCore::Reflect::XPtr&lt;::XCore::Reflect::XObject&gt;) == 8</c>
    /// (XIL2CPP.html line 3538). To satisfy BOTH the templated pin AND the
    /// shadow-stack array declaration, <c>XPtr</c> is declared here as
    /// <c>template&lt;typename T&gt; struct XPtr { T* _p; ... };</c> (8 bytes;
    /// the single raw pointer member). It is therefore EXCLUDED from the
    /// byte-sized fallback loop (see <see cref="IsAlreadyDefinedReflectType"/>)
    /// so the two declarations never clash. The constructors / null-assignment
    /// operators match the shadow-stack emit's
    /// <c>XPtr&lt;XObject&gt;(reinterpret_cast&lt;XObject*&gt;(expr))</c> /
    /// <c>_liveRefs[i] = nullptr;</c> usage.
    /// </para>
    /// </remarks>
    /// <param name="sb">The umbrella-header string builder being assembled.</param>
    private static void AppendGcAbiStub(StringBuilder sb)
    {
        sb.Append('\n');
        sb.Append("// =====================================================================\n");
        sb.Append("// GC-ABI scaffolding stubs (prerequisite for Phases 6.f/6.g/6.h).\n");
        sb.Append("// Phase 6.g: the method bodies now emit the real shadow stack + stack\n");
        sb.Append("// map, so these stubs are EXERCISED by the gate's emitted TU.\n");
        sb.Append("// XGCRootSpan is ALREADY declared (byte-sized to its sizeof pin) by the\n");
        sb.Append("// fallback loop above; XPtr is declared here as a class template (the\n");
        sb.Append("// shadow-stack element type) and excluded from the fallback loop.\n");
        sb.Append("// =====================================================================\n");

        // The ::XCore::Reflect GC-ABI type + free-function surface. XObject and
        // XGCRootSpan are complete by this point (declared above), so the
        // XGC_* signatures + the XStackMapTable::Register stub resolve cleanly.
        sb.Append("namespace XCore { namespace Reflect {\n");

        // XPtr<T> -- the templated managed-pointer the shadow-stack array element
        // (`XPtr<XObject> _liveRefs[N]`) and the AbiPins sizeof pin
        // (`sizeof(XPtr<XObject>) == 8`, XIL2CPP.html line 3538) both spell. A
        // single raw T* member => 8 bytes. The constructors / null-assignment
        // match the shadow-stack emit: `XPtr<XObject>(reinterpret_cast<...>(e))`
        // (the T* ctor) and `_liveRefs[i] = nullptr;` (the nullptr ctor + assign).
        sb.Append("#ifndef XPACT_STUB_HAS_XPTR\n");
        sb.Append("#define XPACT_STUB_HAS_XPTR 1\n");
        sb.Append("  template<typename T> struct XPtr {\n");
        sb.Append("    T* _p;\n");
        sb.Append("    XPtr() noexcept : _p(nullptr) {}\n");
        sb.Append("    XPtr(T* p) noexcept : _p(p) {}\n");
        sb.Append("    XPtr(decltype(nullptr)) noexcept : _p(nullptr) {}\n");
        sb.Append("    XPtr& operator=(decltype(nullptr)) noexcept { _p=nullptr; return *this; }\n");
        // Implicit decay to void* (Phase 6.h write-barrier surface): the
        // XObject-derived property SETTER lowers its barrier as
        // `XPACT_GC_STORE(self, &(self->__BackingField_<Name>), value)` where
        // `value` is an `XPtr<T>` VALUE (PropertyEmitter renders the slot type
        // as XPtr<T>), and the macro's newVal arm does `static_cast<void*>`.
        // A class type has no built-in conversion to void*, so without this
        // operator `static_cast<void*>(XPtr<T>)` is ill-formed. The conversion
        // yields the raw managed pointer the barrier records; it ALSO keeps the
        // raw-pointer + literal-nullptr call sites (the AssignmentLoweringRule
        // field-store form) valid, since those already convert to void* directly.
        sb.Append("    operator void*() const noexcept { return _p; }\n");
        sb.Append("  };\n");
        sb.Append("#endif\n");

        // XGCRootKind { Strong, Conservative } -- the kind discriminator the
        // emitted XGCRootSpan aggregate-init + XGC_RegisterRootSpan calls use.
        sb.Append("#ifndef XPACT_STUB_HAS_XGCROOTKIND\n");
        sb.Append("#define XPACT_STUB_HAS_XGCROOTKIND 1\n");
        sb.Append("  enum class XGCRootKind : uint8_t { Strong = 0, Conservative = 1 };\n");
        sb.Append("#endif\n");

        // FStackMapRecord -- fixed 16-slot stub array so the emitted designated-
        // initializer form `{ .numLiveRefs = N, .liveRefOffsets = { 0, 8, 16 } }`
        // (Phase 6.g) has a sized array to initialise. alignas(8) per the record
        // ABI. _pad keeps the header word 8-byte aligned ahead of the int array.
        sb.Append("#ifndef XPACT_STUB_HAS_FSTACKMAPRECORD\n");
        sb.Append("#define XPACT_STUB_HAS_FSTACKMAPRECORD 1\n");
        sb.Append("  struct alignas(8) FStackMapRecord {\n");
        sb.Append("    uint32_t pcRangeBegin;\n");
        sb.Append("    uint32_t pcRangeEnd;\n");
        sb.Append("    uint16_t numLiveRefs;\n");
        sb.Append("    uint16_t _pad;\n");
        sb.Append("    int32_t  liveRefOffsets[16];\n");
        sb.Append("  };\n");
        sb.Append("#endif\n");

        // XStackMapTable::Register -- the per-function stack-map registrar the
        // Phase 6.g $stackmap registration block calls.
        sb.Append("#ifndef XPACT_STUB_HAS_XSTACKMAPTABLE\n");
        sb.Append("#define XPACT_STUB_HAS_XSTACKMAPTABLE 1\n");
        sb.Append("  struct XStackMapTable {\n");
        sb.Append("    static void Register(unsigned long long funcAddr,\n");
        sb.Append("                         unsigned long long funcSize,\n");
        sb.Append("                         const ::XCore::Reflect::FStackMapRecord* rec) noexcept {\n");
        sb.Append("      (void)funcAddr; (void)funcSize; (void)rec;\n");
        sb.Append("    }\n");
        sb.Append("  };\n");
        sb.Append("#endif\n");

        // XGC root-span + write-barrier free functions. extern "C" gives them a
        // flat C linker symbol (matching the runtime ABI) while their C++ name
        // lookup keeps the ::XCore::Reflect:: qualification the emitter uses.
        // No-op bodies; every parameter is referenced via a (void) cast so the
        // gate's -Werror=unused-parameter posture stays clean. Sizes use
        // unsigned long long (the size_t-compatible width the emitter passes).
        sb.Append("#ifndef XPACT_STUB_HAS_XGC_FREE_FUNCS\n");
        sb.Append("#define XPACT_STUB_HAS_XGC_FREE_FUNCS 1\n");
        sb.Append("  extern \"C\" inline void XGC_RegisterRootSpan(\n");
        sb.Append("      ::XCore::Reflect::XGCRootSpan* span, void* base,\n");
        sb.Append("      unsigned long long byteLength, unsigned long long elementStride,\n");
        sb.Append("      ::XCore::Reflect::XGCRootKind kind) noexcept {\n");
        sb.Append("    (void)span; (void)base; (void)byteLength; (void)elementStride; (void)kind;\n");
        sb.Append("  }\n");
        sb.Append("  extern \"C\" inline void XGC_UpdateRootSpan(\n");
        sb.Append("      ::XCore::Reflect::XGCRootSpan* span, void* base,\n");
        sb.Append("      unsigned long long byteLength, unsigned long long elementStride) noexcept {\n");
        sb.Append("    (void)span; (void)base; (void)byteLength; (void)elementStride;\n");
        sb.Append("  }\n");
        sb.Append("  extern \"C\" inline void XGC_UnregisterRootSpan(\n");
        sb.Append("      ::XCore::Reflect::XGCRootSpan* span) noexcept {\n");
        sb.Append("    (void)span;\n");
        sb.Append("  }\n");
        sb.Append("  extern \"C\" inline void XGC_WriteBarrier(void** slot, void* newVal) noexcept {\n");
        sb.Append("    (void)slot; (void)newVal;\n");
        sb.Append("  }\n");
        sb.Append("#endif\n");

        sb.Append("} }\n");

        // File-scope GC macros (after the ::XCore::Reflect surface is complete).
        //
        // XPACT_GC_STORE(parent, slotPtr, newVal): the no-op-ish write-barrier
        // form. References every arg so -Werror=unused does not fire; tolerates
        // the emitter passing &slot as slotPtr (reinterpret_cast<void**>) and an
        // XObject* OR a literal nullptr as newVal. NOTE (documented deviation):
        // newVal uses static_cast<void*>, NOT reinterpret_cast<void*>. The spec
        // text says reinterpret_cast<void*>, but the GC-emit phases also emit
        // XPACT_GC_STORE(parent, &slot, nullptr) (XIL2CPP.html line 1889), and
        // `reinterpret_cast<void*>(nullptr)` is ill-formed (no cast from
        // std::nullptr_t to void*). static_cast<void*> is valid for BOTH an
        // object pointer (XObject* -> void* is a standard conversion) AND a
        // literal nullptr, so it tolerates every documented call site. Routes
        // through XGC_WriteBarrier so the macro exercises the stub free function.
        sb.Append("#ifndef XPACT_GC_STORE\n");
        sb.Append("#define XPACT_GC_STORE(parent, slotPtr, newVal) \\\n");
        sb.Append("  do { (void)(parent); ::XCore::Reflect::XGC_WriteBarrier( \\\n");
        sb.Append("       reinterpret_cast<void**>(slotPtr), \\\n");
        sb.Append("       static_cast<void*>(newVal)); } while(0)\n");
        sb.Append("#endif\n");

        // XPACT_BACKEDGE_SAFEPOINT_CHECK(): alias of the (already no-op)
        // XPACT_SAFEPOINT_CHECK() the loop back-edge lowering (Phase 6.g) emits.
        sb.Append("#ifndef XPACT_BACKEDGE_SAFEPOINT_CHECK\n");
        sb.Append("#define XPACT_BACKEDGE_SAFEPOINT_CHECK() XPACT_SAFEPOINT_CHECK()\n");
        sb.Append("#endif\n");

        // ---- Container primary templates (prerequisite for the Phase 6.f
        //      container partial specializations). ----
        AppendContainerStub(sb);
    }

    /// <summary>
    /// Append the container-emit prerequisite stub surface the Phase 6.f
    /// container partial specializations
    /// (<see cref="Simgenics.XPact.XIL2CPP.Emit.Cpp.ContainerPartialSpecEmitter"/>)
    /// specialize / reference, per <c>/Documents/XIL2CPP.html</c> Rev 4
    /// Section 5.7 (container emit) + Section 6.2 (container roots):
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>The <c>::XCore::Container::TArray&lt;T&gt;</c> /
    ///     <c>TMap&lt;K,V&gt;</c> / <c>TSet&lt;T&gt;</c> primary templates.</b>
    ///     The container emitter emits an EXPLICIT specialization in-namespace
    ///     with the UNqualified <c>struct</c> name (FIX 1):
    ///     <c>namespace XCore { namespace Container { template &lt;&gt; struct TArray&lt;XPtr&lt;T'&gt;&gt; { ... }; } }</c>
    ///     of each; the primary template must exist for the specialization to
    ///     name. Each carries the member surface the specialization's ctor /
    ///     resize bodies call (<c>GetData()</c> / <c>Num()</c>), mirroring the
    ///     member names <see cref="Simgenics.XPact.XIL2CPP.Emit.Cpp.ContainerPartialSpecEmitter"/>
    ///     references.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Namespace-scope <c>GetData()</c> / <c>Num()</c> free functions.</b>
    ///     A C++ EXPLICIT (full) specialization does NOT inherit the primary
    ///     template's members, so the unqualified <c>GetData()</c> / <c>Num()</c>
    ///     calls in the specialization's own constructor would otherwise fail
    ///     name lookup. Enclosing-namespace free functions in
    ///     <c>::XCore::Container</c> satisfy the unqualified lookup so the
    ///     specialization body resolves.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The global <c>using ::XCore::Reflect::XPtr;</c> alias.</b> The
    ///     property / field emit renders an XObject slot as the UNQUALIFIED
    ///     <c>XPtr&lt;T&gt;</c> (PropertyEmitter), and the container emit names
    ///     the closed element slot unqualified too. The global using-declaration
    ///     makes <c>XPtr</c> visible by unqualified lookup from any namespace the
    ///     emitted code declares (e.g. <c>namespace Game</c>).
    ///   </description></item>
    ///   <item><description>
    ///     <b>The <c>::System::Collections::Generic::List</c> stub.</b> The
    ///     <c>.cs.h</c> field-storage emit renders a C# <c>List&lt;T&gt;</c>
    ///     field as the (template-argument-stripped) qualified name
    ///     <c>::System::Collections::Generic::List</c>; a non-template stub of
    ///     that exact name gives the header field a complete type.
    ///   </description></item>
    /// </list>
    /// Every addition is <c>#ifndef</c>-guarded + idempotent (additive: the
    /// pre-6.f gate output is unchanged when the module declares no container).
    /// </summary>
    /// <param name="sb">The umbrella-header string builder being assembled.</param>
    private static void AppendContainerStub(StringBuilder sb)
    {
        sb.Append('\n');
        sb.Append("// =====================================================================\n");
        sb.Append("// Container-emit prerequisite stubs (Phase 6.f container partial specs).\n");
        sb.Append("// The TArray/TMap/TSet primary templates the emitted explicit\n");
        sb.Append("// specializations name + the unqualified-lookup helpers their bodies use.\n");
        sb.Append("// =====================================================================\n");

        sb.Append("#ifndef XPACT_STUB_HAS_CONTAINERS\n");
        sb.Append("#define XPACT_STUB_HAS_CONTAINERS 1\n");
        sb.Append("namespace XCore { namespace Container {\n");

        // The TArray / TMap / TSet primary templates the explicit specializations
        // are OF. Member GetData()/Num() mirror the names the spec ctor/resize
        // bodies reference (the spec replaces the body; see the free functions).
        sb.Append("  template<typename T> struct TArray {\n");
        sb.Append("    T* GetData() noexcept { return nullptr; }\n");
        sb.Append("    unsigned long long Num() const noexcept { return 0; }\n");
        sb.Append("  };\n");
        sb.Append("  template<typename K, typename V> struct TMap {\n");
        sb.Append("    V* GetData() noexcept { return nullptr; }\n");
        sb.Append("    unsigned long long Num() const noexcept { return 0; }\n");
        sb.Append("  };\n");
        sb.Append("  template<typename T> struct TSet {\n");
        sb.Append("    T* GetData() noexcept { return nullptr; }\n");
        sb.Append("    unsigned long long Num() const noexcept { return 0; }\n");
        sb.Append("  };\n");

        // Enclosing-namespace fallbacks so the explicit specialization's own
        // ctor/resize bodies resolve their unqualified GetData()/Num() calls (a
        // full specialization does NOT inherit the primary template's members).
        sb.Append("  inline void* GetData() noexcept { return nullptr; }\n");
        sb.Append("  inline unsigned long long Num() noexcept { return 0; }\n");

        sb.Append("} }\n");
        sb.Append("#endif\n");

        // Global using so the UNQUALIFIED `XPtr<T>` the property / field / member
        // emit renders resolves from any emitted namespace (e.g. namespace Game).
        sb.Append("#ifndef XPACT_STUB_HAS_XPTR_USING\n");
        sb.Append("#define XPACT_STUB_HAS_XPTR_USING 1\n");
        sb.Append("using ::XCore::Reflect::XPtr;\n");
        sb.Append("#endif\n");

        // The BCL List stub the .cs.h field storage names (template args stripped
        // by the shell field-type renderer => the bare qualified name).
        sb.Append("#ifndef XPACT_STUB_HAS_SYSTEM_LIST\n");
        sb.Append("#define XPACT_STUB_HAS_SYSTEM_LIST 1\n");
        sb.Append("namespace System { namespace Collections { namespace Generic {\n");
        sb.Append("  struct List {};\n");
        sb.Append("} } }\n");
        sb.Append("#endif\n");

        // The container element-type element stubs. The 6.f container partial-spec
        // TU names its GC-rooted element by the UNqualified short type name the
        // Pass-3 emit signature carries (e.g. `XPtr<XActor>` for a C#
        // `List<XActor>`). A global-scope forward declaration makes that name
        // resolve from inside the `namespace XCore { namespace Container { ... } }`
        // the explicit specialization is wrapped in (unqualified lookup reaches
        // the global namespace). `XPtr<T>` only stores a `T*`, so an INCOMPLETE
        // `XActor` suffices for the `XPtr<XActor>` element + its `sizeof`.
        sb.Append("#ifndef XPACT_STUB_HAS_CONTAINER_ELEMENTS\n");
        sb.Append("#define XPACT_STUB_HAS_CONTAINER_ELEMENTS 1\n");
        sb.Append("struct XActor;\n");
        sb.Append("struct XPawn;\n");
        sb.Append("#endif\n");
    }

    /// <summary>
    /// The sizeof-pinned type names already given a hand-written stub (so the
    /// byte-array fallback does not re-define them): the FClass / lifecycle /
    /// XObject hand-stubs above, plus <c>XPtr</c>, which is now a CLASS TEMPLATE
    /// (declared in <see cref="AppendGcAbiStub"/>) -- a plain <c>struct XPtr</c>
    /// byte stub would clash with the template declaration AND could not satisfy
    /// the templated sizeof pin <c>sizeof(::XCore::Reflect::XPtr&lt;...&gt;)</c>.
    /// </summary>
    private static bool IsAlreadyDefinedReflectType(string type) => type switch
    {
        "FClass" or "FXObjectLifecycleTable" or "XObject" or "XPtr" => true,
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
