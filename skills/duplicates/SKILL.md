---
name: duplicates
description: >-
  Find and group identical duplicate files safely using Vacumify.
  Use when the user types /duplicates, "find duplicates", or asks about identical
  copies of files to reclaim wasted storage.
---

# /duplicates

Scans for duplicate files using Vacumify SHA-256 content hashing and presents clear original vs copy groups.

## Execution Workflow

1. **Target Directory**:
   - Use specified path or current workspace / user directory.

2. **Execute Scan**:
   Call the lazy MCP tool `vacumify_scan`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_scan"
   - `Arguments`: `{"path": "<target_path>", "minDuplicateBytes": 102400}`

3. **Format Output**:
   - **Summary**: Total number of duplicate groups and total wasted space.
   - **Top Duplicate Clusters**:
     - For each top duplicate cluster (top 3 to 5 by size):
       - File name and size.
       - Original file marked as `[KEEP]` (oldest or primary project location).
       - Redundant duplicate copies marked as `[SAFE TO RECYCLE]`.
   - **Recommended Action**:
     - Offer to send redundant copies to the OS Recycle Bin using `vacumify_recycle_items`.

## Rules
- Highlight the original file clearly so the user knows nothing important is lost.
- Keep output compact and readable.
- Do NOT use emojis anywhere in the output.
