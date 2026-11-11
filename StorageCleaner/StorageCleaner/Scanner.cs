using System.Collections.Concurrent;
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
    internal string? QuickSig { get; set; }
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

public sealed record ScanOptions(
    bool IncludeHiddenCaches = false,
    long MinDuplicateBytes = 100L * 1024,
    bool FastLargeFileSampling = true
);

public sealed record ScanProgress(long Files, long Bytes, string CurrentPath, string Stage = "Scanning");

public sealed record ScanResult(
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<string> Errors,
    long BytesScanned,
    long ReclaimableDuplicateBytes
);

public static class Scanner
{
    private const long LargeFileFullHashThreshold = 16L * 1024 * 1024;

    private static readonly string[] ProtectedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
    ];

    private static readonly HashSet<string> AlwaysExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData",
        "$Recycle.Bin",
        "System Volume Information",
        "node_modules",
        ".git",
        ".vs"
    };

    public static async Task<ScanResult> ScanAsync(
        IEnumerable<string> roots,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        await Task.Yield();

        var errors = new ConcurrentQueue<string>();
        var seenDirectories = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var dirQueue = new ConcurrentQueue<string>();

        var canonicalRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawRoot in roots)
        {
            try
            {
                string full = Path.GetFullPath(rawRoot).TrimEnd(Path.DirectorySeparatorChar);
                if (!IsProtectedRoot(full) && seenDirectories.TryAdd(full, 0))
                {
                    canonicalRoots.Add(full);
                    dirQueue.Enqueue(full);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue($"{rawRoot}: {ex.Message}");
            }
        }

        if (dirQueue.IsEmpty)
        {
            return new ScanResult([], [.. errors], 0, 0);
        }

        // 1. Parallel Batched Directory Traversal
        var fileBags = new ConcurrentBag<List<FileEntry>>();
        int pendingDirs = dirQueue.Count;
        using var semaphore = new SemaphoreSlim(pendingDirs);
        int workerCount = Math.Clamp(Environment.ProcessorCount, 4, 8);

        long totalFiles = 0;
        long totalBytes = 0;
        long lastReportTick = Environment.TickCount64;

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            var localFiles = new List<FileEntry>(4096);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await semaphore.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!dirQueue.TryDequeue(out string? canonical))
                {
                    break;
                }

                long dirFiles = 0;
                long dirBytes = 0;
                string? samplePath = null;

                try
                {
                    var dirInfo = new DirectoryInfo(canonical);
                    if (dirInfo.Exists)
                    {
                        foreach (var fsi in dirInfo.EnumerateFileSystemInfos("*", enumerationOptions))
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                if (fsi.Attributes.HasFlag(FileAttributes.Directory))
                                {
                                    string childPath = fsi.FullName.TrimEnd(Path.DirectorySeparatorChar);
                                    if (!ShouldSkipDirectory(childPath, fsi.Name, canonicalRoots, options.IncludeHiddenCaches)
                                        && seenDirectories.TryAdd(childPath, 0))
                                    {
                                        Interlocked.Increment(ref pendingDirs);
                                        dirQueue.Enqueue(childPath);
                                        semaphore.Release();
                                    }
                                }
                                else if (fsi is FileInfo fileInfo)
                                {
                                    long size = fileInfo.Length;
                                    localFiles.Add(new FileEntry
                                    {
                                        Path = fileInfo.FullName,
                                        Name = fileInfo.Name,
                                        Size = size,
                                        LastAccessUtc = fileInfo.LastAccessTimeUtc,
                                        LastWriteUtc = fileInfo.LastWriteTimeUtc
                                    });
                                    dirFiles++;
                                    dirBytes += size;
                                    samplePath = fileInfo.FullName;
                                }
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                            {
                                errors.Enqueue($"{fsi.FullName}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    errors.Enqueue($"{canonical}: {ex.Message}");
                }
                finally
                {
                    if (dirFiles > 0)
                    {
                        long countSnap = Interlocked.Add(ref totalFiles, dirFiles);
                        long bytesSnap = Interlocked.Add(ref totalBytes, dirBytes);
                        long now = Environment.TickCount64;
                        long prev = Volatile.Read(ref lastReportTick);
                        if (now - prev >= 80 && Interlocked.CompareExchange(ref lastReportTick, now, prev) == prev)
                        {
                            progress?.Report(new ScanProgress(countSnap, bytesSnap, samplePath ?? canonical, "Discovering files"));
                        }
                    }

                    if (Interlocked.Decrement(ref pendingDirs) == 0)
                    {
                        semaphore.Release(workerCount);
                    }
                }
            }

            if (localFiles.Count > 0)
            {
                fileBags.Add(localFiles);
            }
        })).ToArray();

        await Task.WhenAll(workers);
        token.ThrowIfCancellationRequested();

        var files = new List<FileEntry>((int)Math.Min(totalFiles, int.MaxValue));
        foreach (var bag in fileBags)
        {
            files.AddRange(bag);
        }

        // 2. High-Speed Hybrid Duplicate Detection
        progress?.Report(new ScanProgress(totalFiles, totalBytes, "Grouping identical file sizes…", "Analyzing candidates"));

        long minDupSize = Math.Max(1L, options.MinDuplicateBytes);
        var candidates = files.Where(f => f.Size >= minDupSize)
                              .GroupBy(f => f.Size)
                              .Where(g => g.Count() > 1)
                              .SelectMany(g => g)
                              .ToList();

        int totalCandidates = candidates.Count;
        if (totalCandidates > 0)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = token
            };

            // Stage A: Fast 1 KB Prefix Check (Sequential sector read, zero seek)
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

                    if (fs.Length != file.Size) return;
                    int read = await fs.ReadAtLeastAsync(buf, 1024, throwOnEndOfStream: false, ct);
                    file.QuickSig = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, read)));
                    if (file.Size <= 1024)
                    {
                        file.Hash = file.QuickSig;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    errors.Enqueue($"Prefix check skipped: {file.Path}: {ex.Message}");
                }

                long processed = Interlocked.Increment(ref prefixProcessed);
                long now = Environment.TickCount64;
                long prev = Volatile.Read(ref lastReportTick);
                if (now - prev >= 80 && Interlocked.CompareExchange(ref lastReportTick, now, prev) == prev)
                {
                    progress?.Report(new ScanProgress(totalFiles, totalBytes, $"Quick signature check ({processed:N0}/{totalCandidates:N0})", "Signature check"));
                }
            });

            // Stage B: Full SHA-256 (<= 16 MB or Strict Mode) or 8-Point Distributed SHA-256 (> 16 MB in Fast Mode)
            var needsVerify = candidates.Where(f => f.QuickSig is not null && f.Size > 1024)
                                        .GroupBy(f => (f.Size, f.QuickSig!))
                                        .Where(g => g.Count() > 1)
                                        .SelectMany(g => g)
                                        .ToList();

            if (needsVerify.Count > 0)
            {
                long verifyProcessed = 0;
                int totalVerify = needsVerify.Count;

                await Parallel.ForEachAsync(needsVerify, parallelOptions, async (file, ct) =>
                {
                    try
                    {
                        bool useSampledHash = options.FastLargeFileSampling && file.Size > LargeFileFullHashThreshold;
                        await using var fs = new FileStream(file.Path, new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.ReadWrite | FileShare.Delete,
                            BufferSize = useSampledHash ? 64 * 1024 : 128 * 1024,
                            Options = useSampledHash ? FileOptions.RandomAccess : FileOptions.SequentialScan
                        });

                        if (fs.Length != file.Size) return;
                        if (!useSampledHash)
                        {
                            file.Hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                        }
                        else
                        {
                            const int chunk = 64 * 1024;
                            const int samples = 8;
                            byte[] buf = new byte[chunk * samples];
                            long step = (file.Size - chunk) / (samples - 1);
                            int offset = 0;

                            for (int k = 0; k < samples; k++)
                            {
                                fs.Seek(k == samples - 1 ? file.Size - chunk : k * step, SeekOrigin.Begin);
                                int read = await fs.ReadAtLeastAsync(buf.AsMemory(offset, chunk), chunk, throwOnEndOfStream: false, ct);
                                offset += read;
                            }

                            file.Hash = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, offset)));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        errors.Enqueue($"Verification skipped: {file.Path}: {ex.Message}");
                    }

                    long processed = Interlocked.Increment(ref verifyProcessed);
                    long now = Environment.TickCount64;
                    long prev = Volatile.Read(ref lastReportTick);
                    if (now - prev >= 80 && Interlocked.CompareExchange(ref lastReportTick, now, prev) == prev)
                    {
                        progress?.Report(new ScanProgress(totalFiles, totalBytes, $"Verifying duplicates: {file.Name} ({processed:N0}/{totalVerify:N0})", "Verifying duplicates"));
                    }
                });
            }
        }

        // 3. Duplicate Grouping & Reclaimable Space Calculation
        long reclaimableBytes = AssignDuplicateGroups(files);

        progress?.Report(new ScanProgress(totalFiles, totalBytes, "Ready", "Complete"));
        return new ScanResult(files, [.. errors], totalBytes, reclaimableBytes);
    }

    public static long AssignDuplicateGroups(IEnumerable<FileEntry> files)
    {
        int groupId = 1;
        long reclaimableBytes = 0;
        var all = files as IList<FileEntry> ?? files.ToList();

        foreach (var f in all)
        {
            f.DuplicateGroupId = 0;
            f.DuplicateGroupCount = 0;
        }

        foreach (var group in all.Where(f => f.Hash is not null).GroupBy(f => (f.Size, f.Hash!)).Where(g => g.Count() > 1))
        {
            int groupCount = group.Count();
            reclaimableBytes += (groupCount - 1) * group.Key.Size;

            foreach (var copy in group)
            {
                copy.DuplicateGroupId = groupId;
                copy.DuplicateGroupCount = groupCount;
            }
            groupId++;
        }

        return reclaimableBytes;
    }

    private static bool ShouldSkipDirectory(string canonicalPath, string folderName, ISet<string> canonicalRoots, bool includeHiddenCaches)
    {
        if (IsProtectedRoot(canonicalPath)) return true;
        if (canonicalRoots.Contains(canonicalPath)) return false;
        if (AlwaysExcludedFolders.Contains(folderName)) return true;
        if (!includeHiddenCaches && folderName.StartsWith('.')) return true;
        return false;
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
