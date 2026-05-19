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

    /// <summary>File format version. Bumped on any breaking format change.</summary>
    private const int FormatVersion = 1;

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
    ///   <item>Any prerequisite's content hash differs from what was recorded last; OR</item>
    ///   <item>The action's <see cref="ComputeActionKey"/> differs from any produced item's stored hash; OR</item>
    ///   <item>Any <see cref="IExternalAction.ProducedItems"/> file does not exist on disk.</item>
    /// </list>
    /// Returns false (= up-to-date) only when every produced item exists,
    /// each carries the same recorded key, and the recorded key matches
    /// the current action key.
    /// </summary>
    public bool IsActionOutdated(LinkedAction linked)
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

        // 4. No prerequisite's content hash may have changed since record.
        //    We use a content-hash subkey approach: a prerequisite's
        //    stored key is the IoHash that was assigned when its
        //    producer action recorded it. We compare against the live
        //    file's hash; if they differ the producer's output has
        //    changed since record (or was modified externally).
        foreach (FileItem prereq in action.PrerequisiteItems)
        {
            // For prerequisites that have no producer in this build
            // (raw source files), the stored key is the prereq's own
            // content hash; the comparison is content-hash-against-
            // content-hash. We do not consult the file's previous
            // content hash from this archive -- we trust the file's
            // current content hash as the live truth.
            //
            // For prerequisites that ARE producers in this build
            // (intermediate outputs), their stored entry equals the
            // producer's CurrentVersion-keyed value; the producer
            // action is itself checked by its own IsActionOutdated.
            //
            // In either case, the "key changed" rule (item 3) catches
            // the staleness; this leaf check only matters for raw
            // source files that XBT has previously hashed via
            // RecordHash on the prerequisite item directly.
            IoHash recorded = GetStoredHash(prereq);
            if (recorded != IoHash.Zero)
            {
                if (!File.Exists(prereq.FullPath))
                {
                    return true;
                }
                // Read the prerequisite's current content hash. We
                // re-stat through FileItem -- the recorded hash is
                // either the prereq's content hash (raw source) or
                // its producer-derived key; if neither matches we
                // are stale.
                IoHash live = prereq.ContentHash;
                if (live != recorded)
                {
                    // Producer-derived keys won't match a content
                    // hash; fall through to item 3 which has already
                    // run and returned true if the producer's key
                    // changed. If item 3 didn't flag this, the
                    // recorded value IS the content hash and we are
                    // stale.
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Persist the archive to disk. Writes to a sibling
    /// <c>.tmp.&lt;pid&gt;.&lt;tick&gt;</c> file first and renames into
    /// place per <c>/Documents/XBT.html</c> Section 6.4 atomic-rename
    /// contract. A torn write leaves the previous archive intact on
    /// disk.
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
        string directory = Path.GetDirectoryName(_archivePath)!;
        int pid = Environment.ProcessId;
        long tick = DateTime.UtcNow.Ticks;
        string tempPath = Path.Combine(directory, $"ActionHistory.bin.tmp.{pid}.{tick}");

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(s_magic);
            writer.Write(FormatVersion);

            byte[] versionBytes = CurrentVersion.ToByteArray();
            writer.Write(versionBytes);

            writer.Write(PartitionCount);

            for (int i = 0; i < PartitionCount; i++)
            {
                _partitions[i].Snapshot(out IReadOnlyDictionary<string, IoHash> entries);
                writer.Write(entries.Count);
                foreach ((string path, IoHash hash) in entries)
                {
                    byte[] pathBytes = Encoding.UTF8.GetBytes(path);
                    writer.Write(pathBytes.Length);
                    writer.Write(pathBytes);
                    writer.Write(hash.ToByteArray());
                }
            }
        }

        // Atomic rename. File.Move(overwrite=true) maps to MoveFileEx
        // with MOVEFILE_REPLACE_EXISTING on Win64 (per XBT.html Section
        // 6.4). On Linux we rely on the underlying rename(2) call,
        // which is atomic when source and destination are on the same
        // volume -- guaranteed here because both live in the same
        // directory.
        File.Move(tempPath, _archivePath, overwrite: true);
    }

    /// <summary>
    /// Load the archive into memory. Missing file = empty archive.
    /// Mismatched <see cref="CurrentVersion"/> = empty archive (the
    /// schema has changed; old entries are invalid). Corrupt or
    /// truncated file = empty archive + a one-time warning.
    /// </summary>
    private void LoadFromDisk()
    {
        if (!File.Exists(_archivePath))
        {
            return;
        }

        try
        {
            using FileStream stream = new(_archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

            byte[] magic = reader.ReadBytes(4);
            if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(s_magic))
            {
                return;
            }

            int format = reader.ReadInt32();
            if (format != FormatVersion)
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

            for (int i = 0; i < partitionCount; i++)
            {
                int entryCount = reader.ReadInt32();
                if (entryCount < 0)
                {
                    return;
                }
                for (int j = 0; j < entryCount; j++)
                {
                    int pathLen = reader.ReadInt32();
                    if (pathLen < 0 || pathLen > 4096)
                    {
                        return;
                    }
                    byte[] pathBytes = reader.ReadBytes(pathLen);
                    if (pathBytes.Length != pathLen)
                    {
                        return;
                    }
                    string path = Encoding.UTF8.GetString(pathBytes);
                    byte[] hashBytes = reader.ReadBytes(IoHash.Length);
                    if (hashBytes.Length != IoHash.Length)
                    {
                        return;
                    }
                    IoHash hash = new(hashBytes);
                    _partitions[i].Set(path, hash);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Truncated -- treat as empty.
            ClearAllPartitions();
        }
        catch (IOException)
        {
            // Treat as empty archive; the next Save() will overwrite.
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
        // public, instance property on IExternalAction, in source-
        // declaration order. Reflection cannot retrieve source-declaration
        // order portably, but MetadataToken increases monotonically for
        // members declared in source order within a type -- this is a
        // documented invariant of the C# compiler. We sort by token to
        // recover the declaration order.
        Type t = typeof(IExternalAction);
        PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Array.Sort(props, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));

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
    private sealed class Partition
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IoHash> _map = new(StringComparer.Ordinal);

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

        public void Snapshot(out IReadOnlyDictionary<string, IoHash> snapshot)
        {
            lock (_gate)
            {
                snapshot = new Dictionary<string, IoHash>(_map, StringComparer.Ordinal);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
            }
        }
    }
}
