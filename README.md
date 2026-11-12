<div align="center">

<img src="LogoModern.png" alt="Vacumify — Open-Source Windows Storage Cleaner & Duplicate File Finder" width="120" />

# Vacumify

**Fast, local-first Windows duplicate file finder, large file analyzer, and forgotten storage cleaner.**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-2563EB?style=flat-square)](https://github.com/kuroi17/Vacumify)
[![.NET](https://img.shields.io/badge/.NET-10%20WPF-1D4ED8?style=flat-square)](https://dotnet.microsoft.com/)
[![MCP](https://img.shields.io/badge/MCP-Enabled-9333EA?style=flat-square)](https://modelcontextprotocol.io/)
[![Privacy](https://img.shields.io/badge/Privacy-100%25%20Local%20%E2%80%A2%20No%20Telemetry-059669?style=flat-square)](#safety--privacy)

</div>

---

**Vacumify** is a lightweight, open-source Windows desktop utility for reclaiming disk space without bloat. Built with .NET 10 and Windows 11 Fluent WPF, it finds **identical duplicate files**, **large storage hogs**, and **forgotten untouched files** in seconds — with zero telemetry, zero background services, and safe Recycle Bin cleanup.

## Features

- **🤖 Model Context Protocol (MCP) Server** — Connect Vacumify directly to AI agents (**Claude Code**, **Cursor**, **Claude Desktop**, **Antigravity**). AI agents can scan workspaces, isolate heavy dev caches, and recycle junk through native JSON-RPC stdio tools (`vacumify --mcp`).
- **⚡ 1-Click Smart Clean ("Safe Junk Sweep")** — Automatically detects zero-risk safe junk: redundant duplicate copies (always keeping original copy #1 intact), obsolete installers in Downloads (>30 days untouched), and dead crash dumps. Reclaim gigabytes in a single confirmation.
- **🛠️ Dormant Dev Caches Hunter** — Detects bloated developer artifact folders (`node_modules`, Python `venv`/`.venv`, Rust `target`, .NET `bin`/`obj`, Gradle) without enumerating hundreds of thousands of tiny subfiles. Clean old project dependencies in seconds.
- **Sub-10s Hybrid Duplicate File Finder** — Parallel multi-core directory traversal, exact byte-size grouping, 1 KB prefix filtering, and SHA-256 verification (full-file SHA-256 for files ≤ 16 MB; 8-point distributed 512 KB fingerprinting or optional full SHA-256 for larger files).
- **Smart Duplicate Selection** — One-click **Keep newest**, **Keep oldest**, **Select all**, and **Deselect all** across grouped duplicates.
- **Forgotten Files Tracker** — Surface files not accessed in **7, 15, 30, 60, or 90 days** to spot old downloads, installers, and clutter.
- **Large File Analyzer** — Find heavy files (**100 MB+**, **500 MB+**, **1 GB+**, **5 GB+**) with live extension filtering (`.mp4,.zip,.iso`).
- **Flexible Scan Scope** — 1-click folder presets (`User Home`, `Downloads`, `Desktop`, `Documents`, `Videos`) plus an optional **Include hidden caches (`.*`)** deep scan toggle.
- **Storage Allocation Overview** — Instant breakdown of reclaimable duplicate space, large files, and forgotten files.

## Quick Start

### Prerequisites
- Windows 10 or Windows 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Run from Source
```powershell
git clone https://github.com/kuroi17/Vacumify.git
cd Vacumify
dotnet run --project .\StorageCleaner\StorageCleaner\StorageCleaner.csproj -c Release
```

### Build a Standalone `.exe`
Publish a self-contained Windows x64 build (no .NET runtime required on target PCs):

```powershell
dotnet publish .\StorageCleaner\StorageCleaner\StorageCleaner.csproj -c Release -r win-x64 --self-contained true
```

Output binary:
```text
StorageCleaner\StorageCleaner\bin\Release\net10.0-windows\win-x64\publish\Vacumify.exe
```

## AI Agent Integration (Model Context Protocol / MCP)

Vacumify includes a pure Node.js, zero-dependency **Model Context Protocol (MCP)** server (`vacumify-mcp`) over `stdio` for **Windows, macOS, and Linux**, allowing AI agents (**Claude Code**, **Cursor**, **Claude Desktop**, **Windsurf**, **Antigravity**) to audit disk space and safely recycle clutter without opening or installing the desktop GUI.

### Quick Install (npm / npx — Pure Node.js Standalone, No `.exe` Required)

Run directly with zero setup:
```bash
npx -y vacumify-mcp
```

Or install globally:
```bash
npm install -g vacumify-mcp
```

#### Claude Code CLI:
```bash
claude mcp add vacumify npx -y vacumify-mcp
```

#### Claude Desktop / Cursor (`config.json`):
```json
{
  "mcpServers": {
    "vacumify": {
      "command": "npx",
      "args": ["-y", "vacumify-mcp"]
    }
  }
}
```

*(Alternatively, point directly to your local executable via `"command": "C:\\path\\to\\Vacumify.exe", "args": ["--mcp"]` or click **Connect AI / MCP** in the GUI sidebar.)*

### 4 Exposed MCP Tools (with GUI Condition Parity & Project Activity Scoring):
* `vacumify_scan`: Parallel scan supporting `forgottenDays` (default 30), `minLargeFileBytes` (default 100 MB), `minDuplicateBytes` (default 100 KB), `includeHidden`, and **Active vs Abandoned project scoring**.
* `vacumify_get_dev_caches`: Detects dormant `node_modules`, Python `venv`, Rust `target`, .NET `bin`/`obj`, and Gradle caches with `olderThanDays` age filter and **Active vs Abandoned** protection.
* `vacumify_get_safe_junk`: Identifies 100% zero-risk safe junk (duplicate copies #2..N, obsolete downloads installers, crash dumps).
* `vacumify_recycle_items`: Safely sends files or folders to the **Windows Recycle Bin** (supports `dryRun: true`; never permanently deletes).

### 5 Native MCP Slash Prompts:
* `/audit-storage` — Complete overview audit across duplicates, large files, forgotten files, and dormant dev caches.
* `/duplicates` — Identify duplicate files with Keep Newest / Keep Oldest recommendation.
* `/forgotten-files` — Surface untouched files matching `forgottenDays` threshold (7d, 15d, 30d, 60d, 90d+).
* `/large-files` — Rank heavy storage hogs matching `minLargeFileMB` threshold (100 MB+, 500 MB+, 1 GB+).
* `/dev-caches` — Isolate bloated build caches while protecting active projects.

## Safety & Privacy

- **100% Local & Offline** — No cloud uploads, accounts, ads, telemetry, or background daemons.
- **Recycle Bin Only** — Files are never permanently wiped; confirmed items move to the Windows Recycle Bin.
- **Protected System Paths** — `C:\Windows`, `Program Files`, `ProgramData`, `AppData`, `.git`, and `node_modules` are skipped and guarded against deletion.
- **Loop-Safe Traversal** — Directory junctions and symbolic links are automatically skipped.

---

<div align="center">
  Built with <strong>C# · .NET 10 · WPF</strong>
</div>
