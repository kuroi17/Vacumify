# Vacumify — Desktop Storage Cleaner

A lightweight, local-first Windows WPF utility for discovering and safely cleaning forgotten, duplicate, and large files.

Vacumify operates 100% locally: zero telemetry, zero cloud uploads, no background daemons, and zero automated deletions.

---

## Key Features

1. **Forgotten Files Tracker**:
   - Filter files by inactivity threshold (7, 15, 30, 60, or 90 days).
   - Instant "Select all" and "Deselect all" helper controls.

2. **2-Phase Duplicate Finder**:
   - **Fast Pre-check**: Groups files by exact byte size, then checks a fast 4 KB partial hash.
   - **Full Verification**: Only candidate files sharing identical partial hashes undergo full SHA-256 streaming hashing.
   - **Smart Selection**: One-click actions to **"Keep newest in each group"** or **"Keep oldest in each group"**.
   - Displays clear Group IDs and matching copy counts.

3. **Large Files Analyzer**:
   - Identify storage hogs with size thresholds (100 MB, 500 MB, 1 GB, 5 GB).
   - Filter by file extension (`*` for all, or e.g. `.mp4,.iso,.zip`).

4. **Safety & Recycle Bin Integration**:
   - Critical system directories (`C:\Windows`, `Program Files`, `ProgramData`) are excluded from scanning and deletion.
   - Symlinks and directory junctions are skipped to prevent infinite loops.
   - Files are moved to the **Windows Recycle Bin** rather than permanently deleted.
   - Deletion summary displays exact reclaimable space.

---

## How to Run

### Requirements
- Windows 10 / 11
- .NET 10 SDK

### 1. Run from source
Open PowerShell in the project directory:

```powershell
cd "C:\Users\HP LAPTOP 15s\Vacumify"
dotnet run --project .\StorageCleaner\StorageCleaner\StorageCleaner.csproj
```

### 2. Build Release Standalone Executable
To publish a self-contained single folder / executable that does not require .NET to be pre-installed:

```powershell
dotnet publish .\StorageCleaner\StorageCleaner\StorageCleaner.csproj -c Release -r win-x64 --self-contained true
```
The executable will be generated at:
`StorageCleaner\StorageCleaner\bin\Release\net10.0-windows\win-x64\publish\Vacumify.exe`

