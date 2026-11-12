#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const readline = require('readline');
const scanner = require('../lib/scanner');

const SERVER_NAME = 'vacumify';
const SERVER_VERSION = '1.2.0';
const PROTOCOL_VERSION = '2024-11-05';
const MS_PER_DAY = 24 * 60 * 60 * 1000;

const TOOLS = [
  {
    name: 'vacumify_scan',
    description:
      'Scan a local folder to identify large files, duplicate file groups, forgotten files, and developer caches with Active/Dormant/Abandoned project context. Read-only operation.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Directory path to scan (e.g. C:\\Users\\Name\\Downloads or /home/user/Projects).'
        },
        includeHidden: {
          type: 'boolean',
          description: 'Include hidden files and folders (default: false).'
        },
        minDuplicateBytes: {
          type: 'integer',
          description: 'Minimum file size in bytes for duplicate detection (default: 102400 = 100 KB).'
        },
        minLargeFileBytes: {
          type: 'integer',
          description: 'Minimum file size in bytes to flag as a large file (default: 104857600 = 100 MB).'
        },
        forgottenDays: {
          type: 'integer',
          description: 'Days since last access/modification to flag a file as forgotten (default: 90).'
        }
      },
      required: ['path']
    }
  },
  {
    name: 'vacumify_get_dev_caches',
    description:
      'Scan a workspace or Projects directory specifically for reclaimable developer caches (node_modules, .NET bin/obj, .venv, Rust/Maven target, __pycache__, Gradle, .next, dist, build) with Active/Dormant/Abandoned git activity status.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Root workspace or Projects directory path to scan.'
        },
        olderThanDays: {
          type: 'integer',
          description: 'Optional filter: only return caches in projects untouched for at least N days.'
        }
      },
      required: ['path']
    }
  },
  {
    name: 'vacumify_get_safe_junk',
    description:
      'Identify safe-to-delete junk candidates in a folder: redundant duplicate copies (preserving original) and obsolete installers (.msi/.exe/.dmp/old .log untouched >30 days).',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Directory path to analyze.'
        }
      },
      required: ['path']
    }
  },
  {
    name: 'vacumify_recycle_items',
    description:
      'Safely move specified files or folders into the OS Recycle Bin / System Trash. NEVER permanently deletes files, and blocks critical OS/system paths.',
    inputSchema: {
      type: 'object',
      properties: {
        paths: {
          type: 'array',
          items: { type: 'string' },
          description: 'List of absolute file or directory paths to move to Recycle Bin / Trash.'
        },
        dryRun: {
          type: 'boolean',
          description: 'If true, preview what would be recycled without moving any files (default: false).'
        }
      },
      required: ['paths']
    }
  }
];

const PROMPTS = [
  {
    name: 'audit-storage',
    description: 'Comprehensive storage audit covering dev caches, duplicates, forgotten files, and large files.',
    arguments: [
      {
        name: 'path',
        description: 'Directory path to audit (optional; defaults to asking or current workspace).',
        required: false
      }
    ]
  },
  {
    name: 'duplicates',
    description: 'Find duplicate file groups and safely recycle redundant copies.',
    arguments: [
      {
        name: 'path',
        description: 'Directory path to scan for duplicates (optional).',
        required: false
      }
    ]
  },
  {
    name: 'forgotten-files',
    description: 'Find untouched/forgotten files older than a configurable day threshold (default 90 days).',
    arguments: [
      {
        name: 'path',
        description: 'Directory path to inspect (optional).',
        required: false
      },
      {
        name: 'days',
        description: 'Inactivity threshold in days (default: 90).',
        required: false
      }
    ]
  },
  {
    name: 'large-files',
    description: 'Find oversized files exceeding a size threshold (default 100 MB).',
    arguments: [
      {
        name: 'path',
        description: 'Directory path to inspect (optional).',
        required: false
      },
      {
        name: 'minSizeMb',
        description: 'Minimum file size in MB (default: 100).',
        required: false
      }
    ]
  },
  {
    name: 'dev-caches',
    description: 'Scan workspace for bloated developer caches scored by Active/Dormant/Abandoned project activity.',
    arguments: [
      {
        name: 'path',
        description: 'Workspace or Projects directory path (optional).',
        required: false
      },
      {
        name: 'olderThanDays',
        description: 'Optional minimum days untouched filter.',
        required: false
      }
    ]
  }
];

