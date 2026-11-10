# Storage Cleaner

A local-first Windows WPF utility for reviewing old-access, identical duplicate, and large files. It does not upload scan data, run in the background, or delete anything automatically.

## Build and run (Windows)

Requires the .NET 10 SDK and Windows Desktop targeting support.

```powershell
cd StorageCleaner
dotnet run --project .\StorageCleaner\StorageCleaner.csproj
```

To publish a self-contained Windows x64 folder:

```powershell
dotnet publish .\StorageCleaner\StorageCleaner.csproj -c Release -r win-x64 --self-contained true
```

## Use

1. Enter existing folder or drive paths, one per line, or use **Add folder**.
2. Start a scan. Scanning is recursive and can be cancelled.
3. Review each tab. Forgotten-file age defaults to 30 days and offers 7/15/30/60/90-day options. Large-file size defaults to 100 MB and offers 100 MB/500 MB/1 GB/5 GB options. A shared extension filter accepts comma-separated types such as `.pdf,.docx` (or `*` for all).
4. Select only files you intend to remove using the checkbox. Nothing is preselected.
5. **Open location** reveals the selected file in Explorer. Cleanup asks for confirmation and sends files to the Windows Recycle Bin.

Duplicate groups are determined by equal file size followed by streaming SHA-256 hashing. The duplicate view lists each path in a matching group; the user decides which copies to retain.

## Safety and current scope

- Directory junctions and symbolic links are skipped rather than followed. Inaccessible paths are reported; Windows access controls are not bypassed.
- Windows, Program Files, and common application-data roots are excluded from recursive scanning by default and protected from deletion.
- File metadata and hashes are read locally. No database, telemetry, cloud processing, background monitoring, or persistent scan history is included in this MVP.
- Windows may disable or defer updates to last-access timestamps. “Forgotten” is a date-based review hint, not a claim that a file is unused.
- Saved scan history, advanced scan mode, and localization are not included in this first implementation.
- Changes to files during scanning are tolerated as best-effort: paths are checked again before cleanup, and failures are reported rather than forcibly handled.

## Project structure

- `StorageCleaner/StorageCleaner.csproj` — Windows WPF project targeting .NET 10.
- `StorageCleaner/App.xaml` — app startup and minimal shared styling.
- `StorageCleaner/MainWindow.xaml` — dashboard and result grids.
- `StorageCleaner/MainWindow.xaml.cs` — UI workflow and confirmed cleanup.
- `StorageCleaner/Scanner.cs` — recursive traversal, metadata, protected-path checks, and duplicate hashing.
