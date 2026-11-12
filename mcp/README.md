# Vacumify Model Context Protocol (MCP) Server (`vacumify-mcp`)

Connect **Vacumify** directly to AI agent environments such as **Claude Code**, **Claude Desktop**, **Cursor**, **Windsurf**, and **Google Antigravity**.

Runs completely standalone over `stdio` without opening the desktop GUI. If `Vacumify.exe` is not found locally, `npx -y vacumify-mcp` automatically downloads the official Windows x64 binary from GitHub Releases into `~/.vacumify/`.

---

## Quick Install

### Run directly with npx
```bash
npx -y vacumify-mcp
```

### Global npm install
```bash
npm install -g vacumify-mcp
```

### Claude Code CLI
```bash
claude mcp add vacumify npx -y vacumify-mcp
```

### Claude Desktop / Cursor (`config.json`)
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

---

## Exposed MCP Tools

1. **`vacumify_scan`**: Parallel multi-core scan with full GUI condition parity:
   * `forgottenDays` (default `30`): Days untouched threshold.
   * `minLargeFileBytes` (default `104857600` / 100 MB): Large file size threshold.
   * `minDuplicateBytes` (default `102400` / 100 KB): Minimum duplicate hashing threshold.
   * Includes **Active vs Dormant vs Abandoned** project scoring so AI agents never suggest deleting active work.
2. **`vacumify_get_dev_caches`**: Detects `node_modules`, Python `venv`/`.venv`, Rust `target`, .NET `bin`/`obj`, and Gradle caches with `olderThanDays` age filter and **Active vs Abandoned** protection.
3. **`vacumify_get_safe_junk`**: Identifies 100% zero-risk safe junk: redundant duplicate copies (preserving copy #1 intact), obsolete downloads installers untouched for 30+ days, and dead crash dumps.
4. **`vacumify_recycle_items`**: Safely moves specified files or folders to the **Windows Recycle Bin** (supports `dryRun: true`; never permanently deletes).

---

## Exposed MCP Slash Prompts

* `/audit-storage` — Complete overview audit (duplicates, large files, forgotten files, dev caches).
* `/duplicates` — Identify duplicate files with Keep Newest / Keep Oldest recommendation.
* `/forgotten-files` — Surface untouched files matching `forgottenDays`.
* `/large-files` — Rank heavy storage hogs matching `minLargeFileMB`.
* `/dev-caches` — Isolate bloated build caches while protecting active projects.