function sendMessage(msg) {
  process.stdout.write(JSON.stringify(msg) + '\n');
}

function sendResult(id, result) {
  sendMessage({ jsonrpc: '2.0', id, result });
}

function sendError(id, code, message, data) {
  const err = { code, message };
  if (data !== undefined) err.data = data;
  sendMessage({ jsonrpc: '2.0', id, error: err });
}

function toolTextResult(payload, isError = false) {
  return {
    content: [
      {
        type: 'text',
        text: typeof payload === 'string' ? payload : JSON.stringify(payload, null, 2)
      }
    ],
    ...(isError ? { isError: true } : {})
  };
}

function resolveValidDir(inputPath) {
  if (!inputPath || typeof inputPath !== 'string' || !inputPath.trim()) {
    throw new Error('Missing required "path" argument.');
  }
  const resolved = path.resolve(inputPath.trim());
  if (!fs.existsSync(resolved)) {
    throw new Error(`Directory not found: ${resolved}`);
  }
  const stat = fs.statSync(resolved);
  if (!stat.isDirectory()) {
    throw new Error(`Path is not a directory: ${resolved}`);
  }
  return resolved;
}

function enrichActivity(targetPath, fallbackMs) {
  const act = scanner.getProjectActivity(targetPath, fallbackMs);
  return {
    activityStatus: act.status,
    daysUntouched: act.daysUntouched
  };
}

