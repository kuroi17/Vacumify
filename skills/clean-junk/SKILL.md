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

2. **Identify Safe Junk**:
   Call the lazy MCP tool `vacumify_get_safe_junk`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_get_safe_junk"
   - `Arguments`: `{"path": "<target_path>"}`

3. **Present Preview**:
   - Show candidate files, size, and reason (e.g. redundant duplicate copy, obsolete temporary installer).
   - If user requested preview or dry run, stop here and ask for confirmation.

4. **Recycle**:
   When confirmed:
   - Call `vacumify_recycle_items`:
     - `ServerName`: "vacumify"
     - `ToolName`: "vacumify_recycle_items"
     - `Arguments`: `{"paths": ["<path1>", "<path2>"], "dryRun": false}`
   - Report space reclaimed and confirm items were moved to the Recycle Bin (not permanently destroyed).

## Rules
- System critical paths are strictly protected and blocked by the tool.
- All deletions use the OS Recycle Bin so any item can be restored if desired.
- Do NOT use emojis anywhere in the output.
