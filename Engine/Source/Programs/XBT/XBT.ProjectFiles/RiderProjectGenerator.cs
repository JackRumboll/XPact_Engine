// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ProjectFiles;

/// <summary>
/// Emits a minimal <c>.idea/</c> tree at <c>&lt;EngineRoot&gt;/.idea/</c>
/// per <c>/Documents/XBT.html</c> Rev 4 Section 14.2. Rider parses the
/// <c>.idea/</c> directory to drive its project view, indexing, and
/// build action. Per Section 14.4 the Build action delegates back to
/// <c>XBT.exe build</c> (mirrors UBT's pattern).
/// </summary>
/// <remarks>
/// <para>
/// <b>What we emit:</b>
/// </para>
/// <list type="bullet">
///   <item><c>.idea/modules.xml</c> -- the root module file listing
///   every <c>.iml</c> module.</item>
///   <item><c>.idea/&lt;ModuleName&gt;.iml</c> -- one per discovered
///   XPact module, with the include paths + defines for that module.</item>
///   <item><c>.idea/workspace.xml</c> -- minimal stub. Run / build
///   configurations are added by Rider on first user interaction.</item>
///   <item><c>.idea/.gitignore</c> -- the typical Rider per-project
///   gitignore (workspace.xml, shelf, etc.).</item>
/// </list>
/// <para>
/// <b>Determinism.</b> Every XML element's children are sorted by
/// name (or by the first attribute when name collides); indent is
/// 2-space; line endings are LF; the writer ends every file with a
/// single trailing newline. Two re-runs against identical inputs
/// produce byte-identical output.
/// </para>
/// </remarks>
public sealed class RiderProjectGenerator : IProjectFileGenerator
{
    /// <inheritdoc/>
    public string Name => "Rider";

    /// <summary>
    /// The fixed subdirectory Rider scans at the project root.
    /// </summary>
    public const string IdeaDirectoryName = ".idea";