async function handleVacumifyScan(args) {
  const targetPath = resolveValidDir(args.path);
  const includeHidden = Boolean(args.includeHidden);
  const minDuplicateBytes = Number.isFinite(args.minDuplicateBytes)
    ? Math.max(1, Number(args.minDuplicateBytes))
    : 100 * 1024;
  const minLargeFileBytes = Number.isFinite(args.minLargeFileBytes)
    ? Math.max(1, Number(args.minLargeFileBytes))
    : 100 * 1024 * 1024;
  const forgottenDays = Number.isFinite(args.forgottenDays)
    ? Math.max(1, Number(args.forgottenDays))
    : 90;

  const result = await scanner.scanDirectory(targetPath, {
    includeHidden,
    minDuplicateBytes,
    fastLargeSampling: true
  });

  const nowMs = Date.now();
  const forgottenCutoffMs = nowMs - forgottenDays * MS_PER_DAY;

  const topLargeFiles = result.files
    .filter((f) => f.size >= minLargeFileBytes)
    .sort((a, b) => b.size - a.size)
    .slice(0, 25)
    .map((f) => ({
      path: f.path,
      name: f.name,
      size: f.size,
      sizeFormatted: scanner.formatBytes(f.size),
      lastWrite: scanner.formatDate(f.lastWriteMs),
      lastAccess: scanner.formatDate(f.lastAccessMs),
      ...enrichActivity(f.path, f.lastWriteMs)
    }));

  const forgottenAll = result.files.filter(
    (f) => Math.max(f.lastAccessMs || 0, f.lastWriteMs || 0) < forgottenCutoffMs
  );
  const topForgottenFiles = forgottenAll
    .sort((a, b) => b.size - a.size)
    .slice(0, 25)
    .map((f) => {
      const lastTouchMs = Math.max(f.lastAccessMs || 0, f.lastWriteMs || 0);
      const daysIdle = Math.max(0, Math.floor((nowMs - lastTouchMs) / MS_PER_DAY));
      return {
        path: f.path,
        name: f.name,
        size: f.size,
        sizeFormatted: scanner.formatBytes(f.size),
        daysIdle,
        lastAccess: scanner.formatDate(f.lastAccessMs),
        lastWrite: scanner.formatDate(f.lastWriteMs),
        ...enrichActivity(f.path, f.lastWriteMs)
      };
    });

  const groupsMap = new Map();
  for (const f of result.files) {
    if (!f.duplicateGroupId) continue;
    let list = groupsMap.get(f.duplicateGroupId);
    if (!list) {
      list = [];
      groupsMap.set(f.duplicateGroupId, list);
    }
    list.push(f);
  }

  const topDuplicateGroups = Array.from(groupsMap.entries())
    .map(([groupId, items]) => {
      const sorted = items.slice().sort((a, b) => (a.lastWriteMs || 0) - (b.lastWriteMs || 0));
      const unitSize = sorted[0]?.size || 0;
      const reclaimableBytes = unitSize * Math.max(0, sorted.length - 1);
      const anchor = sorted[0];
      return {
        groupId,
        fileCount: sorted.length,
        unitSize,
        reclaimableBytes,
        reclaimableFormatted: scanner.formatBytes(reclaimableBytes),
        ...enrichActivity(anchor.path, anchor.lastWriteMs),
        paths: sorted.map((x) => x.path)
      };
    })
    .sort((a, b) => b.reclaimableBytes - a.reclaimableBytes)
    .slice(0, 20);

  const devCaches = result.devCaches
    .slice()
    .sort((a, b) => b.size - a.size)
    .slice(0, 30)
    .map((c) => ({
      path: c.path,
      projectName: c.projectName,
      cacheType: c.cacheType,
      size: c.size,
      sizeFormatted: scanner.formatBytes(c.size),
      lastModified: scanner.formatDate(c.lastModifiedMs),
      ...enrichActivity(c.path, c.lastModifiedMs)
    }));

  return {
    scannedPath: targetPath,
    totalFiles: result.files.length,
    totalBytes: result.bytesScanned,
    totalFormatted: scanner.formatBytes(result.bytesScanned),
    conditions: {
      forgottenDays,
      minLargeFileBytes,
      minDuplicateBytes,
      includeHidden
    },
    reclaimableDuplicateBytes: result.reclaimableDuplicateBytes,
    reclaimableDuplicateFormatted: scanner.formatBytes(result.reclaimableDuplicateBytes),
    duplicateGroupCount: groupsMap.size,
    forgottenFilesCount: forgottenAll.length,
    devCacheCount: result.devCaches.length,
    devCacheTotalBytes: result.devCaches.reduce((acc, c) => acc + c.size, 0),
    devCacheTotalFormatted: scanner.formatBytes(result.devCaches.reduce((acc, c) => acc + c.size, 0)),
    topLargeFiles,
    topForgottenFiles,
    topDuplicateGroups,
    devCaches,
    errorsCount: result.errors.length
  };
}

async function handleGetDevCaches(args) {
  const targetPath = resolveValidDir(args.path);
  const olderThanDays =
    args.olderThanDays !== undefined && args.olderThanDays !== null
      ? Math.max(0, Number(args.olderThanDays))
      : null;

  const result = await scanner.scanDirectory(targetPath, {
    includeHidden: false,
    minDuplicateBytes: 100 * 1024,
    fastLargeSampling: true
  });

  let caches = result.devCaches.map((c) => {
    const activity = scanner.getProjectActivity(c.path, c.lastModifiedMs);
    return {
      path: c.path,
      projectName: c.projectName,
      cacheType: c.cacheType,
      size: c.size,
      sizeFormatted: scanner.formatBytes(c.size),
      lastModified: scanner.formatDate(c.lastModifiedMs),
      activityStatus: activity.status,
      daysUntouched: activity.daysUntouched,
      projectRoot: activity.projectRoot
    };
  });

  if (olderThanDays !== null && Number.isFinite(olderThanDays)) {
    caches = caches.filter((c) => c.daysUntouched >= olderThanDays);
  }

  caches.sort((a, b) => b.size - a.size);
  const totalBytes = caches.reduce((acc, c) => acc + c.size, 0);

  return {
    scannedPath: targetPath,
    olderThanDays,
    cacheCount: caches.length,
    totalBytes,
    totalFormatted: scanner.formatBytes(totalBytes),
    caches
  };
}

