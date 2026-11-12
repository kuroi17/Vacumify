const fs = require('fs');
const fsp = fs.promises;
const path = require('path');
const crypto = require('crypto');
const os = require('os');
const { spawnSync } = require('child_process');

const LARGE_FILE_THRESHOLD = 16 * 1024 * 1024; // 16 MB
const PREFIX_BYTES = 1024;
const SAMPLE_BLOCK_BYTES = 64 * 1024;
const HASH_CONCURRENCY = 16;

const ALWAYS_EXCLUDED_FOLDERS = new Set([
  '.git',
  'system volume information',
  '$recycle.bin',
  'windows',
  'program files',
  'program files (x86)',
  'programdata'
]);

const HIDDEN_DEV_FOLDERS = new Set([
  '.cache',
  '.npm',
  '.yarn',
  '.pnpm-store',
  '.gradle',
  '.m2',
  '.nuget',
  '.cargo',
  '.rustup',
  '.docker',
  '.android',
  '.vscode',
  '.vs'
]);

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let idx = 0;
  let val = bytes;
  while (val >= 1024 && idx < units.length - 1) {
    val /= 1024;
    idx++;
  }
  return idx === 0 ? `${Math.round(val)} B` : `${val.toFixed(2).replace(/\.?0+$/, '')} ${units[idx]}`;
}

