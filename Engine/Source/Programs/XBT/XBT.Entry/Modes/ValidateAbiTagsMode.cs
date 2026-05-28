// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// XCore-4b Phase 4b.7 / Contract Rev 13.8 Stage B addendum:
/// <c>validate-abi-tags</c> mode. Walks the engine's runtime headers
/// + the XHT-emitted reflection output and verifies every contract-
/// frozen ABI layout tag + per-type sizeof claim is present and
/// agrees with <see cref="ContractSurface.AbiLayoutTags"/> +
/// <see cref="ContractSurface.AbiTypeSizes"/>.
/// </summary>
/// <remarks>
/// <para>
/// CLI surface (spec-canonical per XBT.html Section 1.2):
/// </para>
/// <list type="bullet">
///   <item><c>-EngineRoot=&lt;path&gt;</c> (optional) -- engine root
///   override. When unset, the mode walks up from CWD looking for
///   <c>Engine.xengine</c>.</item>
///   <item><c>-RuntimeHeaders=&lt;dir&gt;</c> (optional) -- override
///   the runtime-header scan root. Defaults to
///   <c>&lt;EngineRoot&gt;/Source/Runtime/XCore/Public/Reflection</c>.</item>
///   <item><c>-GeneratedRoots=&lt;p1&gt;[,&lt;p2&gt;,...]</c>
///   (optional, repeatable) -- one or more XHT-output roots to walk
///   for <c>.gen.cpp</c> files. When unset, the mode runs in
///   "runtime-headers-only" mode (the generated output may not exist
///   on a fresh checkout that has not yet built).</item>
/// </list>
/// <para>
/// Exit codes: 0 on success; 50 (<c>ManifestMalformed</c>) when any
/// runtime header is missing an ABI tag or carries the wrong sizeof
/// claim; 10 on CLI argument error.
/// </para>
/// <para>
/// The validator is two-layered:
/// </para>
/// <list type="number">
///   <item><b>Runtime-header sweep.</b> Walks every
///   <c>F*.h</c> + <c>XReflectionRuntime.h</c> under the runtime root,
///   reads each file, and verifies (a) the
///   <c>XPACT_*_LAYOUT_TAG</c> macros named in
///   <see cref="ContractSurface.AbiLayoutTags"/> are defined to the
///   exact contract-frozen content string, and (b) the static_assert
///   line for each <see cref="ContractSurface.AbiTypeSizes"/> entry
///   exists with the correct byte total.</item>
///   <item><b>Generated-output sweep (optional).</b> When the caller
///   passes <c>-GeneratedRoots</c>, walks every <c>.gen.cpp</c> file
///   under those roots and confirms each XHT-emitted file carries the
///   per-TU pin block (the runtime-header sweep alone proves the
///   contract is honoured by the runtime; the generated-output sweep
///   proves XHT actually emits the pin instances).</item>
/// </list>
/// <para>
/// Per XCore-4b Section 9.4 hot-reload-safety: this mode catches the
/// "developer edits FProperty.h to add a member without bumping the
/// contract" footgun at standalone-tool runtime (in addition to the
/// per-TU compile-time static_asserts that fire when a consumer
/// rebuilds).
/// </para>
/// </remarks>
[XBTMode("validate-abi-tags")]
public sealed class ValidateAbiTagsMode : IToolMode<ValidateAbiTagsMode>
{
    public static string Name => "validate-abi-tags";

    public static string Description =>
        "Verify XCore-4b reflection-type ABI layout tags + sizeof pins against Contract Rev 13.8.";

    /// <summary>
    /// Exit code on ABI-tag mismatch. Aliased to
    /// <see cref="ExitCodes.ManifestMalformed"/> (50) per Contract
    /// Rev 13.8 Section 13: an ABI-tag mismatch is structurally
    /// equivalent to a manifest-malformed condition (the manifest's
    /// ContractVersion implicitly promises every macro / sizeof named
    /// in the surface, and a mismatch breaks that promise).
    /// </summary>
    public const int AbiTagMismatchExitCode = 50;

    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("ValidateAbiTagsMode.ExecuteAsync");