async function handleGetSafeJunk(args) {
  const targetPath = resolveValidDir(args.path);
  const result = await scanner.scanDirectory(targetPath, {
    includeHidden: false,
    minDuplicateBytes: 100 * 1024,
    fastLargeSampling: true
  });

  const junk = scanner.identifySafeJunk(result.files);
  const totalBytes = junk.reduce((acc, f) => acc + f.size, 0);

  return {
    scannedPath: targetPath,
    candidateCount: junk.length,
    totalBytes,
    totalFormatted: scanner.formatBytes(totalBytes),
    candidates: junk
      .slice()
      .sort((a, b) => b.size - a.size)
      .slice(0, 100)
      .map((f) => ({
        path: f.path,
        name: f.name,
        size: f.size,
        sizeFormatted: scanner.formatBytes(f.size),
        reason: f.duplicateGroupId ? 'redundant_duplicate' : 'obsolete_installer_or_dump',
        lastWrite: scanner.formatDate(f.lastWriteMs)
      }))
  };
}

async function handleRecycleItems(args) {
  if (!Array.isArray(args.paths) || args.paths.length === 0) {
    throw new Error('Argument "paths" must be a non-empty array of absolute file/folder paths.');
  }
  const dryRun = Boolean(args.dryRun);
  return await scanner.recycleItems(args.paths, dryRun);
}

function getPromptResponse(name, args = {}) {
  const target = args.path ? `"${args.path}"` : 'the target folder (ask the user if not specified)';
  switch (name) {
    case 'audit-storage':
      return {
        description: 'Comprehensive storage audit using Vacumify MCP tools.',
        messages: [
          {
            role: 'user',
            content: {
              type: 'text',
              text:
                `Run a full storage audit on ${target} using \`vacumify_scan\` and \`vacumify_get_dev_caches\`.\n` +
                'Summarize reclaimed-space opportunities across four buckets:\n' +
                '1. Developer caches (node_modules, .NET bin/obj, .venv, target, etc.) grouped by project activity (ACTIVE / DORMANT / ABANDONED).\n' +
                '2. Redundant duplicate file groups (keep oldest original, list redundant copies).\n' +
                '3. Forgotten files untouched for 90+ days (`forgottenDays`).\n' +
                '4. Large files exceeding 100 MB (`minLargeFileBytes`).\n' +
                'Do NOT recycle anything without explicit user confirmation. When approved, always move items via `vacumify_recycle_items` (Recycle Bin / System Trash, never permanent delete).'
            }
          }
        ]
      };
    case 'duplicates':
      return {
        description: 'Find duplicate file groups and safely recycle redundant copies.',
        messages: [
          {
            role: 'user',
            content: {
              type: 'text',
              text:
                `Scan ${target} for duplicate files using \`vacumify_scan\` (respecting \`minDuplicateBytes\`, default 100 KB) and \`vacumify_get_safe_junk\`.\n` +
                'Present each duplicate group clearly with the preserved original copy and redundant copies, sorted by reclaimable size.\n' +
                'Ask the user before calling `vacumify_recycle_items` so redundant copies go safely to Recycle Bin / System Trash.'
            }
          }
        ]
      };
    case 'forgotten-files': {
      const days = args.days || '90';
      return {
        description: 'Find untouched files exceeding a day threshold.',
        messages: [
          {
            role: 'user',
            content: {
              type: 'text',
              text:
                `Scan ${target} using \`vacumify_scan\` with \`forgottenDays: ${days}\`.\n` +
                `List the top forgotten files untouched for at least ${days} days, including their size, last-accessed date, and project activity status.\n` +
                'Confirm with the user before recycling any files via `vacumify_recycle_items`.'
            }
          }
        ]
      };
    }
    case 'large-files': {
      const minMb = args.minSizeMb || '100';
      const minBytes = Math.max(1, Number(minMb) || 100) * 1024 * 1024;
      return {
        description: 'Find oversized files above a configurable MB threshold.',
        messages: [
          {
            role: 'user',
            content: {
              type: 'text',
              text:
                `Scan ${target} using \`vacumify_scan\` with \`minLargeFileBytes: ${minBytes}\` (${minMb} MB).\n` +
                'Rank the largest files with sizes, last modified dates, and activity context.\n' +
                'Confirm with the user before recycling any files via `vacumify_recycle_items`.'
            }
          }
        ]
      };
    }
    case 'dev-caches': {
      const olderClause = args.olderThanDays
        ? ` with \`olderThanDays: ${args.olderThanDays}\``
        : '';
      return {
        description: 'Find bloated developer caches with Active/Dormant/Abandoned context.',
        messages: [
          {
            role: 'user',
            content: {
              type: 'text',
              text:
                `Scan ${target} using \`vacumify_get_dev_caches\`${olderClause}.\n` +
                'Highlight ABANDONED and DORMANT project caches first while warning against deleting ACTIVE workspace dependencies unless the user confirms.\n' +
                'Always use `vacumify_recycle_items` (Recycle Bin / System Trash) when cleaning approved caches.'
            }
          }
        ]
      };
    }
    default:
      return null;
  }
}