function formatDate(ms) {
  const d = new Date(ms);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function getProtectedRoots() {
  if (process.platform === 'win32') {
    const winDir = process.env.WINDIR || 'C:\\Windows';
    const progFiles = process.env.ProgramFiles || 'C:\\Program Files';
    const progFilesX86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
    const progData = process.env.ProgramData || 'C:\\ProgramData';
    const appData = process.env.APPDATA || '';
    const localAppData = process.env.LOCALAPPDATA || '';
    return [winDir, progFiles, progFilesX86, progData, appData, localAppData]
      .filter(Boolean)
      .map((p) => path.resolve(p).toLowerCase());
  }
  return ['/system', '/library', '/bin', '/sbin', '/usr', '/etc', '/var', '/dev', '/proc', '/sys']
    .map((p) => path.resolve(p).toLowerCase());
}

const PROTECTED_ROOTS = getProtectedRoots();

function isProtectedRoot(targetPath) {
  try {
    const norm = path.resolve(targetPath).toLowerCase();
    return PROTECTED_ROOTS.includes(norm);
  } catch {
    return true;
  }
}

function isProtectedPath(targetPath) {
  try {
    const norm = path.resolve(targetPath).toLowerCase();
    return PROTECTED_ROOTS.some((root) => norm === root || norm.startsWith(root + path.sep));
  } catch {
    return true;
  }
}

async function parentHasAnyFile(parentDir, patterns) {
  if (!parentDir) return false;
  try {
    const entries = await fsp.readdir(parentDir);
    for (const name of entries) {
      const lower = name.toLowerCase();
      for (const pat of patterns) {
        if (pat.startsWith('*.')) {
          if (lower.endsWith(pat.slice(1))) return true;
        } else if (lower === pat.toLowerCase()) {
          return true;
        }
      }
    }
  } catch {}
  return false;
}

async function isDevCacheFolder(folderName, parentDir) {
  const lower = folderName.toLowerCase();
  if (lower === 'node_modules') return 'node_modules';
  if (lower === 'venv' || lower === '.venv' || lower === '__pycache__') return 'Python venv';
  if (lower === '.gradle') return 'Gradle';

  if (lower === 'target' && (await parentHasAnyFile(parentDir, ['Cargo.toml', 'pom.xml']))) {
    return 'Rust target';
  }
  if ((lower === 'bin' || lower === 'obj') && (await parentHasAnyFile(parentDir, ['*.csproj', '*.fsproj', '*.vbproj', '*.sln']))) {
    return '.NET bin/obj';
  }
  return null;
}

async function fastGetDirectorySize(dirPath) {
  let total = 0;
  const queue = [dirPath];
  while (queue.length > 0) {
    const current = queue.pop();
    let entries;
    try {
      entries = await fsp.readdir(current, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue;
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        queue.push(full);
      } else if (entry.isFile()) {
        try {
          const st = await fsp.stat(full);
          total += st.size;
        } catch {}
      }
    }
  }
  return total;
}

function getExtension(fileName) {
  const ext = path.extname(fileName);
  return ext ? ext.slice(1).toUpperCase() : '';
}

async function computePrefixHash(filePath, size) {
  const len = Math.min(PREFIX_BYTES, size);
  const fh = await fsp.open(filePath, 'r');
  try {
    const buf = Buffer.allocUnsafe(len);
    const { bytesRead } = await fh.read(buf, 0, len, 0);
    return crypto.createHash('sha256').update(buf.subarray(0, bytesRead)).digest('hex');
  } finally {
    await fh.close();
  }
}

async function computeContentHash(filePath, size, fastLargeSampling = true) {
  if (!fastLargeSampling || size <= LARGE_FILE_THRESHOLD) {
    return new Promise((resolve, reject) => {
      const hash = crypto.createHash('sha256');
      const stream = fs.createReadStream(filePath, { highWaterMark: 128 * 1024 });
      stream.on('data', (chunk) => hash.update(chunk));
      stream.on('end', () => resolve(hash.digest('hex')));
      stream.on('error', reject);
    });
  }

  // 8-point distributed sampling for files > 16 MB
  const fh = await fsp.open(filePath, 'r');
  try {
    const hash = crypto.createHash('sha256');
    const sizeBuf = Buffer.allocUnsafe(8);
    sizeBuf.writeBigInt64LE(BigInt(size));
    hash.update(sizeBuf);

    const blocks = 8;
    const buf = Buffer.allocUnsafe(SAMPLE_BLOCK_BYTES);
    for (let i = 0; i < blocks; i++) {
      const offset = i === blocks - 1
        ? Math.max(0, size - SAMPLE_BLOCK_BYTES)
        : Math.floor((i * (size - SAMPLE_BLOCK_BYTES)) / (blocks - 1));
      const { bytesRead } = await fh.read(buf, 0, SAMPLE_BLOCK_BYTES, offset);
      if (bytesRead > 0) {
        hash.update(buf.subarray(0, bytesRead));
      }
    }
    return `SAMPLE:${hash.digest('hex')}`;
  } finally {
    await fh.close();
  }
}

async function mapConcurrent(items, limit, fn) {
  const results = new Array(items.length);
  let idx = 0;
  async function worker() {
    while (idx < items.length) {
      const i = idx++;
      results[i] = await fn(items[i], i);
    }
  }
  const workers = Array.from({ length: Math.min(limit, items.length) }, () => worker());
  await Promise.all(workers);
  return results;
}

async function scanDirectory(rootPath, options = {}) {
  const {
    includeHidden = false,
    minDuplicateBytes = 100 * 1024,
    fastLargeSampling = true
  } = options;

  const resolvedRoot = path.resolve(rootPath);
  if (!fs.existsSync(resolvedRoot)) {
    throw new Error(`Directory does not exist: ${rootPath}`);
  }

  const files = [];
  const devCaches = [];
  const errors = [];
  let bytesScanned = 0;

  const queue = [resolvedRoot];
  const seen = new Set([resolvedRoot.toLowerCase()]);

  while (queue.length > 0) {
    // Process batch concurrently
    const batch = queue.splice(0, 16);
    await Promise.all(batch.map(async (dir) => {
      let entries;
      try {
        entries = await fsp.readdir(dir, { withFileTypes: true });
      } catch (err) {
        errors.push(`${dir}: ${err.message}`);
        return;
      }

      for (const entry of entries) {
        if (entry.isSymbolicLink()) continue;
        const fullPath = path.join(dir, entry.name);

        if (entry.isDirectory()) {
          if (isProtectedRoot(fullPath)) continue;

          const cacheType = await isDevCacheFolder(entry.name, dir);
          if (cacheType) {
            try {
              const st = await fsp.stat(fullPath);
              const cacheSize = await fastGetDirectorySize(fullPath);
              const projName = path.basename(dir) || entry.name;
              devCaches.push({
                path: fullPath,
                projectName: projName,
                cacheType,
                size: cacheSize,
                lastModifiedMs: st.mtimeMs
              });
            } catch {}
            continue;
          }

          const lowerName = entry.name.toLowerCase();
          if (ALWAYS_EXCLUDED_FOLDERS.has(lowerName)) continue;
          if (!includeHidden && (entry.name.startsWith('.') || HIDDEN_DEV_FOLDERS.has(lowerName))) continue;

          const key = fullPath.toLowerCase();
          if (!seen.has(key)) {
            seen.add(key);
            queue.push(fullPath);
          }
        } else if (entry.isFile()) {
          try {
            const st = await fsp.stat(fullPath);
            bytesScanned += st.size;
            files.push({
              path: fullPath,
              name: entry.name,
              extension: getExtension(entry.name),
              size: st.size,
              lastAccessMs: st.atimeMs || st.mtimeMs,
              lastWriteMs: st.mtimeMs,
              duplicateGroupId: 0,
              duplicateGroupCount: 0
            });
          } catch (err) {
            errors.push(`${fullPath}: ${err.message}`);
          }
        }
      }
    }));
  }

  // Duplicate detection (3-stage pipeline)
  const reclaimableDuplicateBytes = await assignDuplicateGroups(files, minDuplicateBytes, fastLargeSampling);
  devCaches.sort((a, b) => b.size - a.size);

  return {
    files,
    devCaches,
    errors,
    bytesScanned,
    reclaimableDuplicateBytes
  };
}

async function assignDuplicateGroups(files, minDuplicateBytes, fastLargeSampling) {
  const bySize = new Map();
  for (const f of files) {
    if (f.size < minDuplicateBytes) continue;
    let list = bySize.get(f.size);
    if (!list) {
      list = [];
      bySize.set(f.size, list);
    }
    list.push(f);
  }

  const sizeCandidates = [];
  for (const grp of bySize.values()) {
    if (grp.length > 1) sizeCandidates.push(...grp);
  }
  if (sizeCandidates.length === 0) return 0;

  // Stage 2: 1 KB prefix hash
  const byPrefix = new Map();
  await mapConcurrent(sizeCandidates, HASH_CONCURRENCY, async (f) => {
    try {
      const pHash = await computePrefixHash(f.path, f.size);
      const key = `${f.size}:${pHash}`;
      let list = byPrefix.get(key);
      if (!list) {
        list = [];
        byPrefix.set(key, list);
      }
      list.push(f);
    } catch {}
  });

  const prefixCandidates = [];
  for (const grp of byPrefix.values()) {
    if (grp.length > 1) prefixCandidates.push(...grp);
  }
  if (prefixCandidates.length === 0) return 0;

  // Stage 3: Full / 8-point distributed SHA-256
  const byContent = new Map();
  await mapConcurrent(prefixCandidates, HASH_CONCURRENCY, async (f) => {
    try {
      const cHash = await computeContentHash(f.path, f.size, fastLargeSampling);
      const key = `${f.size}:${cHash}`;
      let list = byContent.get(key);
      if (!list) {
        list = [];
        byContent.set(key, list);
      }
      list.push(f);
    } catch {}
  });

  let groupId = 1;
  let reclaimable = 0;
  for (const grp of byContent.values()) {
    if (grp.length < 2) continue;
    const count = grp.length;
    reclaimable += (count - 1) * grp[0].size;
    for (const copy of grp) {
      copy.duplicateGroupId = groupId;
      copy.duplicateGroupCount = count;
    }
    groupId++;
  }

  return reclaimable;
}

function identifySafeJunk(files) {
  const safeList = [];

  // 1. Redundant duplicate copies (keep newest intact)
  const dupGroups = new Map();
  for (const f of files) {
    if (f.duplicateGroupCount > 1) {
      let arr = dupGroups.get(f.duplicateGroupId);
      if (!arr) {
        arr = [];
        dupGroups.set(f.duplicateGroupId, arr);
      }
      arr.push(f);
    }
  }
  for (const copies of dupGroups.values()) {
    copies.sort((a, b) => b.lastWriteMs - a.lastWriteMs);
    for (let i = 1; i < copies.length; i++) {
      safeList.push(copies[i]);
    }
  }

  // 2. Obsolete installers & crash dumps (> 30 days untouched)
  const cutoffMs = Date.now() - 30 * 24 * 60 * 60 * 1000;
  for (const f of files) {
    if (f.duplicateGroupCount > 1) continue;
    const ext = f.extension;
    const lowerName = f.name.toLowerCase();
    const isInstaller = ext === 'MSI' ||
      (ext === 'EXE' && (lowerName.includes('setup') || lowerName.includes('installer') || lowerName.includes('install')));
    const isCrashDump = ext === 'DMP' || (ext === 'LOG' && f.size > 20 * 1024 * 1024);

    if ((isInstaller || isCrashDump) && f.lastAccessMs <= cutoffMs) {
      safeList.push(f);
    }
  }

  return safeList;
}

function getProjectActivity(targetPath, fallbackLastWriteMs) {
  let current;
  try {
    const isDir = fs.existsSync(targetPath) && fs.statSync(targetPath).isDirectory();
    current = isDir ? path.dirname(path.resolve(targetPath)) : path.dirname(path.resolve(targetPath));
  } catch {
    current = path.dirname(targetPath);
  }

  let projectRoot = current;
  let lastTouchMs = fallbackLastWriteMs || Date.now();

  try {
    for (let depth = 0; depth < 4 && current; depth++) {
      if (isProtectedRoot(current)) break;
      const gitDir = path.join(current, '.git');
      if (fs.existsSync(gitDir)) {
        projectRoot = current;
        const headLog = path.join(gitDir, 'logs', 'HEAD');
        const gitIndex = path.join(gitDir, 'index');
        if (fs.existsSync(headLog)) {
          lastTouchMs = fs.statSync(headLog).mtimeMs;
        } else if (fs.existsSync(gitIndex)) {
          lastTouchMs = fs.statSync(gitIndex).mtimeMs;
        } else {
          lastTouchMs = fs.statSync(current).mtimeMs;
        }
        break;
      }

      const hasMarker =
        fs.existsSync(path.join(current, 'package.json')) ||
        fs.existsSync(path.join(current, 'Cargo.toml')) ||
        fs.existsSync(path.join(current, 'pyproject.toml'));

      if (hasMarker) {
        projectRoot = current;
        lastTouchMs = fs.statSync(current).mtimeMs;
        break;
      }

      const parent = path.dirname(current);
      if (parent === current) break;
      current = parent;
    }
  } catch {}

  const daysUntouched = Math.max(0, Math.floor((Date.now() - lastTouchMs) / (24 * 60 * 60 * 1000)));
  let status = 'ABANDONED';
  if (daysUntouched <= 14) status = 'ACTIVE';
  else if (daysUntouched <= 90) status = 'DORMANT';

  return { status, daysUntouched, projectRoot };
}

async function recycleItems(paths, dryRun = false) {
  let recycled = 0;
  let failed = 0;
  let protectedSkipped = 0;
  let recycledBytes = 0;
  const recycledPaths = [];
  const validTargets = [];

  for (const p of paths) {
    if (!p || isProtectedPath(p) || isProtectedRoot(p)) {
      protectedSkipped++;
      continue;
    }
    if (!fs.existsSync(p)) {
      failed++;
      continue;
    }
    try {
      const st = fs.statSync(p);
      const size = st.isDirectory() ? await fastGetDirectorySize(p) : st.size;
      validTargets.push({ path: p, isDir: st.isDirectory(), size });
    } catch {
      failed++;
    }
  }

  if (dryRun) {
    for (const t of validTargets) {
      recycled++;
      recycledBytes += t.size;
      recycledPaths.push(t.path);
    }
    return { dryRun: true, recycledCount: recycled, recycledBytes, recycledFormatted: formatBytes(recycledBytes), failedCount: failed, protectedSkipped, recycledPaths };
  }

  if (validTargets.length > 0) {
    if (process.platform === 'win32') {
      // Safe Windows Recycle Bin via Microsoft.VisualBasic.FileIO
      const psScript = `
Add-Type -AssemblyName Microsoft.VisualBasic
$items = $env:VACUMIFY_RECYCLE_JSON | ConvertFrom-Json
foreach ($item in $items) {
  try {
    if ($item.isDir) {
      [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($item.path, 'OnlyErrorDialogs', 'SendToRecycleBin')
    } else {
      [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($item.path, 'OnlyErrorDialogs', 'SendToRecycleBin')
    }
    Write-Output "OK:$($item.path)"
  } catch {
    Write-Output "ERR:$($item.path)"
  }
}
`;
      const encoded = Buffer.from(psScript, 'utf16le').toString('base64');
      const res = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], {
        env: { ...process.env, VACUMIFY_RECYCLE_JSON: JSON.stringify(validTargets) },
        encoding: 'utf8'
      });

      const okSet = new Set();
      if (res.stdout) {
        for (const line of res.stdout.split(/\r?\n/)) {
          if (line.startsWith('OK:')) okSet.add(line.slice(3).trim());
        }
      }
      for (const t of validTargets) {
        if (okSet.has(t.path) || !fs.existsSync(t.path)) {
          recycled++;
          recycledBytes += t.size;
          recycledPaths.push(t.path);
        } else {
          failed++;
        }
      }
    } else if (process.platform === 'darwin') {
      for (const t of validTargets) {
        const res = spawnSync('osascript', ['-e', `tell application "Finder" to delete POSIX file "${t.path.replace(/"/g, '\\"')}"`]);
        if (res.status === 0 && !fs.existsSync(t.path)) {
          recycled++;
          recycledBytes += t.size;
          recycledPaths.push(t.path);
        } else {
          failed++;
        }
      }
    } else {
      for (const t of validTargets) {
        const res = spawnSync('gio', ['trash', t.path]);
        if (res.status === 0 && !fs.existsSync(t.path)) {
          recycled++;
          recycledBytes += t.size;
          recycledPaths.push(t.path);
        } else {
          failed++;
        }
      }
    }
  }

  return {
    dryRun: false,
    recycledCount: recycled,
    recycledBytes,
    recycledFormatted: formatBytes(recycledBytes),
    failedCount: failed,
    protectedSkipped,
    recycledPaths: recycledPaths.slice(0, 50)
  };
}

module.exports = {
  scanDirectory,
  identifySafeJunk,
  getProjectActivity,
  recycleItems,
  formatBytes,
  formatDate
};
