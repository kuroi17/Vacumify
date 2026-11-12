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

    public string ExtensionText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Path)) return "FILE";
            var ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            return string.IsNullOrEmpty(ext) ? "FILE" : (ext.Length > 6 ? ext[..6] : ext);
        }
    }

    public string FileCategory => ExtensionText switch
    {
        "PDF" => "PDF",
        "XLS" or "XLSX" or "CSV" => "Excel",
        "DOC" or "DOCX" or "RTF" => "Word",
        "PPT" or "PPTX" => "PowerPoint",
        "CS" or "PY" or "JS" or "TS" or "JSON" or "HTML" or "CSS" or "XML" or "CPP" or "C" or "H" or "JAVA" or "GO" or "RS" or "PHP" or "SH" or "BAT" or "PS1" or "SQL" => "Code",
        "PNG" or "JPG" or "JPEG" or "GIF" or "WEBP" or "BMP" or "SVG" or "ICO" => "Image",
        "MP4" or "MKV" or "MOV" or "AVI" or "WEBM" or "FLV" => "Video",
        "MP3" or "WAV" or "FLAC" or "AAC" or "OGG" or "M4A" => "Audio",
        "ZIP" or "RAR" or "7Z" or "TAR" or "GZ" or "BZ2" or "ISO" => "Archive",
        "TXT" or "MD" or "LOG" => "Text",
        _ => "Other"
    };

    public string IconResourceKey => FileCategory switch
    {
        "PDF" => "FileIconPdf",
        "Excel" => "FileIconExcel",
        "Word" => "FileIconWord",
        "PowerPoint" => "FileIconPowerPoint",
        "Code" => "FileIconCode",
        "Image" => "FileIconImage",
        "Video" => "FileIconVideo",
        "Audio" => "FileIconAudio",
        "Archive" => "FileIconArchive",
        "Text" => "FileIconText",
        _ => "FileIconGeneric"
    };

    public System.Windows.Media.ImageSource? IconImage
    {
        get
        {
            if (System.Windows.Application.Current is not null && System.Windows.Application.Current.Resources.Contains(IconResourceKey))
            {
                return System.Windows.Application.Current.Resources[IconResourceKey] as System.Windows.Media.ImageSource;
            }
            return null;
        }
    }

    public string TypeBadgeBg => FileCategory switch
    {
        "PDF" => "#FEF2F2",
        "Excel" => "#F0FDF4",
        "Word" => "#EFF6FF",
        "PowerPoint" => "#FFF7ED",
        "Code" => "#FAF5FF",
        "Image" => "#FDF2F8",
        "Video" => "#FFF1F2",
        "Audio" => "#F0FDFA",
        "Archive" => "#FFFBEB",
        "Text" => "#F8FAFC",
        _ => "#F0F9FF"
    };

    public string TypeBadgeBorder => FileCategory switch
    {
        "PDF" => "#FECACA",
        "Excel" => "#BBF7D0",
        "Word" => "#BFDBFE",
        "PowerPoint" => "#FED7AA",
        "Code" => "#E9D5FF",
        "Image" => "#FBCFE8",
        "Video" => "#FECDD3",
        "Audio" => "#99F6E4",
        "Archive" => "#FDE68A",
        "Text" => "#E2E8F0",
        _ => "#BAE6FD"
    };

    public string TypeBadgeFg => FileCategory switch
    {
        "PDF" => "#DC2626",
        "Excel" => "#16A34A",
        "Word" => "#2563EB",
        "PowerPoint" => "#EA580C",
        "Code" => "#7C3AED",
        "Image" => "#DB2777",
        "Video" => "#E11D48",
        "Audio" => "#0D9488",
        "Archive" => "#D97706",
        "Text" => "#64748B",
        _ => "#0284C7"
    };

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
    IReadOnlyList<DevCacheEntry> DevCaches,
    IReadOnlyList<string> Errors,
    long BytesScanned,
    long ReclaimableDuplicateBytes
);

