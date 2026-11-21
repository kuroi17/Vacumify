---
name: audit-storage
description: >-
  Audit local disk storage and scan for reclaimable space safely using Vacumify.
  Use when the user types /audit-storage, "audit storage", "scan disk", or wants
  a clear executive summary of reclaimable storage without raw table clutter.
---

# /audit-storage

Performs a high-speed disk audit using Vacumify MCP and presents a clear, decision-fatigue-free storage assessment.

## Execution Workflow

1. **Target Directory**:
   - If user provided a path, use it.
   - Otherwise, use the current workspace or user home directory.

2. **Execute Scan Tool**:
   Call the lazy MCP tool `vacumify_scan`:
   - `ServerName`: "vacumify"
   - `ToolName`: "vacumify_scan"
   - `Arguments`: `{"path": "<target_path>"}`

3. **Format Output**:
   Present the result in a crisp, readable structure:
   - **Executive Summary**:
     - Total space scanned and total reclaimable space found.
     - Breakdown by category (Dev Caches, Duplicates, Large Untouched Files, System Temp Scratchpads).
   - **Zero-Risk Wins**:
     - List top 3 to 5 highest-impact safe items (abandoned build caches, exact duplicate copies, obsolete installers, stale temp files >48h).
     - Show exact path, size, and reason why it is safe.
   - **Review Needed**:
     - 1 to 3 items where user confirmation is recommended (e.g. large project archives or dormant work).
   - **Action Shortcuts**:
     - Mention `/clean-junk` to recycle verified safe files to the Recycle Bin and purge stale temp scratchpads.
     - Mention `/dev-caches` to inspect development build caches in detail.
     - Mention `/duplicates` to inspect identical duplicate file groups.

## Rules
- Do NOT dump raw tables of dozens or hundreds of files.
- Keep output concise, structured, and focused on decisions.
- Do NOT use emojis anywhere in the output.
