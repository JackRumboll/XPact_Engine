// Copyright Simgenics. All Rights Reserved.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// Per-output content-hash cache. Maps every produced
/// <see cref="FileItem"/> to an <see cref="IoHash"/> that combines the
/// producer's <see cref="IExternalAction.CommandVersion"/>, its response
/// file contents, its <see cref="IExternalAction.CacheKeyComponents"/>,
/// and the relevant environment (toolchain version, contract version,
/// SDK version, architecture, FIPS mode, station role, sim-path policy
/// file hashes).
/// </summary>
/// <remarks>
/// <para>
/// Storage: partitioned binary archive at
/// <c>Intermediate/Build/&lt;Target&gt;/&lt;Config&gt;/ActionHistory.bin</c>.
/// One partition per first byte of the FileItem path hash: low-contention
/// parallel writes; cheap lookup. The on-disk format is a plain little-
/// endian record stream framed by a partition header.
/// </para>
/// <para>
/// <see cref="CurrentVersion"/> is auto-derived from a BLAKE3 hash of the
/// <see cref="IExternalAction"/> property set (footgun #8 preempt per
/// XBT.html Section 5.4). The hash is computed once at static-init from
/// reflection over <see cref="IExternalAction"/>; any schema change to the
/// interface produces a new <see cref="CurrentVersion"/>, automatically
/// invalidating cached entries. The reserved <see cref="XActionType"/>
/// slots (9-15) preserve the hash across Phase 2 enum additions.
/// </para>
/// <para>
/// Atomic write: <see cref="Save"/> writes to a sibling
/// <c>.tmp.&lt;pid&gt;.&lt;actionid&gt;</c> file and renames into place,
/// matching the cancellation + atomic-rename contract in XBT.html Section
/// 6.4. A torn write (process killed mid-write) leaves the previous
/// archive intact on disk.
/// </para>
/// </remarks>
public sealed class ActionHistory
{
    /// <summary>
    /// Partition count -- one slot per high byte of the FileItem path
    /// hash. 256 is small enough that the in-memory dictionary cost is
    /// negligible (one Dictionary per partition); large enough that
    /// per-partition contention is rare under any plausible build size.
    /// </summary>
    public const int PartitionCount = 256;

    /// <summary>Magic prefix on the file header for format identification.</summary>
    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("XAH1");

    /// <summary>
    /// File format version. Bumped on any breaking format change. Audit
    /// fix M3 bumped from 1 to 2 to add the parallel content-hash map.
    /// Version-1 archives loaded under v2 deserialise the producer-key
    /// map only and leave the content-hash map empty; the next save
    /// rewrites the archive in the v2 layout.
    /// </summary>
    private const int FormatVersion = 2;

    /// <summary>
    /// Auto-derived BLAKE3 of the <see cref="IExternalAction"/> property
    /// set (names + types in declaration order). Computed once at first
    /// access via reflection.
    /// </summary>
    public static IoHash CurrentVersion => s_currentVersion.Value;

    private static readonly Lazy<IoHash> s_currentVersion = new(ComputeCurrentVersion);

    private readonly string _archivePath;
    private readonly Partition[] _partitions = new Partition[PartitionCount];

    private ActionHistory(string archivePath)
    {
        _archivePath = archivePath;
        for (int i = 0; i < PartitionCount; i++)
        {
            _partitions[i] = new Partition();
        }
    }