public sealed class DevCacheEntry : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required string ProjectName { get; init; }
    public required string CacheType { get; init; }
    public required long Size { get; set; }
    public required DateTime LastModifiedUtc { get; init; }

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

    public string SizeText => FileEntry.FormatBytes(Size);
    public string ModifiedText => LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string TypeBadgeBg => CacheType switch
    {
        "node_modules" => "#DCFCE7",
        "Python venv" => "#DBEAFE",
        ".NET bin/obj" => "#F3E8FF",
        "Rust target" => "#FFEDD5",
        _ => "#F1F5F9"
    };

    public string TypeBadgeBorder => CacheType switch
    {
        "node_modules" => "#86EFAC",
        "Python venv" => "#93C5FD",
        ".NET bin/obj" => "#D8B4FE",
        "Rust target" => "#FDBA74",
        _ => "#CBD5E1"
    };

    public string TypeBadgeFg => CacheType switch
    {
        "node_modules" => "#15803D",
        "Python venv" => "#1D4ED8",
        ".NET bin/obj" => "#7E22CE",
        "Rust target" => "#C2410C",
        _ => "#475569"
    };
}

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

    private static readonly HashSet<string> HiddenDevFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "venv",
        "__pycache__"
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
            return new ScanResult([], [], [.. errors], 0, 0);
        }

        // 1. Parallel Batched Directory Traversal
        var fileBags = new ConcurrentBag<List<FileEntry>>();
        var devCachesBag = new ConcurrentBag<DevCacheEntry>();
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

                                    // Intercept dev caches & project packages (node_modules, venvs, target, bin/obj)
                                    string parentDir = Path.GetDirectoryName(childPath) ?? "";
                                    if (!IsProtectedRoot(childPath) && IsDevCacheFolder(fsi.Name, parentDir, out string cacheType))
                                    {
                                        long cacheBytes = FastGetDirectorySize(childPath);
                                        string projName = Path.GetFileName(parentDir);
                                        if (string.IsNullOrWhiteSpace(projName)) projName = fsi.Name;

                                        devCachesBag.Add(new DevCacheEntry
                                        {
                                            Path = childPath,
                                            ProjectName = projName,
                                            CacheType = cacheType,
                                            Size = cacheBytes,
                                            LastModifiedUtc = fsi.LastWriteTimeUtc
                                        });
                                        continue;
                                    }

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
        return new ScanResult(files, devCachesBag.OrderByDescending(c => c.Size).ToList(), [.. errors], totalBytes, reclaimableBytes);
    }

    public static List<FileEntry> IdentifySafeJunk(IReadOnlyList<FileEntry> files)
    {
        var safeList = new List<FileEntry>();

        // 1. Duplicate Copies (Keep newest copy, redundant older copies are 100% safe to remove)
        foreach (var grp in files.Where(f => f.DuplicateGroupCount > 1).GroupBy(f => f.DuplicateGroupId))
        {
            var copies = grp.OrderByDescending(f => f.LastWriteUtc).ToList();
            for (int i = 1; i < copies.Count; i++)
            {
                safeList.Add(copies[i]);
            }
        }

        // 2. Old Installers & Crash Dumps (> 30 days untouched)
        var cutoff = DateTime.UtcNow.AddDays(-30);
        foreach (var f in files)
        {
            if (f.DuplicateGroupCount > 1) continue;

            string ext = f.ExtensionText;
            bool isInstaller = ext == "MSI" ||
                (ext == "EXE" && (f.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                                  f.Name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
                                  f.Name.Contains("install", StringComparison.OrdinalIgnoreCase)));

            bool isCrashDump = ext == "DMP" || (ext == "LOG" && f.Size > 20 * 1024 * 1024);

            if ((isInstaller || isCrashDump) && f.LastAccessUtc <= cutoff)
            {
                safeList.Add(f);
            }
        }

        return safeList;
    }

    public static bool IsDevCacheFolder(string folderName, string parentDir, out string cacheType)
    {
        if (string.Equals(folderName, "node_modules", StringComparison.OrdinalIgnoreCase))
        {
            cacheType = "node_modules";
            return true;
        }
        if (string.Equals(folderName, "venv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderName, ".venv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderName, "__pycache__", StringComparison.OrdinalIgnoreCase))
        {
            cacheType = "Python venv";
            return true;
        }
        if (string.Equals(folderName, "target", StringComparison.OrdinalIgnoreCase) &&
            ParentHasAnyFile(parentDir, "Cargo.toml", "pom.xml"))
        {
            cacheType = "Rust target";
            return true;
        }
        if ((string.Equals(folderName, "bin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(folderName, "obj", StringComparison.OrdinalIgnoreCase)) &&
            ParentHasAnyFile(parentDir, "*.csproj", "*.fsproj", "*.vbproj", "*.sln"))
        {
            cacheType = ".NET bin/obj";
            return true;
        }
        if (string.Equals(folderName, ".gradle", StringComparison.OrdinalIgnoreCase))
        {
            cacheType = "Gradle";
            return true;
        }

        cacheType = "";
        return false;
    }

    private static bool ParentHasAnyFile(string parentDir, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir)) return false;
        try
        {
            foreach (var pattern in patterns)
            {
                if (Directory.EnumerateFiles(parentDir, pattern, SearchOption.TopDirectoryOnly).Any())
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static long FastGetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", opt))
            {
                size += fi.Length;
            }
        }
        catch { }
        return size;
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
        if (!includeHiddenCaches && (folderName.StartsWith('.') || HiddenDevFolders.Contains(folderName))) return true;
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

    public static bool IsProtectedRoot(string path)
    {
        return ProtectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Any(root =>
            string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), path, StringComparison.OrdinalIgnoreCase));
    }

    public static (string Status, int DaysUntouched, string ProjectRoot) GetProjectActivity(string targetPath, DateTime fallbackLastWriteUtc)
    {
        string? dir = Directory.Exists(targetPath)
            ? Path.GetDirectoryName(targetPath.TrimEnd(Path.DirectorySeparatorChar)) ?? targetPath
            : Path.GetDirectoryName(targetPath);

        string projectRoot = dir ?? "";
        DateTime lastTouchUtc = fallbackLastWriteUtc;

        try
        {
            string? current = dir;
            for (int depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (IsProtectedRoot(current)) break;

                string gitDir = Path.Combine(current, ".git");
                if (Directory.Exists(gitDir))
                {
                    projectRoot = current;
                    string gitHeadLog = Path.Combine(gitDir, "logs", "HEAD");
                    string gitIndex = Path.Combine(gitDir, "index");
                    if (File.Exists(gitHeadLog))
                        lastTouchUtc = File.GetLastWriteTimeUtc(gitHeadLog);
                    else if (File.Exists(gitIndex))
                        lastTouchUtc = File.GetLastWriteTimeUtc(gitIndex);
                    else
                        lastTouchUtc = Directory.GetLastWriteTimeUtc(current);
                    break;
                }

                bool hasProjectMarker = File.Exists(Path.Combine(current, "package.json"))
                    || File.Exists(Path.Combine(current, "Cargo.toml"))
                    || File.Exists(Path.Combine(current, "pyproject.toml"));
                if (hasProjectMarker)
                {
                    projectRoot = current;
                    lastTouchUtc = Directory.GetLastWriteTimeUtc(current);
                    break;
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch { }

        int daysUntouched = Math.Max(0, (int)(DateTime.UtcNow - lastTouchUtc).TotalDays);
        string status = daysUntouched switch
        {
            <= 14 => "ACTIVE",
            <= 90 => "DORMANT",
            _ => "ABANDONED"
        };

        return (status, daysUntouched, projectRoot);
    }
}