        try
        {
            ValidateOptions options = ValidateOptions.Parse(args);
            string engineRoot = options.EngineRoot ?? DiscoverEngineRoot();
            string runtimeHeaders = options.RuntimeHeaders
                ?? Path.Combine(engineRoot, "Source", "Runtime", "XCore", "Public", "Reflection");
            // XCoreXObject Phase 5.a addendum: the XObject base type +
            // FXObjectArrayEntry static_asserts live under
            // Public/XObject/ (the new System-5 sub-area). The
            // validator must scan both Reflection/ AND XObject/ to
            // find every pin contributed by Contract Rev 13.9. When
            // the caller passes -RuntimeHeaders=<dir> the override is
            // used as-is (the caller knows what they're doing); the
            // default broadens to scan both subtrees.
            string xobjectHeaders = options.RuntimeHeaders
                ?? Path.Combine(engineRoot, "Source", "Runtime", "XCore", "Public", "XObject");
            string xreflectionRuntimeH = Path.Combine(
                engineRoot, "Source", "Runtime", "XCore", "Public", "XReflectionRuntime.h");

            List<string> failures = new();

            // Layer 1: runtime-header sweep. Every contract-frozen tag
            // must appear in XReflectionRuntime.h (the canonical home
            // for the XPACT_*_LAYOUT_TAG macros) AND every contract-
            // frozen sizeof must appear in one of the F*.h headers in
            // the reflection root OR in the new XObject/ sub-area
            // (XCoreXObject Phase 5.a).
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRuntimeMacros(xreflectionRuntimeH, failures);

            cancellationToken.ThrowIfCancellationRequested();
            ValidateRuntimeSizeofs(runtimeHeaders, xobjectHeaders, failures);

            // Layer 2 (optional): generated-output sweep. The caller
            // points us at one or more XHT-output roots; we walk every
            // .gen.cpp and confirm the pin block is present.
            if (options.GeneratedRoots.Count > 0)
            {
                foreach (string root in options.GeneratedRoots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateGeneratedOutput(root, failures);
                }
            }

            if (failures.Count == 0)
            {
                Logger.Info(
                    "validate-abi-tags: all "
                    + ContractSurface.AbiLayoutTags.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " layout tags and "
                    + ContractSurface.AbiTypeSizes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " sizeof pins match Contract Rev 13.8 (XCore-4b Stage B addendum).",
                    new DiagnosticContext { Action = "validate-abi-tags" });
                return Task.FromResult(0);
            }

            StringBuilder sb = new();
            sb.AppendLine("validate-abi-tags: " + failures.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " ABI-tag / sizeof drift(s) detected against Contract Rev 13.8:");
            foreach (string f in failures)
            {
                sb.Append("  ").AppendLine(f);
            }
            Logger.Error(
                sb.ToString().TrimEnd(),
                exitCode: AbiTagMismatchExitCode,
                new DiagnosticContext { Action = "validate-abi-tags" });
            return Task.FromResult(AbiTagMismatchExitCode);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
    }