async function handleRequest(msg) {
  const { id, method, params } = msg;
  const isNotification = id === undefined || id === null;

  try {
    switch (method) {
      case 'initialize': {
        sendResult(id, {
          protocolVersion: PROTOCOL_VERSION,
          capabilities: {
            tools: {},
            prompts: {}
          },
          serverInfo: {
            name: SERVER_NAME,
            version: SERVER_VERSION
          }
        });
        return;
      }
      case 'notifications/initialized':
      case 'initialized':
        return;
      case 'ping': {
        if (!isNotification) sendResult(id, {});
        return;
      }
      case 'tools/list': {
        sendResult(id, { tools: TOOLS });
        return;
      }
      case 'tools/call': {
        const toolName = params?.name;
        const args = params?.arguments || {};
        try {
          let data;
          if (toolName === 'vacumify_scan') {
            data = await handleVacumifyScan(args);
          } else if (toolName === 'vacumify_get_dev_caches') {
            data = await handleGetDevCaches(args);
          } else if (toolName === 'vacumify_get_safe_junk') {
            data = await handleGetSafeJunk(args);
          } else if (toolName === 'vacumify_recycle_items') {
            data = await handleRecycleItems(args);
          } else {
            sendError(id, -32601, `Unknown tool: ${toolName}`);
            return;
          }
          sendResult(id, toolTextResult(data));
        } catch (toolErr) {
          sendResult(id, toolTextResult({ error: toolErr.message || String(toolErr) }, true));
        }
        return;
      }
      case 'prompts/list': {
        sendResult(id, { prompts: PROMPTS });
        return;
      }
      case 'prompts/get': {
        const promptName = params?.name;
        const promptRes = getPromptResponse(promptName, params?.arguments || {});
        if (!promptRes) {
          sendError(id, -32602, `Unknown prompt: ${promptName}`);
          return;
        }
        sendResult(id, promptRes);
        return;
      }
      default: {
        if (!isNotification) {
          sendError(id, -32601, `Method not found: ${method}`);
        }
      }
    }
  } catch (err) {
    if (!isNotification) {
      sendError(id, -32603, err?.message || 'Internal error');
    }
  }
}

const rl = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity
});

let pending = Promise.resolve();
rl.on('line', (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  let parsed;
  try {
    parsed = JSON.parse(trimmed);
  } catch (e) {
    sendError(null, -32700, 'Parse error');
    return;
  }
  pending = pending.then(() => handleRequest(parsed));
});
