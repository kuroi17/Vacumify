---
name: forgotten
description: >-
  Find forgotten files untouched for 90+ days using Vacumify.
  Use when the user types /forgotten, /forgotten-files, "forgotten files",
  or asks about untouched and dormant files to clean.
---

# /forgotten

Scans for files untouched for 90+ days, sorted by largest size first, filtering out OS noise.

## Execution Workflow

1. **Target Directory**:
   - Use specified path or current workspace / user directory.

2. **Execute Scan**:
   Call the lazy MCP tool `vacumify_scan`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_scan"
   - `Arguments`: `{"path": "<target_path>", "forgottenDays": 90}`

3. **Format Output**:
   - **Summary**: Total forgotten files count and total space occupied.
   - **Top Space Sinks (Top 5)**:
     - File name, size, path.
     - Days untouched and last modified / accessed date.
     - 1-line safety verdict (e.g. Obsolete installer, old build zip, dormant dataset).
   - **Recommended Action**:
     - Offer to recycle specific safe forgotten items via `vacumify_recycle_items`.

## Rules
- Ignore Windows system clutter (NTUSER.DAT, thumbs.db, desktop.ini).
- Prioritize largest files first so user sees immediate high-yield wins.
- Do NOT use emojis anywhere in the output.