    /// <summary>
    /// Open the action-history archive for the given target/configuration.
    /// The archive lives at
    /// <c>&lt;targetDir&gt;/&lt;config&gt;/ActionHistory.bin</c>; if the
    /// file does not exist a fresh empty archive is returned.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="targetDir"/> is null.</exception>
    public static ActionHistory Open(string targetDir, BuildConfiguration config)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);

        Directory.CreateDirectory(targetDir);
        string configDir = Path.Combine(targetDir, config.ToString());
        Directory.CreateDirectory(configDir);

        string path = Path.Combine(configDir, "ActionHistory.bin");
        ActionHistory history = new(path);
        history.LoadFromDisk();
        return history;
    }

    /// <summary>
    /// Test hook: open an action-history archive at an arbitrary path
    /// without imposing the <c>&lt;targetDir&gt;/&lt;config&gt;/</c>
    /// layout. Tests use this to land an archive in a per-test scratch
    /// directory.
    /// </summary>
    internal static ActionHistory OpenAtPath(string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        ActionHistory history = new(archivePath);
        history.LoadFromDisk();
        return history;
    }

    /// <summary>
    /// Compute the per-action stored hash from the action's
    /// <see cref="IExternalAction.CommandVersion"/>, response file
    /// contents, cache-key components, and the rolling
    /// <see cref="CurrentVersion"/>. The result is what
    /// <see cref="RecordHash"/> persists.
    /// </summary>
    public static IoHash ComputeActionKey(IExternalAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using Hasher hasher = Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // 1. CurrentVersion. Any schema change to IExternalAction
        //    invalidates every cached entry.
        Span<byte> versionBytes = stackalloc byte[IoHash.Length];
        CurrentVersion.CopyTo(versionBytes);
        hasher.Update(versionBytes);

        // 2. The action's own CommandVersion (already a BLAKE3 of its
        //    command-line + arguments + working dir + cache-key
        //    components, per ExternalAction.ComputeCommandVersion).
        Span<byte> commandBytes = stackalloc byte[IoHash.Length];
        action.CommandVersion.CopyTo(commandBytes);
        hasher.Update(commandBytes);

        // 3. The ContractVersion -- when the contract surface changes,
        //    every action's stored key invalidates as well, because the
        //    semantics of the same command-line may have shifted.
        byte[] contractBytes = Encoding.UTF8.GetBytes(ContractVersion.Current);
        BitConverter.TryWriteBytes(intBuffer, contractBytes.Length);
        hasher.Update(intBuffer);
        hasher.Update(contractBytes);

        // 4. ResponseFileContents (nullable, length-prefixed).
        if (action.ResponseFileContents is null)
        {
            BitConverter.TryWriteBytes(intBuffer, -1);
            hasher.Update(intBuffer);
        }
        else
        {
            byte[] rspBytes = Encoding.UTF8.GetBytes(action.ResponseFileContents);
            BitConverter.TryWriteBytes(intBuffer, rspBytes.Length);
            hasher.Update(intBuffer);
            hasher.Update(rspBytes);
        }

        // 5. CacheKeyComponents (ordered).
        BitConverter.TryWriteBytes(intBuffer, action.CacheKeyComponents.Count);
        hasher.Update(intBuffer);
        foreach (string component in action.CacheKeyComponents)
        {
            byte[] componentBytes = Encoding.UTF8.GetBytes(component);
            BitConverter.TryWriteBytes(intBuffer, componentBytes.Length);
            hasher.Update(intBuffer);
            hasher.Update(componentBytes);
        }

        Span<byte> digest = stackalloc byte[IoHash.Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }

    /// <summary>
    /// Return the previously-stored hash for <paramref name="file"/>, or
    /// <see cref="IoHash.Zero"/> when no entry exists.
    /// </summary>
    public IoHash GetStoredHash(FileItem file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Partition partition = _partitions[PartitionOf(file.FullPath)];
        return partition.TryGet(file.FullPath, out IoHash hash) ? hash : IoHash.Zero;
    }

    /// <summary>
    /// Record the hash for a produced file. Concurrent calls to different
    /// partitions are lock-free; concurrent calls into the same partition
    /// serialize on a per-partition lock.
    /// </summary>
    public void RecordHash(FileItem file, IoHash hash)
    {
        ArgumentNullException.ThrowIfNull(file);
        Partition partition = _partitions[PartitionOf(file.FullPath)];
        partition.Set(file.FullPath, hash);
    }

    /// <summary>
    /// True iff the action's outputs are stale. An action is outdated when:
    /// <list type="number">
    ///   <item>Any <see cref="IExternalAction.ProducedItems"/> file does not exist on disk; OR</item>
    ///   <item>The action's <see cref="ComputeActionKey"/> differs from any produced item's stored hash; OR</item>
    ///   <item>Any raw-source prereq's recorded content hash differs from its live content hash; OR</item>
    ///   <item>Any header recorded by <see cref="CppDependencyCache"/> for the action's source has a recorded content hash that differs from its live content hash (audit fix R3-C1).</item>
    /// </list>
    /// Returns false (= up-to-date) only when every produced item exists,
    /// each carries the same recorded key, every raw-source prereq's
    /// content hash is unchanged, and every transitively-included header
    /// recorded by the dependency cache is also unchanged.
    /// </summary>
    public bool IsActionOutdated(LinkedAction linked)
        => IsActionOutdated(linked, cppDependencyCache: null);

    /// <summary>
    /// Audit fix R3-C1: cache-aware overload. When
    /// <paramref name="cppDependencyCache"/> is non-null and the action
    /// has a recorded dependency set, each recorded header's content
    /// hash is consulted as a fourth invalidation signal. Editing a
    /// transitively-included header that is NOT in
    /// <see cref="IExternalAction.PrerequisiteItems"/> (because the PCH
    /// did not include it) now correctly invalidates the cached output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without the cache lookup, the action graph would only invalidate
    /// when the source's own content changed or when the PCH artefact
    /// changed -- which is the silent-staleness bug the Round-3 audit
    /// surfaced and this overload closes. The recorded headers are
    /// produced by <see cref="CppDependencyCache.ParseDepfile"/> from
    /// the toolchain's <c>.d</c> / <c>.deps.json</c> emit and committed
    /// post-success by <see cref="ParallelExecutor"/>.
    /// </para>
    /// </remarks>
    public bool IsActionOutdated(LinkedAction linked, CppDependencyCache? cppDependencyCache)
    {
        ArgumentNullException.ThrowIfNull(linked);
        IExternalAction action = linked.Action;

        // 1. Every ProducedItem must exist on disk.
        foreach (FileItem produced in action.ProducedItems)
        {
            if (!File.Exists(produced.FullPath))
            {
                return true;
            }
        }

        // 2. The action's current key.
        IoHash currentKey = ComputeActionKey(action);

        // 3. Every ProducedItem's stored key must equal currentKey.
        foreach (FileItem produced in action.ProducedItems)
        {
            IoHash stored = GetStoredHash(produced);
            if (stored == IoHash.Zero || stored != currentKey)
            {
                return true;
            }
        }

        // 4. Audit fix M3: every raw-source prereq's recorded content
        //    hash must match the live file's hash. Producer-intermediate
        //    prereqs are NOT consulted here (the producer's own action
        //    key was already verified in rule 3 via its own
        //    IsActionOutdated when its turn came around upstream of us
        //    in the topo order). The dedicated content-hash map ensures
        //    we don't confuse an action key with a content hash; the
        //    previous Rev 13 behaviour compared the stored action key
        //    against the live content hash, which never matched and
        //    always forced re-runs.
        foreach (FileItem prereq in action.PrerequisiteItems)
        {
            IoHash recordedContent = GetStoredContentHash(prereq);
            if (recordedContent == IoHash.Zero)
            {
                continue;
            }
            if (!File.Exists(prereq.FullPath))
            {
                return true;
            }
            IoHash live = prereq.ContentHash;
            if (live != recordedContent)
            {
                return true;
            }
        }

        // 5. Audit fix R3-C1: every header recorded by the dependency
        //    cache for the action's source must also still match its
        //    recorded content hash. The action's source is the first
        //    PrerequisiteItem by convention (toolchains always pass the
        //    source first; PCH artefacts and depfile sit alongside);
        //    we look up the recorded headers for every prereq and union
        //    them so the check is robust to action shapes that do not
        //    have a singleton source.
        if (cppDependencyCache is not null)
        {
            foreach (FileItem prereq in action.PrerequisiteItems)
            {
                IReadOnlyList<FileItem>? recorded =
                    cppDependencyCache.GetRecordedDependencies(prereq.FullPath);
                if (recorded is null)
                {
                    continue;
                }
                foreach (FileItem header in recorded)
                {
                    IoHash recordedContent = GetStoredContentHash(header);
                    if (recordedContent == IoHash.Zero)
                    {
                        // Header was recorded as part of the dep set but
                        // its content hash was not committed (the record
                        // call lost a race, or the previous build was
                        // killed between the dep-set record and the
                        // content-hash record). Conservatively force a
                        // rebuild so the cache catches up; the rebuild
                        // is one-time per such header.
                        return true;
                    }
                    if (!File.Exists(header.FullPath))
                    {
                        // Header recorded as a transitive dep is gone --
                        // the source's include graph has shifted and we
                        // need to recompile to learn the new shape.
                        return true;
                    }
                    IoHash live = header.ContentHash;
                    if (live != recordedContent)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Audit fix M3: read the content-hash entry for a raw-source
    /// prerequisite. Returns <see cref="IoHash.Zero"/> when no entry
    /// exists. Separate from <see cref="GetStoredHash"/> (which returns
    /// the producer-key for produced intermediates).
    /// </summary>
    public IoHash GetStoredContentHash(FileItem file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Partition partition = _partitions[PartitionOf(file.FullPath)];
        return partition.TryGetContentHash(file.FullPath, out IoHash hash) ? hash : IoHash.Zero;
    }

    /// <summary>
    /// Audit fix M3: record a raw-source prerequisite's content hash.
    /// Distinct from <see cref="RecordHash"/> which records a producer's
    /// action key against its produced item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit fix R6-C1: wired into <see cref="ParallelExecutor"/>'s
    /// post-action-success hook. After a successful action, the
    /// executor walks <see cref="IExternalAction.PrerequisiteItems"/>
    /// and calls <see cref="RecordContentHash"/> for each prereq whose
    /// path does NOT appear in the action graph's producer map (i.e.,
    /// the raw-source files the build consumes, not the intermediate
    /// outputs another action produced). The producer-output path is
    /// already covered by <see cref="RecordHash"/>; recording a content
    /// hash for it would be redundant.
    /// </para>
    /// <para>
    /// With both maps populated, <see cref="IsActionOutdated"/> rule 4
    /// becomes load-bearing: a raw-source edit invalidates downstream
    /// actions through the content-hash compare, not through the
    /// FileItem mtime check (which the contract bans). This is the
    /// Phase-1 wire-up for what becomes the Phase 2
    /// XPactBuildAccelerator's content-addressable cache key per
    /// prerequisite.
    /// </para>
    /// </remarks>
    public void RecordContentHash(FileItem file, IoHash hash)
    {
        ArgumentNullException.ThrowIfNull(file);
        Partition partition = _partitions[PartitionOf(file.FullPath)];
        partition.SetContentHash(file.FullPath, hash);
    }

    /// <summary>
    /// Persist the archive to disk. Writes to a sibling
    /// <c>.tmp.&lt;pid&gt;.&lt;guid&gt;</c> file first and renames into
    /// place per <c>/Documents/XBT.html</c> Section 6.4 atomic-rename
    /// contract. A torn write leaves the previous archive intact on
    /// disk. The temp-file suffix is a GUID rather than a timestamp
    /// so the reproducibility envelope (Toolchain Contract Rev 13
    /// Section 2.1) is honoured -- no clock values leak into any
    /// artefact name.
    /// </summary>
    public void Save()
    {
        // Write all partitions into a single contiguous file. The file
        // layout is:
        //     [magic "XAH1" : 4]
        //     [format version : 4]
        //     [current version digest : 32]
        //     [partition count : 4]                  -- equals PartitionCount
        //     for each partition:
        //         [entry count : 4]
        //         for each entry:
        //             [path UTF-8 length : 4]
        //             [path UTF-8 bytes : N]
        //             [hash : 32]
        // Temp-file naming: <pid>.<guid> -- no timestamp anywhere.
        // Per Toolchain Contract Rev 13 Section 2.1 the
        // reproducibility envelope bans timestamps from any artefact
        // name engine-wide (footgun #1 preempt); a GUID gives the
        // collision-avoidance the temp-file naming convention
        // requires without leaking a clock value. Mirrors the
        // BuildCsCompiler.WriteAtomically pattern.
        string directory = Path.GetDirectoryName(_archivePath)!;
        int pid = Environment.ProcessId;
        string nonce = Guid.NewGuid().ToString("N");
        string tempPath = Path.Combine(directory, $"ActionHistory.bin.tmp.{pid}.{nonce}");

        // Audit fix R7-M3: snapshot every partition in parallel BEFORE
        // taking the file-write lock. Each per-partition snapshot
        // serialises with the writer that may be racing it (per-
        // partition mutex inside Partition.Snapshot), so dispatching
        // the snapshots in parallel doesn't change the cross-
        // partition contention; it just hides per-partition lock
        // wait under wall-clock concurrency. The final write to disk
        // is still serial because BinaryWriter is not thread-safe
        // and the on-disk format is a single contiguous stream.
        //
        // Phase 2 TODO: when the per-partition split lands (one
        // file per partition under <ConfigDir>/ActionHistory/), the
        // write itself can also parallelize. The single-file format
        // here keeps the cross-config disk footprint small.
        IReadOnlyDictionary<string, IoHash>[] producerSnapshots =
            new IReadOnlyDictionary<string, IoHash>[PartitionCount];
        IReadOnlyDictionary<string, IoHash>[] contentSnapshots =
            new IReadOnlyDictionary<string, IoHash>[PartitionCount];
        System.Threading.Tasks.Parallel.For(0, PartitionCount, i =>
        {
            _partitions[i].Snapshot(out producerSnapshots[i]);
            _partitions[i].SnapshotContent(out contentSnapshots[i]);
        });

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(s_magic);
                writer.Write(FormatVersion);

                byte[] versionBytes = CurrentVersion.ToByteArray();
                writer.Write(versionBytes);

                writer.Write(PartitionCount);

                for (int i = 0; i < PartitionCount; i++)
                {
                    // ----- Producer-key map (v1 + v2) -----
                    IReadOnlyDictionary<string, IoHash> entries = producerSnapshots[i];
                    writer.Write(entries.Count);
                    // Sort entries by path with StringComparer.Ordinal so
                    // the on-disk byte sequence is invariant across runs.
                    // Dictionary<>.GetEnumerator order is not specified;
                    // relying on it for the serialised output would make
                    // ActionHistory.bin a non-reproducible artefact (cache
                    // hits would depend on iteration order on the writer's
                    // machine).
                    foreach ((string path, IoHash hash) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
                        writer.Write(pathBytes.Length);
                        writer.Write(pathBytes);
                        writer.Write(hash.ToByteArray());
                    }

                    // ----- Content-hash map (v2; audit fix M3) -----
                    IReadOnlyDictionary<string, IoHash> contentEntries = contentSnapshots[i];
                    writer.Write(contentEntries.Count);
                    foreach ((string path, IoHash hash) in contentEntries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
                        writer.Write(pathBytes.Length);
                        writer.Write(pathBytes);
                        writer.Write(hash.ToByteArray());
                    }
                }
            }
            // Audit fix M12: fsync before rename so a power-loss after
            // rename cannot leave a torn ActionHistory archive. The
            // BinaryWriter has left the FileStream open (leaveOpen=true)
            // so the Flush call here observes every queued write.
            stream.Flush(flushToDisk: true);
        }

        // Atomic rename. File.Move(overwrite=true) maps to MoveFileEx
        // with MOVEFILE_REPLACE_EXISTING on Win64 (per XBT.html Section
        // 6.4). On Linux we rely on the underlying rename(2) call,
        // which is atomic when source and destination are on the same
        // volume -- guaranteed here because both live in the same
        // directory. Audit fix R6-C5: wrapped in the AV-retry helper
        // because Windows Defender transiently locks the just-written
        // archive while it scans the post-flush content.
        FileSystemOps.RetryOnTransientIOException(
            () => File.Move(tempPath, _archivePath, overwrite: true));
    }

    /// <summary>
    /// Load the archive into memory. Missing file = empty archive.
    /// Mismatched <see cref="CurrentVersion"/> = empty archive (the
    /// schema has changed; old entries are invalid). Corrupt or
    /// truncated file = empty archive + a one-time warning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit fix M15: stage partition reads into a local
    /// <see cref="Dictionary{TKey,TValue}"/> array; only commit to
    /// <see cref="_partitions"/> after every partition reads
    /// successfully. A torn-read mid-load no longer leaves a
    /// half-populated archive in memory.
    /// </para>
    /// <para>
    /// TODO(Phase 2 -- TargetMakefile concurrent IO): load is still
    /// serial because the single-file format does not include a
    /// per-partition offset table. The Phase 2 per-bucket-file split
    /// (one ActionHistory_<c>&lt;byte&gt;</c>.bin per partition under
    /// the config directory) enables both parallel load and parallel
    /// save without an offset table -- each bucket is an independent
    /// file. Revisit when the partition-contention measurement
    /// indicates the per-partition contention is real (current
    /// 256-bucket scheme has not surfaced measurable contention).
    /// </para>
    /// </remarks>
    private void LoadFromDisk()
    {
        if (!File.Exists(_archivePath))
        {
            return;
        }

        Dictionary<string, IoHash>[]? staged = null;
        try
        {
            // Audit fix R7-C3: AV-retry-wrap the open so a sibling save's
            // MoveFileEx scan window does not surface as a build failure.
            // Once the open succeeds the file handle is stable; the rest
            // of the load happens against the snapshotted handle.
            using FileStream stream = FileSystemOps.RetryOnTransientIOException(
                () => new FileStream(_archivePath, FileMode.Open, FileAccess.Read, FileShare.Read));
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

            byte[] magic = reader.ReadBytes(4);
            if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(s_magic))
            {
                return;
            }

            // Audit fix M3: accept v1 (producer-key map only) AND v2
            // (producer-key + content-hash maps). v1 archives are
            // treated as having an empty content map; the next save
            // rewrites in v2 layout.
            int format = reader.ReadInt32();
            if (format != 1 && format != FormatVersion)
            {
                return;
            }

            byte[] versionBytes = reader.ReadBytes(IoHash.Length);
            if (versionBytes.Length != IoHash.Length)
            {
                return;
            }
            IoHash storedVersion = new(versionBytes);
            if (storedVersion != CurrentVersion)
            {
                // Schema drift: every previous entry is invalid.
                return;
            }

            int partitionCount = reader.ReadInt32();
            if (partitionCount != PartitionCount)
            {
                return;
            }

            // Audit fix M15: stage reads into a local array. Commit
            // to _partitions only after every partition loads cleanly.
            // Audit fix M3: stage both maps (producer-key and content-
            // hash) in parallel so a torn partial read leaves the live
            // archive untouched.
            staged = new Dictionary<string, IoHash>[partitionCount];
            Dictionary<string, IoHash>[] stagedContent =
                new Dictionary<string, IoHash>[partitionCount];

            for (int i = 0; i < partitionCount; i++)
            {
                staged[i] = new Dictionary<string, IoHash>(StringComparer.Ordinal);
                stagedContent[i] = new Dictionary<string, IoHash>(StringComparer.Ordinal);

                if (!ReadPathToHashMap(reader, staged[i]))
                {
                    return;
                }

                // v2 only: content-hash map follows.
                if (format == FormatVersion)
                {
                    if (!ReadPathToHashMap(reader, stagedContent[i]))
                    {
                        return;
                    }
                }
            }

            // All partitions loaded; commit atomically.
            for (int i = 0; i < partitionCount; i++)
            {
                foreach ((string path, IoHash hash) in staged[i])
                {
                    _partitions[i].Set(path, hash);
                }
                foreach ((string path, IoHash hash) in stagedContent[i])
                {
                    _partitions[i].SetContentHash(path, hash);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Truncated -- staged partitions are dropped (they live in
            // local variables and never reached _partitions). LoadFromDisk
            // is called from the Open() constructor, so _partitions is
            // already empty when this catch fires; ClearAllPartitions()
            // is still invoked explicitly as defence-in-depth to maintain
            // the hard postcondition that no torn entries leak into the
            // live archive even if a future refactor moves the call site.
            ClearAllPartitions();
        }
        catch (IOException)
        {
            // Treat as empty archive; the next Save() will overwrite.
            // staged partitions never reached _partitions.
        }
    }

    private void ClearAllPartitions()
    {
        for (int i = 0; i < _partitions.Length; i++)
        {
            _partitions[i].Clear();
        }
    }

    /// <summary>
    /// Audit fix M3 helper: read one length-prefixed
    /// <c>path -&gt; IoHash</c> map from <paramref name="reader"/> into
    /// <paramref name="dest"/>. Returns false on any structural error
    /// (negative count, oversized path, truncated input). Caller treats
    /// false as "abort the load, leave the archive untouched".
    /// </summary>
    private static bool ReadPathToHashMap(BinaryReader reader, Dictionary<string, IoHash> dest)
    {
        int entryCount = reader.ReadInt32();
        if (entryCount < 0)
        {
            return false;
        }
        for (int j = 0; j < entryCount; j++)
        {
            int pathLen = reader.ReadInt32();
            if (pathLen < 0 || pathLen > 4096)
            {
                return false;
            }
            byte[] pathBytes = reader.ReadBytes(pathLen);
            if (pathBytes.Length != pathLen)
            {
                return false;
            }
            string path = Encoding.UTF8.GetString(pathBytes);
            byte[] hashBytes = reader.ReadBytes(IoHash.Length);
            if (hashBytes.Length != IoHash.Length)
            {
                return false;
            }
            IoHash hash = new(hashBytes);
            dest[path] = hash;
        }
        return true;
    }

    /// <summary>
    /// Map a file path to its partition index. Uses BLAKE3 of the path
    /// and the first byte of the digest -- deterministic across runs and
    /// machines, distributes paths roughly uniformly across the 256 slots.
    /// </summary>
    private static int PartitionOf(string fullPath)
    {
        Span<byte> digest = stackalloc byte[IoHash.Length];
        ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(fullPath);
        using Hasher hasher = Hasher.New();
        hasher.Update(utf8);
        hasher.Finalize(digest);
        return digest[0];
    }

    private static IoHash ComputeCurrentVersion()
    {
        // The schema we hash is the canonical declaration of every
        // public, instance property on IExternalAction, sorted by
        // property name with StringComparer.Ordinal. Alphabetical
        // ordering is stable across Roslyn versions, .NET versions,
        // and architectures; MetadataToken ordering relies on Roslyn
        // implementation details that are not spec-guaranteed and can
        // shift between compiler revisions. Reorder-resilient by
        // construction: shuffling the property declarations in the
        // interface source does not change the hash, but adding,
        // removing, or renaming a property does.
        Type t = typeof(IExternalAction);
        PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Array.Sort(props, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

        using Hasher hasher = Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // Include the XActionType enum's value list -- adding an enum
        // member that lands in a reserved slot doesn't change the IExternalAction
        // property surface but does change which actions can be
        // emitted. Include the named XActionType values in declaration
        // order so adding a new emitted value (beyond the reserved slots)
        // bumps the version.
        string[] actionTypeNames = Enum.GetNames(typeof(XActionType));
        BitConverter.TryWriteBytes(intBuffer, actionTypeNames.Length);
        hasher.Update(intBuffer);
        foreach (string actionName in actionTypeNames)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(actionName);
            BitConverter.TryWriteBytes(intBuffer, nameBytes.Length);
            hasher.Update(intBuffer);
            hasher.Update(nameBytes);
        }

        // Then the IExternalAction property names + their type full
        // names (Type.FullName is the canonical assembly-qualified
        // identifier; a rename of a property OR a change to a property's
        // type bumps the hash).
        BitConverter.TryWriteBytes(intBuffer, props.Length);
        hasher.Update(intBuffer);
        foreach (PropertyInfo prop in props)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(prop.Name);
            BitConverter.TryWriteBytes(intBuffer, nameBytes.Length);
            hasher.Update(intBuffer);
            hasher.Update(nameBytes);

            string typeFullName = prop.PropertyType.FullName ?? prop.PropertyType.Name;
            byte[] typeBytes = Encoding.UTF8.GetBytes(typeFullName);
            BitConverter.TryWriteBytes(intBuffer, typeBytes.Length);
            hasher.Update(intBuffer);
            hasher.Update(typeBytes);
        }

        Span<byte> digest = stackalloc byte[IoHash.Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }

    /// <summary>
    /// In-memory map for a single partition. Concurrent reads are
    /// lock-free; writes serialise on a per-partition lock so two writers
    /// in the same partition don't race. Cross-partition writes are
    /// unconditional.
    /// </summary>
    /// <remarks>
    /// Audit fix M3: each partition carries two parallel maps. The
    /// producer-key map stores producer-action keys keyed on the
    /// produced FileItem's path. The content-hash map stores raw-source
    /// content hashes keyed on the source FileItem's path. The two are
    /// kept distinct because the IsActionOutdated rule 4 needs the
    /// content hash to compare against the live file; mixing them was
    /// the previous Rev 13 bug.
    /// </remarks>
    private sealed class Partition
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IoHash> _map = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IoHash> _contentMap = new(StringComparer.Ordinal);

        public bool TryGet(string path, out IoHash hash)
        {
            // Volatile read isn't needed: Dictionary<>.TryGetValue is
            // safe for reads happening *after* writes complete, which
            // is the only ordering the caller cares about under the
            // build's per-action lifecycle. Concurrent reads-during-
            // writes would need the lock; we take the lock to be safe.
            lock (_gate)
            {
                return _map.TryGetValue(path, out hash);
            }
        }

        public void Set(string path, IoHash hash)
        {
            lock (_gate)
            {
                _map[path] = hash;
            }
        }

        public bool TryGetContentHash(string path, out IoHash hash)
        {
            lock (_gate)
            {
                return _contentMap.TryGetValue(path, out hash);
            }
        }

        public void SetContentHash(string path, IoHash hash)
        {
            lock (_gate)
            {
                _contentMap[path] = hash;
            }
        }

        public void Snapshot(out IReadOnlyDictionary<string, IoHash> snapshot)
        {
            lock (_gate)
            {
                snapshot = new Dictionary<string, IoHash>(_map, StringComparer.Ordinal);
            }
        }

        public void SnapshotContent(out IReadOnlyDictionary<string, IoHash> snapshot)
        {
            lock (_gate)
            {
                snapshot = new Dictionary<string, IoHash>(_contentMap, StringComparer.Ordinal);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _contentMap.Clear();
            }
        }
    }
}
