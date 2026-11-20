---
name: vacumify
description: >-
  Universal storage cleaner and storage audit command center powered by Vacumify.
  Use when the user types /vacumify, "vacumify", or wants an overview of all
  available disk cleaning capabilities and commands.
---

# /vacumify

Central command center for Vacumify storage cleaning and disk auditing tools.

## Available Slash Commands

- `/audit-storage`: Full disk scan with concise executive summary and zero-risk recommendations.
- `/forgotten` (or `/forgotten-files`): Scan for files untouched for 90+ days, sorted by largest size first.
- `/large` (or `/large-files`): Identify massive files (100 MB+) eating up storage.
- `/duplicates`: Find identical duplicate files and separate original files from safe-to-delete copies.
- `/dev-caches`: Scan developer project caches (`node_modules`, `.next`, `target`, `bin`, `obj`) classified by Git activity (Active, Dormant, Abandoned).
- `/clean-junk`: Safely recycle confirmed zero-risk junk directly to the OS Recycle Bin.

## How to Run

1. Type any of the commands above directly in the chat prompt.
2. You can optionally append a directory path, for example:
   - `/audit-storage C:\Users\HP LAPTOP 15s\Downloads`
   - `/dev-caches C:\Users\HP LAPTOP 15s\Projects`
   - `/duplicates`

## Safety Guarantees
- Zero permanent file destruction: all recycled items are sent to the Windows Recycle Bin.
- Active project protection: files and caches in active git repositories are protected from automated cleanup.
- System directory blacklist: critical Windows and OS system paths are blocked.
- Do NOT use emojis anywhere in the output.
