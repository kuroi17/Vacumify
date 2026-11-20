---
name: dev-caches
description: >-
  Audit developer build caches (node_modules, .next, target, bin, obj, .venv)
  with Git project activity context using Vacumify. Use when the user types
  /dev-caches, "clean build caches", or asks about dev project storage.
---

# /dev-caches

Scans for developer caches and classifies them by project activity status (Active, Dormant, Abandoned) so the user never deletes active project caches by mistake.

## Execution Workflow

1. **Target Directory**:
   - Use specified path or current workspace / development directory.

2. **Execute Dev Caches Scan**:
   Call the lazy MCP tool `vacumify_get_dev_caches`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_get_dev_caches"
   - `Arguments`: `{"path": "<target_path>"}`

3. **Format Output**:
   - **Summary**: Total dev cache space found.
   - **Activity Classification**:
     - **Abandoned (untouched >90 days)**: Highest priority for safe cleanup. Highlight total space recoverable without affecting active work.
     - **Dormant (untouched 30-90 days)**: Safe candidates if projects are archived or completed.
     - **Active (<30 days)**: Clearly marked as `[KEEP - ACTIVE WORK]` to protect ongoing development.
   - **Recommended Action**:
     - Offer to recycle abandoned caches to the Recycle Bin via `vacumify_recycle_items`.

## Rules
- Remind user that build caches can always be re-created via package managers (`npm install`, `cargo build`, `dotnet restore`).
- Do NOT use emojis anywhere in the output.
