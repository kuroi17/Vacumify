# Vacumify Model Context Protocol (MCP) Server

Connect **Vacumify** directly to AI agent environments such as **Claude Code**, **Claude Desktop**, **Cursor**, **Windsurf**, and **Google Antigravity**.

Let your AI agent inspect disk health, hunt for bloated dev caches (`node_modules`, `venv`, `bin`/`obj`), detect identical duplicate files via SHA-256 fingerprinting, and safely recycle clutter using Vacumify's parallel multi-core C# engine.

---

## Exposed MCP Tools

1. **`vacumify_scan`**: Parallel multi-core scan of any directory. Returns total files, scanned bytes, duplicate groups, reclaimable duplicate bytes, and top heavy files.
2. **`vacumify_get_dev_caches`**: Detects dormant developer artifact folders (`node_modules`, Python `venv`/`.venv`, Rust `target`, .NET `bin`/`obj`, Gradle) with sizes and project names without enumerating hundreds of thousands of subfiles.
3. **`vacumify_get_safe_junk`**: Identifies 100% zero-risk safe junk: redundant duplicate copies (preserving copy #1 intact), obsolete downloads installers (>30d untouched), and crash dumps.
4. **`vacumify_recycle_items`**: Safely moves specified files or folders to the **Windows Recycle Bin** (includes `dryRun: true` preview support).

---

## Configuration Guides

### 1. Claude Desktop (`claude_desktop_config.json`)
```json
{
  "mcpServers": {
    "vacumify": {
      "command": "C:\\path\\to\\Vacumify.exe",
      "args": ["--mcp"]
    }
  }
}
```

### 2. Claude Code (`~/.claude.json` or project `.claude.json`)
```bash
claude mcp add vacumify -- C:\path\to\Vacumify.exe --mcp
```

### 3. Cursor IDE (`Settings > Features > MCP`)
- **Name**: `vacumify`
- **Type**: `command`
- **Command**: `C:\path\to\Vacumify.exe --mcp`

### 4. Running via npx / Node wrapper
```bash
# Set path or have Vacumify.exe in PATH:
set VACUMIFY_PATH=C:\path\to\Vacumify.exe
npx vacumify-mcp
```

