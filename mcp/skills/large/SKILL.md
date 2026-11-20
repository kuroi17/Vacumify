---
name: large
description: >-
  Identify massive files (100 MB+) consuming storage using Vacumify.
  Use when the user types /large, /large-files, "large files", "biggest files",
  or asks what is eating up disk space.
---

# /large

Scans for files over 100 MB and categorizes them by file type and recency.

## Execution Workflow

1. **Target Directory**:
   - Use specified path or current workspace / user directory.

2. **Execute Scan**:
   Call the lazy MCP tool `vacumify_scan`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_scan"
   - `Arguments`: `{"path": "<target_path>", "minLargeFileBytes": 104857600}`

3. **Format Output**:
   - **Summary**: Total large files found and combined size.
   - **Top Largest Files (Top 5)**:
     - File name, size, type (Video, Installer, Archive, Virtual Disk, DB).
     - Last accessed / modified date to gauge active use.
     - Safety verdict (e.g. Old installer = Safe to remove, Work project file = Review).
   - **Recommended Action**:
     - Point out any safe-to-delete large items.

## Rules
- Keep output concise and decision-focused.
- Do NOT use emojis anywhere in the output.
