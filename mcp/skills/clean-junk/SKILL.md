---
name: clean-junk
description: >-
  Identify and recycle safe junk files (redundant duplicates, obsolete installers)
  to the Recycle Bin using Vacumify. Use when the user types /clean-junk,
  "clean safe junk", or wants quick 1-click safe cleanup.
---

# /clean-junk

Safely sweeps confirmed zero-risk junk files and moves them directly to the OS Recycle Bin.

## Execution Workflow

1. **Target Directory**:
   - Use specified path or current workspace.

2. **Identify Safe Junk & Temp Scratchpads**:
   Call the lazy MCP tool `vacumify_get_safe_junk`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_get_safe_junk"
   - `Arguments`: `{"path": "<target_path>"}`

   For temporary files, call `vacumify_clean_temp` with `dryRun: true`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_clean_temp"
   - `Arguments`: `{"olderThanHours": 48, "dryRun": true}`

3. **Present Preview**:
   - Show candidate files, size, and reason (e.g. redundant duplicate copy, obsolete temporary installer, stale temporary files >48h).
   - If user requested preview or dry run, stop here and ask for confirmation.

4. **Recycle or Purge**:
   When confirmed:
   - For user junk and duplicates, call `vacumify_recycle_items`:
     - `ServerName`: "vacumify"
     - `ToolName`: "vacumify_recycle_items"
     - `Arguments`: `{"paths": ["<path1>", "<path2>"], "dryRun": false}`
   - For stale temp files, call `vacumify_clean_temp`:
     - `ServerName`: "vacumify"
     - `ToolName`: "vacumify_clean_temp"
     - `Arguments`: `{"olderThanHours": 48, "dryRun": false}`
   - Report space reclaimed and confirm items were moved to the Recycle Bin or purged safely.

## Rules
- System critical paths are strictly protected and blocked by the tool.
- All deletions use the OS Recycle Bin so any item can be restored if desired.
- Do NOT use emojis anywhere in the output.