    /// <inheritdoc/>
    public Task<GenerationResult> GenerateAsync(GenerationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        token.ThrowIfCancellationRequestedWithDiagnostic("RiderProjectGenerator.GenerateAsync");

        try
        {
            string ideaDir = Path.Combine(context.EngineRoot, IdeaDirectoryName);
            Directory.CreateDirectory(ideaDir);

            List<string> writtenFiles = new();

            // Sort modules alphabetically by name for stable output.
            List<ModuleRecord> sortedModules = context.Modules
                .OrderBy(r => r.Rules.Name, StringComparer.Ordinal)
                .ToList();

            // 1. modules.xml -- the root.
            string modulesXmlPath = Path.Combine(ideaDir, "modules.xml");
            WriteAtomic(modulesXmlPath, RenderModulesXml(sortedModules));
            writtenFiles.Add(modulesXmlPath);

            // 2. Per-module .iml files.
            foreach (ModuleRecord rec in sortedModules)
            {
                token.ThrowIfCancellationRequestedWithDiagnostic("RiderProjectGenerator.GenerateAsync.iml");
                string imlPath = Path.Combine(ideaDir, rec.Rules.Name + ".iml");
                WriteAtomic(imlPath, RenderImlFile(rec));
                writtenFiles.Add(imlPath);
            }

            // 3. workspace.xml -- minimal stub.
            string workspaceXmlPath = Path.Combine(ideaDir, "workspace.xml");
            WriteAtomic(workspaceXmlPath, RenderWorkspaceXml());
            writtenFiles.Add(workspaceXmlPath);

            // 4. .gitignore for the .idea tree.
            string gitignorePath = Path.Combine(ideaDir, ".gitignore");
            WriteAtomic(gitignorePath, RenderGitignore());
            writtenFiles.Add(gitignorePath);

            // Stable ordering of the result list (the WrittenFiles
            // contract is informational; we still keep it ordinal-sorted
            // so downstream consumers see deterministic order).
            writtenFiles.Sort(StringComparer.Ordinal);

            return Task.FromResult(new GenerationResult(
                Succeeded: true,
                WrittenFiles: writtenFiles));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new GenerationResult(
                Succeeded: false,
                WrittenFiles: Array.Empty<string>(),
                ErrorMessage: $"Rider .idea/ tree generation failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Render the root <c>modules.xml</c> file. Format per the
    /// JetBrains documentation -- one <c>&lt;module&gt;</c> element per
    /// <c>.iml</c> file, referenced by relative path.
    /// </summary>
    internal static string RenderModulesXml(IReadOnlyList<ModuleRecord> sortedModules)
    {
        // Build children in alphabetical order. Each <module> element
        // references the .iml by its filename relative to .idea/.
        List<XElement> moduleElements = new();
        foreach (ModuleRecord rec in sortedModules)
        {
            string imlRel = rec.Rules.Name + ".iml";
            // $PROJECT_DIR$ is the Rider placeholder for the project
            // root. Using it makes the .idea/ tree portable -- two
            // developers can check out the same repo at different
            // absolute paths and Rider resolves correctly.
            string imlPathExpr = "$PROJECT_DIR$/" + IdeaDirectoryName + "/" + imlRel;
            string filePathAttr = "file://" + imlPathExpr;

            XElement modElement = new("module",
                new XAttribute("fileurl", filePathAttr),
                new XAttribute("filepath", imlPathExpr));
            moduleElements.Add(modElement);
        }

        XElement modulesNode = new("modules", moduleElements.Cast<object>().ToArray());
        XElement componentNode = new("component",
            new XAttribute("name", "ProjectModuleManager"),
            modulesNode);
        XElement projectNode = new("project",
            new XAttribute("version", "4"),
            componentNode);

        return SerializeXml(projectNode);
    }

    /// <summary>
    /// Render one <c>.iml</c> file. Includes the module's source roots,
    /// include paths, and private definitions in the shape Rider parses.
    /// </summary>
    internal static string RenderImlFile(ModuleRecord rec)
    {
        // Source root: the directory containing the .Build.toml.
        string moduleDir = Path.GetDirectoryName(rec.DescriptorPath) ?? string.Empty;
        // The .iml expresses paths relative to its own location at
        // .idea/<Module>.iml; we use $MODULE_DIR$ placeholders Rider
        // resolves at load time. The module dir is recorded as the
        // canonical content root.
        string contentUrl = "file://$MODULE_DIR$/" + ToForwardSlash(moduleDir);

        // Build the content element with one sourceFolder child.
        XElement contentNode = new("content",
            new XAttribute("url", contentUrl),
            new XElement("sourceFolder",
                new XAttribute("url", contentUrl),
                new XAttribute("isTestSource", "false")));

        // NewModuleRootManager: the standard Rider module-root component.
        // Children: content + orderEntry (for the inherited JDK + dependent
        // module references) per the JetBrains documented shape.
        List<XElement> rootChildren = new() { contentNode };
        rootChildren.Add(new XElement("orderEntry", new XAttribute("type", "inheritedJdk")));
        rootChildren.Add(new XElement("orderEntry",
            new XAttribute("type", "sourceFolder"),
            new XAttribute("forTests", "false")));

        // Module dependencies (PublicDependencyModuleNames +
        // PrivateDependencyModuleNames). Sorted alphabetically for
        // determinism.
        SortedSet<string> dependencies = new(StringComparer.Ordinal);
        foreach (ModuleDep d in rec.Rules.PublicDependencyModuleNames)
        {
            dependencies.Add(d.Name);
        }
        foreach (ModuleDep d in rec.Rules.PrivateDependencyModuleNames)
        {
            dependencies.Add(d.Name);
        }
        foreach (string depName in dependencies)
        {
            rootChildren.Add(new XElement("orderEntry",
                new XAttribute("type", "module"),
                new XAttribute("module-name", depName)));
        }

        XElement moduleRootMgrNode = new("component",
            new XAttribute("name", "NewModuleRootManager"),
            rootChildren.Cast<object>().ToArray());

        // Custom XBT component carries include paths + definitions so a
        // future Rider plugin / external tool can read them without
        // re-parsing the .Build.toml. Format is informational; Rider
        // ignores unknown <component> elements.
        XElement xbtNode = RenderXBTComponent(rec);

        XElement moduleNode = new("module",
            new XAttribute("type", "CPP_MODULE"),
            new XAttribute("version", "4"),
            moduleRootMgrNode,
            xbtNode);

        return SerializeXml(moduleNode);
    }

    /// <summary>
    /// Custom XBT-specific component holding the module's include paths
    /// + definitions in a stable, parseable shape. Rider ignores
    /// <c>&lt;component&gt;</c> elements with unknown names.
    /// </summary>
    private static XElement RenderXBTComponent(ModuleRecord rec)
    {
        List<XElement> children = new();

        // Include paths, sorted alphabetically for determinism.
        List<string> includes = new();
        includes.AddRange(rec.Rules.PublicIncludePaths);
        includes.AddRange(rec.Rules.PrivateIncludePaths);
        includes.Sort(StringComparer.Ordinal);
        foreach (string inc in includes)
        {
            children.Add(new XElement("includePath", new XAttribute("path", ToForwardSlash(inc))));
        }

        // Definitions, sorted alphabetically for determinism.
        List<string> defs = new();
        defs.AddRange(rec.Rules.PublicDefinitions);
        defs.AddRange(rec.Rules.PrivateDefinitions);
        defs.Sort(StringComparer.Ordinal);
        foreach (string def in defs)
        {
            children.Add(new XElement("define", new XAttribute("value", def)));
        }

        // Module metadata (tier, type, simpath flag) -- informational.
        children.Add(new XElement("metadata",
            new XAttribute("tier", rec.Rules.Tier.ToString()),
            new XAttribute("moduleType", rec.Rules.ModuleType.ToString()),
            new XAttribute("simPath", rec.Rules.SimPath ? "true" : "false")));

        return new XElement("component",
            new XAttribute("name", "XBT.ModuleInfo"),
            children.Cast<object>().ToArray());
    }

    /// <summary>
    /// Minimal <c>workspace.xml</c> stub. Rider expands this file with
    /// run configurations, recent files, etc. on first interaction --
    /// we ship the minimum that lets Rider parse the directory.
    /// </summary>
    internal static string RenderWorkspaceXml()
    {
        XElement projectNode = new("project",
            new XAttribute("version", "4"));
        return SerializeXml(projectNode);
    }

    /// <summary>
    /// Typical Rider per-project <c>.gitignore</c>. The repo's root
    /// <c>.gitignore</c> covers most of <c>.idea/</c>; this per-project
    /// file documents the JetBrains exclusions for clarity.
    /// </summary>
    internal static string RenderGitignore()
    {
        // LF-terminated; trailing newline. Stable across re-runs.
        return string.Join('\n', new[]
        {
            "# Default ignored files by Rider per https://www.jetbrains.com/help/rider/Project_View.html",
            "/shelf/",
            "/workspace.xml",
            "# Editor-based HTTP Client requests",
            "/httpRequests/",
            "",
        });
    }

    /// <summary>
    /// Serialize <paramref name="root"/> with 2-space indent + LF line
    /// endings + trailing newline. The XML declaration is included
    /// because Rider's parser expects it; the declaration says
    /// <c>encoding="utf-8"</c> -- we write the file bytes as UTF-8
    /// without BOM so the declaration and the actual bytes agree.
    /// </summary>
    private static string SerializeXml(XElement root)
    {
        // Use a MemoryStream + UTF-8 (no BOM) writer so the emitted
        // declaration says <?xml version="1.0" encoding="utf-8" ...?>.
        // XmlWriter targeting a StringBuilder would default to utf-16
        // (mismatched with the UTF-8 bytes we write to disk) and would
        // break any downstream parser that honours the declaration.
        using MemoryStream memStream = new();
        XmlWriterSettings settings = new()
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using (XmlWriter writer = XmlWriter.Create(memStream, settings))
        {
            // Force the <?xml version="1.0" encoding="utf-8"?> emission.
            writer.WriteStartDocument();
            root.WriteTo(writer);
            writer.WriteEndDocument();
        }

        string xml = Encoding.UTF8.GetString(memStream.ToArray());
        // Replace CRLF on hypothetical Windows-newline emission.
        xml = xml.Replace("\r\n", "\n");
        if (!xml.EndsWith('\n'))
        {
            xml += '\n';
        }
        return xml;
    }

    /// <summary>
    /// Normalise backslashes to forward slashes for cross-platform
    /// Rider .idea/ files. Rider's parser accepts both but the
    /// JetBrains-emitted shape consistently uses forward slashes.
    /// </summary>
    private static string ToForwardSlash(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Write the file via temp-file + atomic rename per
    /// <c>/Documents/XBT.html</c> Rev 4 Section 6.4 so a killed XBT
    /// process never leaves a torn .idea file. UTF-8 without BOM, LF
    /// line endings.
    /// </summary>
    private static void WriteAtomic(string outputPath, string text)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = outputPath + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(tmpPath, bytes);
        File.Move(tmpPath, outputPath, overwrite: true);
    }
}
