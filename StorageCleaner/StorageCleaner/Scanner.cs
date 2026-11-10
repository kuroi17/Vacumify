using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;

namespace Vacumify;

public sealed class FileEntry : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required DateTime LastAccessUtc { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public string? Hash { get; set; }
    public string? QuickSig { get; set; }
    public int DuplicateGroupId { get; set; }
    public int DuplicateGroupCount { get; set; }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SizeText => FormatBytes(Size);
    public string AccessedText => LastAccessUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ModifiedText => LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double n = bytes;
        int i = 0;
        while (n >= 1024 && i < units.Length - 1)
        {
            n /= 1024;
            i++;
        }
        return $"{n:0.##} {units[i]}";
    }
}

public sealed record ScanProgress(long Files, long Bytes, string CurrentPath, string Stage = "Scanning");

public sealed record ScanResult(
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<string> Errors,
    long BytesScanned,
    long ReclaimableDuplicateBytes
);

public static class Scanner
{
    private static readonly string[] ProtectedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
    ];

    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData",
        "$Recycle.Bin",
        "System Volume Information",
        "node_modules",
        ".git",
        ".gemini",
        ".vs"
    };

    public static async Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        await Task.Yield();

        var files = new List<FileEntry>(64000);
        var errors = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0, count = 0;

        var canonicalRoots = roots.Select(r =>
        {
            try { return Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return r; }
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pending = new Queue<string>(canonicalRoots);
        long lastReportTick = Environment.TickCount64;

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        // 1. High-Performance Batched Directory Traversal
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Dequeue();
            string canonical;
            try
            {
                canonical = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                errors.Add($"{directory}: {ex.Message}");
                continue;
            }

            if (!seenDirectories.Add(canonical)) continue;
            if (ShouldSkipDirectory(canonical, canonicalRoots)) continue;

            DirectoryInfo dirInfo;
            try
            {
                dirInfo = new DirectoryInfo(canonical);
                if (!dirInfo.Exists) continue;
            }
            catch (Exception ex)
            {
                errors.Add($"{canonical}: {ex.Message}");
                continue;
            }

            try
            {
                foreach (var fsi in dirInfo.EnumerateFileSystemInfos("*", enumerationOptions))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (fsi.Attributes.HasFlag(FileAttributes.Directory))
                        {
                            string dirName = fsi.Name;
                            if (!canonicalRoots.Contains(fsi.FullName))
                            {
                                // Skip system folders, node_modules, and hidden development dot-caches (.cursor, .gradle, .gemini, etc.)
                                if (ExcludedFolderNames.Contains(dirName) || dirName.StartsWith('.'))
                                {
                                    continue;
                                }
                            }
                            pending.Enqueue(fsi.FullName);
                        }
                        else if (fsi is FileInfo fileInfo)
                        {
                            var file = new FileEntry
                            {
                                Path = fileInfo.FullName,
                                Name = fileInfo.Name,
                                Size = fileInfo.Length,
                                LastAccessUtc = fileInfo.LastAccessTimeUtc,
                                LastWriteUtc = fileInfo.LastWriteTimeUtc
                            };

                            files.Add(file);
                            count++;
                            bytes += fileInfo.Length;

                            long now = Environment.TickCount64;
                            if (now - lastReportTick >= 80)
                            {
                                lastReportTick = now;
                                progress?.Report(new ScanProgress(count, bytes, fileInfo.FullName, "Discovering files"));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                    {
                        errors.Add($"{fsi.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                errors.Add($"{canonical}: {ex.Message}");
            }
        }

        // 2. High-Speed 3-Stage Concurrency Duplicate Engine (Goal: < 10 seconds)
        progress?.Report(new ScanProgress(count, bytes, "Grouping identical file sizes…", "Candidate analysis"));

        // Only files >= 1024 bytes (1 KB) with exact matching size
        var sizeGroups = files.Where(f => f.Size >= 1024)
                              .GroupBy(f => f.Size)
                              .Where(g => g.Count() > 1)
                              .ToList();

        var candidates = sizeGroups.SelectMany(g => g).ToList();
        int totalCandidates = candidates.Count;

        if (totalCandidates > 0)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = token
            };

            // Stage A: Lightning-fast 1 KB Prefix Check (Sequential sector read, zero seek)
            long prefixProcessed = 0;
            await Parallel.ForEachAsync(candidates, parallelOptions, async (file, ct) =>
            {
                try
                {
                    byte[] buf = new byte[1024];
                    await using var fs = new FileStream(file.Path, new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        BufferSize = 1024,
                        Options = FileOptions.SequentialScan
                    });

                    int read = await fs.ReadAsync(buf.AsMemory(0, 1024), ct);
                    file.QuickSig = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, read)));
                    if (file.Size <= 1024) file.Hash = file.QuickSig;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    lock (errors) { errors.Add($"Prefix check skipped: {file.Path}: {ex.Message}"); }
                }

                long processed = Interlocked.Increment(ref prefixProcessed);
                long now = Environment.TickCount64;
                if (now - lastReportTick >= 80)
                {
                    lastReportTick = now;
                    progress?.Report(new ScanProgress(count, bytes, $"Quick signature check ({processed:N0}/{totalCandidates:N0})", "Signature check"));
                }
            });

            // Group by (Size, PrefixSig)
            var prefixMatches = candidates.Where(f => f.QuickSig is not null)
                                          .GroupBy(f => (f.Size, f.QuickSig!))
                                          .Where(g => g.Count() > 1)
                                          .ToList();

            // Stage B: 1 KB Suffix (Tail) Check ONLY for matching prefix candidates > 2048 bytes
            var needsTail = prefixMatches.Where(g => g.Key.Size > 2048).SelectMany(g => g).ToList();
            if (needsTail.Count > 0)
            {
                await Parallel.ForEachAsync(needsTail, parallelOptions, async (file, ct) =>
                {
                    try
                    {
                        byte[] buf = new byte[1024];
                        await using var fs = new FileStream(file.Path, new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.ReadWrite | FileShare.Delete,
                            BufferSize = 1024,
                            Options = FileOptions.RandomAccess
                        });

                        fs.Seek(-1024, SeekOrigin.End);
                        int read = await fs.ReadAsync(buf.AsMemory(0, 1024), ct);
                        file.QuickSig += Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, read)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        lock (errors) { errors.Add($"Tail check skipped: {file.Path}: {ex.Message}"); }
                    }
                });
            }

            // Stage C: Full SHA-256 Verification ONLY for candidates matching Size + Prefix + Tail
            var confirmedCandidates = candidates.Where(f => f.QuickSig is not null)
                                                .GroupBy(f => (f.Size, f.QuickSig!))
                                                .Where(g => g.Count() > 1)
                                                .ToList();

            var needsFullHash = confirmedCandidates.Where(g => g.Key.Size > 2048).SelectMany(g => g).ToList();
            if (needsFullHash.Count > 0)
            {
                long fullProcessed = 0;
                int totalFull = needsFullHash.Count;

                await Parallel.ForEachAsync(needsFullHash, parallelOptions, async (file, ct) =>
                {
                    try
                    {
                        await using var fs = new FileStream(file.Path, new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.ReadWrite | FileShare.Delete,
                            BufferSize = 128 * 1024,
                            Options = FileOptions.SequentialScan
                        });

                        file.Hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        lock (errors) { errors.Add($"Full verification skipped: {file.Path}: {ex.Message}"); }
                    }

                    long processed = Interlocked.Increment(ref fullProcessed);
                    long now = Environment.TickCount64;
                    if (now - lastReportTick >= 80)
                    {
                        lastReportTick = now;
                        progress?.Report(new ScanProgress(count, bytes, $"Verifying full content ({processed:N0}/{totalFull:N0})", "Full verification"));
                    }
                });
            }
        }

        // 3. Duplicate grouping & reclaimable space calculation
        int groupId = 1;
        long reclaimableBytes = 0;
        var duplicateGroups = files.Where(f => f.Hash is not null)
                                   .GroupBy(f => f.Hash!, StringComparer.Ordinal)
                                   .Where(g => g.Count() > 1)
                                   .ToList();

        foreach (var group in duplicateGroups)
        {
            int groupCount = group.Count();
            long groupFileSize = group.First().Size;
            reclaimableBytes += (groupCount - 1) * groupFileSize;

            foreach (var copy in group)
            {
                copy.DuplicateGroupId = groupId;
                copy.DuplicateGroupCount = groupCount;
            }
            groupId++;
        }

        progress?.Report(new ScanProgress(count, bytes, "Ready", "Complete"));
        return new ScanResult(files, errors, bytes, reclaimableBytes);
    }

    private static bool ShouldSkipDirectory(string path, ISet<string> canonicalRoots)
    {
        if (canonicalRoots.Contains(path)) return false;

        string folderName = Path.GetFileName(path);
        if (ExcludedFolderNames.Contains(folderName) || folderName.StartsWith('.')) return true;

        return IsProtectedRoot(path);
    }

    public static bool IsProtectedPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch
        {
            return true;
        }

        return ProtectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Any(root =>
        {
            string protectedPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(protectedPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsProtectedRoot(string path)
    {
        return ProtectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Any(root =>
            string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), path, StringComparison.OrdinalIgnoreCase));
    }
}