    /// <summary>
    /// Layer-1a: verify XReflectionRuntime.h carries every
    /// <c>#define XPACT_*_LAYOUT_TAG</c> in the contract surface with
    /// the exact frozen content string.
    /// </summary>
    private static void ValidateRuntimeMacros(string xrtH, List<string> failures)
    {
        if (!File.Exists(xrtH))
        {
            failures.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "XReflectionRuntime.h not found at expected location '{0}'.",
                xrtH));
            return;
        }

        string text = File.ReadAllText(xrtH);
        foreach ((string macro, string content) in ContractSurface.AbiLayoutTags)
        {
            // Find: #define <macro> "<content>"
            // The macro may span multiple lines with backslash
            // continuations; we collapse whitespace before matching the
            // string-literal body.
            string collapsed = CollapseLineContinuations(text);
            string needle = "#define " + macro;
            int idx = collapsed.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0)
            {
                failures.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "XReflectionRuntime.h is missing '#define {0}' (Contract Rev 13.8 §11.6).",
                    macro));
                continue;
            }

            // Extract the body after #define <macro> up to the next
            // newline; the body must contain the contract-frozen string
            // literal verbatim. We compare via a substring check on the
            // unescaped form because the macro's line-broken expansion
            // may insert whitespace.
            int newlineIdx = collapsed.IndexOf('\n', idx);
            if (newlineIdx < 0) { newlineIdx = collapsed.Length; }
            string body = collapsed.Substring(idx + needle.Length, newlineIdx - idx - needle.Length);

            // The macro body should be: <ws>"<content>"
            string expectedLiteral = "\"" + content + "\"";
            if (!body.Contains(expectedLiteral, StringComparison.Ordinal))
            {
                failures.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "XReflectionRuntime.h '#define {0}' content drifted from Contract Rev 13.8. "
                    + "Expected literal: {1}",
                    macro,
                    expectedLiteral));
            }
        }
    }

    /// <summary>
    /// Layer-1b: verify every contract-frozen sizeof claim appears in
    /// the corresponding reflection-runtime header as a
    /// <c>static_assert(sizeof(TypeName) == N, ...)</c> line. The walk
    /// is over the entire Reflection/ subtree because the static
    /// asserts may live in any file (e.g. <c>FName.h</c> for FName,
    /// <c>FClass.h</c> for FClass).
    /// </summary>
    private static void ValidateRuntimeSizeofs(string reflectionDir, string xobjectDir, List<string> failures)
    {
        if (!Directory.Exists(reflectionDir))
        {
            failures.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Reflection header directory not found at expected location '{0}'.",
                reflectionDir));
            return;
        }

        // Build a combined view of every .h file's text so the lookup
        // is one substring probe per (TypeName, bytes) pair.
        // XCoreXObject Phase 5.a addendum: pull from Public/Reflection/
        // AND Public/XObject/ so the XObject + FXObjectArrayEntry
        // static_asserts (which live in the new XObject/ sub-area) are
        // discoverable.
        StringBuilder combined = new();
        foreach (string file in Directory.EnumerateFiles(reflectionDir, "*.h", SearchOption.TopDirectoryOnly))
        {
            combined.Append(File.ReadAllText(file));
            combined.Append('\n');
        }
        if (Directory.Exists(xobjectDir))
        {
            foreach (string file in Directory.EnumerateFiles(xobjectDir, "*.h", SearchOption.TopDirectoryOnly))
            {
                combined.Append(File.ReadAllText(file));
                combined.Append('\n');
            }
        }
        string allHeaders = combined.ToString();

        foreach ((string type, int bytes) in ContractSurface.AbiTypeSizes)
        {
            // XCoreXObject Phase 5.a addendum: skip types whose
            // implementation is deferred to a later XCoreXObject
            // phase. The contract entry is RETAINED (the canonical
            // surface bytes + StructureHash depend on it), but the
            // runtime static_assert is not yet shippable. The phase
            // landing the type REMOVES it from
            // AbiTypesDeferredUntilPhase, at which point the
            // validator starts enforcing the pin.
            if (ContractSurface.AbiTypesDeferredUntilPhase.TryGetValue(type, out string? deferralReason))
            {
                Logger.Info(
                    "validate-abi-tags: skipping '" + type + "' sizeof pin "
                    + "(deferred until " + deferralReason + "). The contract "
                    + "entry is retained for StructureHash stability; the "
                    + "static_assert will be enforced once the type ships.",
                    new DiagnosticContext { Action = "validate-abi-tags" });
                continue;
            }

            // Match patterns like
            //   static_assert(sizeof(FName)  == 8,
            //   static_assert(sizeof(FProperty) == 104,
            // The exact whitespace between sizeof, the type, and ==
            // varies across the corpus, so we probe for the
            // canonical-ish "sizeof(TYPE)" + "== N" pair separately.
            //
            // Phase 5.a robustness: a `sizeof(FStruct)` mention in a
            // comment that quotes a HISTORICAL size (e.g.,
            // "// sizeof(FStruct) == 112." documenting the Phase 4b.5
            // baseline before the Rev 13.9 cascade) would have made
            // the first-match probe return the historical comment.
            // The fix: anchor the probe at `static_assert(sizeof(TYPE)`
            // so only real assert sites match.
            string staticAssertProbe = "static_assert(sizeof(" + type + ")";
            int sizeofIdx = allHeaders.IndexOf(staticAssertProbe, StringComparison.Ordinal);
            if (sizeofIdx < 0)
            {
                failures.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "No 'static_assert(sizeof({0}) == ...)' found in Reflection / XObject headers (Contract Rev 13.9 §11.2 / §11.3).",
                    type));
                continue;
            }

            // Look forward from sizeofIdx for the matching == N value;
            // bound the scan at 80 chars to keep the probe local to the
            // static_assert line.
            int windowEnd = System.Math.Min(allHeaders.Length, sizeofIdx + 80);
            string window = allHeaders.Substring(sizeofIdx, windowEnd - sizeofIdx);
            string eqProbe = "== " + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!window.Contains(eqProbe, StringComparison.Ordinal))
            {
                failures.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "static_assert(sizeof({0})) value drifted from Contract Rev 13.9: "
                    + "expected '{1}' near the assert site.",
                    type,
                    eqProbe));
            }
        }
    }

    /// <summary>
    /// Layer 2 (optional): verify XHT-emitted <c>.gen.cpp</c> files
    /// carry the per-TU pin block. We don't re-check every individual
    /// pin line (that would duplicate the XHT emit-test coverage);
    /// instead we confirm the marker comments that anchor the pin
    /// block are present + the guarded layout-tag region exists.
    /// </summary>
    private static void ValidateGeneratedOutput(string root, List<string> failures)
    {
        if (!Directory.Exists(root))
        {
            failures.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Generated-output root not found: '{0}'.",
                root));
            return;
        }

        const string anchorComment =
            "// XCore-4b Stage B addendum ABI layout pins (Contract Rev 13.8";
        const string sizeofAnchor =
            "// Per-type sizeof pins per XCore-4b Rev 4 §11.2 + §11.3";

        int filesScanned = 0;
        foreach (string genCpp in Directory.EnumerateFiles(root, "*.gen.cpp", SearchOption.AllDirectories))
        {
            filesScanned++;
            string text = File.ReadAllText(genCpp);
            if (!text.Contains(anchorComment, StringComparison.Ordinal))
            {
                failures.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "XHT-emitted '{0}' is missing the XCore-4b layout-tag pin block (anchor: {1}).",
                    genCpp,
                    anchorComment));
                continue;
            }
            if (!text.Contains(sizeofAnchor, StringComparison.Ordinal))
            {
                failures.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "XHT-emitted '{0}' is missing the XCore-4b sizeof pin block (anchor: {1}).",
                    genCpp,
                    sizeofAnchor));
            }
        }

        if (filesScanned == 0)
        {
            // Empty .gen.cpp roots are non-fatal: an XHT-output root
            // that has not been written yet (fresh build) is a
            // legitimate state. The runtime-headers sweep is enough
            // for the contract integrity check.
            Logger.Info(
                "validate-abi-tags: generated-output root '" + root
                + "' contained no .gen.cpp files (skipped).",
                new DiagnosticContext { Action = "validate-abi-tags" });
        }
    }

    /// <summary>
    /// Collapse C-style backslash-newline line continuations so a
    /// multi-line <c>#define</c> body shows up as one logical line for
    /// the substring probe. Preserves byte offsets that aren't on a
    /// continuation by leaving non-continued newlines intact.
    /// </summary>
    private static string CollapseLineContinuations(string text)
    {
        // The common form is:
        //   #define MACRO \
        //       "literal"
        // After collapse: #define MACRO     "literal"
        // We accept the LF + optional CR shapes; the surface mostly
        // produces LF since the XHT emitter is LF-only, but the
        // runtime headers come from MSVC's editor which may insert
        // CRLF -- the AtomicFile writes are LF-only but the runtime
        // headers are hand-authored and may not be.
        if (text.IndexOf('\\') < 0)
        {
            return text;
        }
        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r'))
            {
                // Skip backslash + the following newline (and the LF
                // that follows a CR for CRLF). Replace with a single
                // space so adjacent tokens don't fuse.
                sb.Append(' ');
                i++;  // skip the newline char
                if (i < text.Length - 1 && text[i] == '\r' && text[i + 1] == '\n')
                {
                    i++;
                }
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private static string DiscoverEngineRoot()
    {
        string? cursor = Environment.CurrentDirectory;
        while (!string.IsNullOrEmpty(cursor))
        {
            string candidate = Path.Combine(cursor, "Engine", "Engine.xengine");
            if (File.Exists(candidate))
            {
                return Path.Combine(cursor, "Engine");
            }
            candidate = Path.Combine(cursor, "Engine.xengine");
            if (File.Exists(candidate))
            {
                return cursor;
            }
            DirectoryInfo? parent = Directory.GetParent(cursor);
            if (parent is null)
            {
                break;
            }
            cursor = parent.FullName;
        }
        throw new XBTException(
            "Could not discover the engine root. Pass -EngineRoot=<path> or run xbt from inside the repo.",
            exitCode: 10);
    }

    private sealed record ValidateOptions
    {
        public string? EngineRoot { get; init; }
        public string? RuntimeHeaders { get; init; }
        public required IReadOnlyList<string> GeneratedRoots { get; init; }

        public static ValidateOptions Parse(string[] args)
        {
            string? engine = null;
            string? runtimeHeaders = null;
            List<string> generated = new();

            foreach (string arg in args)
            {
                if (arg.StartsWith("-EngineRoot=", StringComparison.OrdinalIgnoreCase))
                {
                    engine = arg["-EngineRoot=".Length..];
                }
                else if (arg.StartsWith("-RuntimeHeaders=", StringComparison.OrdinalIgnoreCase))
                {
                    runtimeHeaders = arg["-RuntimeHeaders=".Length..];
                }
                else if (arg.StartsWith("-GeneratedRoots=", StringComparison.OrdinalIgnoreCase))
                {
                    string raw = arg["-GeneratedRoots=".Length..];
                    foreach (string piece in raw.Split(','))
                    {
                        string trimmed = piece.Trim();
                        if (trimmed.Length > 0) { generated.Add(trimmed); }
                    }
                }
                else
                {
                    throw new XBTException($"Unknown argument '{arg}'.", exitCode: 10);
                }
            }

            return new ValidateOptions
            {
                EngineRoot = engine,
                RuntimeHeaders = runtimeHeaders,
                GeneratedRoots = generated,
            };
        }
    }
}
