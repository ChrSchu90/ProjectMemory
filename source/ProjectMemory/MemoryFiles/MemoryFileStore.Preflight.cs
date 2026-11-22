namespace ProjectMemory.MemoryFiles;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ProjectMemory.Database;

internal sealed partial class MemoryFileStore
{
    #region Static Fields

    private const int MaximumTreeDepth = 128;
    private const int MaximumPreflightEntries = 100_000;
    private static readonly TimeSpan PreflightDuration = TimeSpan.FromSeconds(30);

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Inspects the managed roots and SQLite names before migration or destructive recovery.
    /// Ordinary offline files, including unaddressable names and staging remnants, are left in place.
    /// </summary>
    /// <param name="database">The actual lifecycle and migration paths used by the host.</param>
    /// <param name="token">Cancellation between directory entries.</param>
    /// <returns>The canonical UUID trees to reconcile with SQLite before replay.</returns>
    internal (HashSet<Guid> Projects, HashSet<Guid> Quarantine) Preflight(DatabasePaths database, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var root = DataRoot();
        foreach (var file in new[] { database.DatabasePath, database.MigrationPath, database.BackupPath })
        {
            foreach (var suffix in new[] { "", "-shm", "-wal", "-journal" })
            {
                if (Attributes(file + suffix)?.HasFlag(FileAttributes.Directory) == true)
                    throw new MemoryFileException("UNSAFE_PATH");
            }
        }

        if (Attributes(database.MigrationLockPath)?.HasFlag(FileAttributes.Directory) == true)
            throw new MemoryFileException("UNSAFE_PATH");

        var visited = 0;
        var projects = InspectRoot("projects");
        var quarantine = InspectRoot("quarantine");
        if (projects.Overlaps(quarantine))
            throw new MemoryFileException("UNSAFE_PATH");
        CheckBudget();
        return (projects, quarantine);

        HashSet<Guid> InspectRoot(string name)
        {
            var ids = new HashSet<Guid>();
            var parent = Path.Combine(root, name);
            var attributes = Attributes(parent);
            if (attributes is null)
                return ids;
            if (!attributes.Value.HasFlag(FileAttributes.Directory))
                throw new MemoryFileException("UNSAFE_PATH");

            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                var entry = Inspect(path);
                var component = Path.GetFileName(path);
                if (!entry.HasFlag(FileAttributes.Directory) || !Guid.TryParseExact(component, "D", out var id)
                    || component != id.ToString("D") || id == Guid.Empty)
                    throw new MemoryFileException("UNSAFE_PATH");
                ids.Add(id);
                Visit(path, 0);
            }

            return ids;
        }

        void Visit(string directory, int depth)
        {
            if (depth > MaximumTreeDepth)
                throw new MemoryFileException("UNSAFE_PATH");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Inspect(path).HasFlag(FileAttributes.Directory))
                {
                    Visit(path, depth + 1);
                }
            }
        }

        FileAttributes Inspect(string path)
        {
            visited++;
            CheckBudget();
            return Attributes(path) ?? throw new MemoryFileException("UNSAFE_PATH");
        }

        void CheckBudget()
        {
            token.ThrowIfCancellationRequested();
            if (visited > MaximumPreflightEntries || Stopwatch.GetElapsedTime(started) >= PreflightDuration)
                throw new MemoryFileException("SCAN_LIMIT");
        }
    }

    #endregion

    #region Private Methods

    #endregion
}
