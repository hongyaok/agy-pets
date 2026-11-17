/**
 * Antigravity Pets sidecar backend.
 *
 * Serves the interactive pet UI, caches petdex.dev manifest & WebP sprites,
 * persists pet configuration, queries Antigravity Language Server for live
 * model usage, and manages the Windows WPF desktop overlay process.
 */
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Response, SidecarApp } from 'sidecar_sdk';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PLUGIN_ROOT = resolve(__dirname, '..', '..');
const CACHE_DIR = join(__dirname, 'cache');
const SPRITES_CACHE_DIR = join(CACHE_DIR, 'sprites');
const MANIFEST_CACHE_PATH = join(CACHE_DIR, 'manifest-v2.json');
const CONFIG_PATH = join(CACHE_DIR, 'config.json');
const OVERLAY_EXE = join(
  PLUGIN_ROOT,
  'overlay',
  'bin',
  'Release',
  'net10.0-windows',
  'AgyPetsOverlay.exe'
);
const OVERLAY_PROJECT = join(PLUGIN_ROOT, 'overlay');

const MANIFEST_URL = 'https://petdex.dev/api/manifest/v2';
const DEFAULT_ASSET_BASE = 'https://assets.petdex.dev';
const MANIFEST_TTL_MS = 60 * 60 * 1000; // 1 hour

const DEFAULT_CONFIG = {
  slug: 'boba',
  displayName: 'Boba',
  spritesheet: 'curated/boba/sprite-v2.webp',
  spriteVersionNumber: 2,
  size: 96,
  speed: 1.0,
  behavior: 'roam',
};

mkdirSync(SPRITES_CACHE_DIR, { recursive: true });

let memoryManifest = null; // { fetchedAt: number, assetBase: string, pets: Array<object> }
let overlayProc = null;

function jsonError(status, message) {
  return new Response(JSON.stringify({ error: message }), {
    contentType: 'application/json',
    status,
  });
}

function normalizeManifest(raw) {
  const assetBase =
    (typeof raw?.assetBase === 'string' && raw.assetBase.replace(/\/+$/, '')) ||
    DEFAULT_ASSET_BASE;
  const fields = Array.isArray(raw?.fields) ? raw.fields : null;
  const idx = (name, fallback) => {
    const i = fields ? fields.indexOf(name) : -1;
    return i >= 0 ? i : fallback;
  };

  const iSlug = idx('slug', 0);
  const iDisplayName = idx('displayName', 1);
  const iKind = idx('kind', 2);
  const iSubmittedBy = idx('submittedBy', 3);
  const iSpritesheet = idx('spritesheet', 4);
  const iSpriteVersion = idx('spriteVersionNumber', 7);

  const rawPets = Array.isArray(raw?.pets) ? raw.pets : [];
  const pets = rawPets
    .map((entry) => {
      if (Array.isArray(entry)) {
        return {
          slug: String(entry[iSlug] ?? ''),
          displayName: String(entry[iDisplayName] ?? entry[iSlug] ?? ''),
          kind: String(entry[iKind] ?? ''),
          submittedBy: String(entry[iSubmittedBy] ?? ''),
          spritesheet: String(entry[iSpritesheet] ?? ''),
          spriteVersionNumber: Number(entry[iSpriteVersion] ?? 1) || 1,
        };
      }
      if (entry && typeof entry === 'object') {
        return {
          slug: String(entry.slug ?? ''),
          displayName: String(entry.displayName ?? entry.slug ?? ''),
          kind: String(entry.kind ?? ''),
          submittedBy: String(entry.submittedBy ?? ''),
          spritesheet: String(entry.spritesheet ?? ''),
          spriteVersionNumber: Number(entry.spriteVersionNumber ?? 1) || 1,
        };
      }
      return null;
    })
    .filter((p) => p && p.slug && p.spritesheet);

  return { assetBase, pets };
}

async function getManifest() {
  const now = Date.now();
  if (memoryManifest && now - memoryManifest.fetchedAt < MANIFEST_TTL_MS) {
    return memoryManifest;
  }

  // Check disk cache before fetching from network.
  if (existsSync(MANIFEST_CACHE_PATH)) {
    try {
      const st = statSync(MANIFEST_CACHE_PATH);
      if (now - st.mtimeMs < MANIFEST_TTL_MS) {
        const raw = JSON.parse(readFileSync(MANIFEST_CACHE_PATH, 'utf-8'));
        const normalized = normalizeManifest(raw);
        memoryManifest = { fetchedAt: st.mtimeMs, ...normalized };
        return memoryManifest;
      }
    } catch {
      // Corrupt cache; fall through to network fetch.
    }
  }

  try {
    const res = await fetch(MANIFEST_URL);
    if (!res.ok) {
      throw new Error(`petdex manifest fetch failed: HTTP ${res.status}`);
    }
    const raw = await res.json();
    writeFileSync(MANIFEST_CACHE_PATH, JSON.stringify(raw), 'utf-8');
    const normalized = normalizeManifest(raw);
    memoryManifest = { fetchedAt: now, ...normalized };
    return memoryManifest;
  } catch (err) {
    // Fall back to stale disk cache if network is unavailable.
    if (existsSync(MANIFEST_CACHE_PATH)) {
      const raw = JSON.parse(readFileSync(MANIFEST_CACHE_PATH, 'utf-8'));
      const normalized = normalizeManifest(raw);
      memoryManifest = { fetchedAt: now, ...normalized };
      return memoryManifest;
    }
    throw err;
  }
}

function validateSpritePath(rawPath) {
  if (typeof rawPath !== 'string') return null;
  const trimmed = rawPath.trim().replace(/\\/g, '/');
  if (!trimmed || trimmed.startsWith('/') || trimmed.includes('..') || trimmed.includes(':')) {
    return null;
  }
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._\-\/]*\.webp$/i.test(trimmed)) {
    return null;
  }
  const dest = resolve(SPRITES_CACHE_DIR, normalize(trimmed));
  const rootPrefix = resolve(SPRITES_CACHE_DIR) + sep;
  if (!dest.startsWith(rootPrefix)) {
    return null;
  }
  return { cleanPath: trimmed, cachePath: dest };
}

function readConfig() {
  if (!existsSync(CONFIG_PATH)) {
    return { ...DEFAULT_CONFIG };
  }
  try {
    const saved = JSON.parse(readFileSync(CONFIG_PATH, 'utf-8'));
    return sanitizeConfig({ ...DEFAULT_CONFIG, ...saved });
  } catch {
    return { ...DEFAULT_CONFIG };
  }
}

function sanitizeConfig(input) {
  const sizeNum = Number(input.size);
  const speedNum = Number(input.speed);
  const behavior = typeof input.behavior === 'string' && input.behavior.trim()
    ? input.behavior.trim()
    : DEFAULT_CONFIG.behavior;

  return {
    slug: typeof input.slug === 'string' && input.slug.trim() ? input.slug.trim() : DEFAULT_CONFIG.slug,
    displayName:
      typeof input.displayName === 'string' && input.displayName.trim()
        ? input.displayName.trim()
        : DEFAULT_CONFIG.displayName,
    spritesheet:
      typeof input.spritesheet === 'string' && input.spritesheet.trim()
        ? input.spritesheet.trim()
        : DEFAULT_CONFIG.spritesheet,
    spriteVersionNumber: Number(input.spriteVersionNumber) || DEFAULT_CONFIG.spriteVersionNumber,
    size: Number.isFinite(sizeNum) ? Math.min(256, Math.max(32, Math.round(sizeNum))) : DEFAULT_CONFIG.size,
    speed: Number.isFinite(speedNum) ? Math.min(4.0, Math.max(0.25, Number(speedNum.toFixed(2)))) : DEFAULT_CONFIG.speed,
    behavior,
  };
}

function isOverlayRunning() {
  return Boolean(overlayProc && overlayProc.exitCode === null && !overlayProc.killed);
}

function stopOverlay() {
  if (!isOverlayRunning()) {
    overlayProc = null;
    return false;
  }
  try {
    overlayProc.kill();
  } catch {
    // Ignore if already exited.
  }
  overlayProc = null;
  return true;
}

process.on('exit', () => stopOverlay());
process.on('SIGINT', () => {
  stopOverlay();
  process.exit(0);
});
process.on('SIGTERM', () => {
  stopOverlay();
  process.exit(0);
});

const app = new SidecarApp();

app.page('/', () => {
  const indexPath = join(__dirname, 'index.html');
  if (!existsSync(indexPath)) {
    return '<!doctype html><html><body><h2>Antigravity Pets UI not found</h2></body></html>';
  }
  return readFileSync(indexPath, 'utf-8');
});

app.api(
  '/api/pets',
  async (data) => {
    const manifest = await getManifest();
    const q = typeof data?.q === 'string' ? data.q.trim().toLowerCase() : '';
    const kind = typeof data?.kind === 'string' ? data.kind.trim().toLowerCase() : '';
    const rawOffset = Number.parseInt(data?.offset ?? '0', 10);
    const rawLimit = Number.parseInt(data?.limit ?? '48', 10);
    const offset = Number.isFinite(rawOffset) && rawOffset >= 0 ? rawOffset : 0;
    const limit = Number.isFinite(rawLimit) && rawLimit > 0 ? Math.min(rawLimit, 200) : 48;

    let list = manifest.pets;
    if (kind && kind !== 'all') {
      list = list.filter((p) => p.kind.toLowerCase() === kind);
    }
    if (q) {
      list = list.filter(
        (p) =>
          p.slug.toLowerCase().includes(q) ||
          p.displayName.toLowerCase().includes(q) ||
          p.submittedBy.toLowerCase().includes(q)
      );
    }

    return {
      total: manifest.pets.length,
      filtered: list.length,
      offset,
      limit,
      assetBase: manifest.assetBase,
      pets: list.slice(offset, offset + limit),
    };
  },
  'GET'
);

app.api(
  '/api/sprite',
  async (data) => {
    const validated = validateSpritePath(data?.path);
    if (!validated) {
      return jsonError(400, 'Invalid or missing sprite path');
    }

    const { cleanPath, cachePath } = validated;
    if (existsSync(cachePath)) {
      const buf = readFileSync(cachePath);
      return new Response(buf, {
        contentType: 'image/webp',
        headers: { 'Cache-Control': 'public, max-age=86400' },
      });
    }

    let assetBase = DEFAULT_ASSET_BASE;
    try {
      const manifest = await getManifest();
      assetBase = manifest.assetBase || DEFAULT_ASSET_BASE;
    } catch {
      // Use default asset base if manifest fetch fails.
    }

    const spriteUrl = `${assetBase}/${cleanPath}`;
    const res = await fetch(spriteUrl);
    if (!res.ok) {
      return jsonError(res.status === 404 ? 404 : 502, `Failed to fetch sprite: HTTP ${res.status}`);
    }

    const buf = Buffer.from(await res.arrayBuffer());
    mkdirSync(dirname(cachePath), { recursive: true });
    writeFileSync(cachePath, buf);

    return new Response(buf, {
      contentType: 'image/webp',
      headers: { 'Cache-Control': 'public, max-age=86400' },
    });
  },
  'GET'
);

app.api('/api/config', () => readConfig(), 'GET');

app.api(
  '/api/config',
  (data) => {
    const current = readConfig();
    const next = sanitizeConfig({ ...current, ...(data || {}) });
    mkdirSync(CACHE_DIR, { recursive: true });
    writeFileSync(CONFIG_PATH, JSON.stringify(next, null, 2), 'utf-8');
    return next;
  },
  'POST'
);

function stripThinkingLevel(label) {
  return String(label ?? '')
    .replace(
      /\s*(?:\((?:x-?high|extra\s*high|ultra\s*high|high|medium|med|low|minimal|min|max|none|thinking|non-thinking|extended\s*thinking|standard)(?:\s+thinking)?\)|[-–—]\s*(?:x-?high|extra\s*high|high|medium|med|low|minimal|thinking))\s*$/i,
      ''
    )
    .trim();
}

function stripModelIdThinkingSuffix(modelId) {
  return String(modelId ?? '')
    .replace(
      /-(?:x-?high|extra-?high|ultra-?high|high|medium|med|low|minimal|min|max|none|thinking|non-thinking)$/i,
      ''
    )
    .trim();
}

function groupUsageModels(rawConfigs) {
  const normalized = (Array.isArray(rawConfigs) ? rawConfigs : []).map((c) => {
    const rawLabel = c?.label ?? c?.modelId ?? c?.modelOrAlias?.model ?? 'Unknown';
    const rawModelId = c?.modelId ?? c?.modelOrAlias?.model ?? '';
    const frac =
      typeof c?.quotaInfo?.remainingFraction === 'number'
        ? c.quotaInfo.remainingFraction
        : typeof c?.remainingFraction === 'number'
          ? c.remainingFraction
          : null;
    const resetTime = c?.quotaInfo?.resetTime ?? c?.resetTime ?? null;
    return {
      label: rawLabel,
      modelId: rawModelId,
      remainingFraction: frac,
      resetTime,
      disabled: Boolean(c?.disabled),
      variants: typeof c?.variants === 'number' ? c.variants : 1,
    };
  });

  const hasNumericQuota = normalized.some((m) => typeof m.remainingFraction === 'number');
  const active = normalized.filter((m) => {
    if (m.disabled && m.remainingFraction === null) return false;
    if (hasNumericQuota && m.remainingFraction === null) return false;
    return true;
  });

  const groups = new Map();
  for (const item of active) {
    const baseLabel = stripThinkingLevel(item.label) || item.label || 'Unknown';
    const baseModelId = stripModelIdThinkingSuffix(item.modelId);
    const key = baseLabel.toLowerCase() || baseModelId.toLowerCase() || 'unknown';
    const existing = groups.get(key);
    if (!existing) {
      groups.set(key, {
        label: baseLabel,
        modelId: baseModelId,
        remainingFraction: item.remainingFraction,
        resetTime: item.resetTime || null,
        variants: item.variants,
      });
    } else {
      if (typeof item.remainingFraction === 'number') {
        existing.remainingFraction =
          typeof existing.remainingFraction === 'number'
            ? Math.min(existing.remainingFraction, item.remainingFraction)
            : item.remainingFraction;
      }
      if (!existing.resetTime && item.resetTime) {
        existing.resetTime = item.resetTime;
      }
      existing.variants += item.variants;
    }
  }

  return Array.from(groups.values());
}

app.api(
  '/api/usage',
  async () => {
    const lsAddr = process.env.ANTIGRAVITY_LS_ADDRESS;
    const csrf = process.env.ANTIGRAVITY_CSRF_TOKEN;
    if (!lsAddr || !csrf) {
      return jsonError(500, 'Language server environment variables not available');
    }

    const res = await fetch(
      `http://${lsAddr}/exa.language_server_pb.LanguageServerService/GetUserStatus`,
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'x-codeium-csrf-token': csrf,
        },
        body: '{}',
      }
    );

    if (!res.ok) {
      return jsonError(502, `GetUserStatus failed: HTTP ${res.status}`);
    }

    const payload = await res.json();
    const userStatus = payload?.userStatus ?? {};
    const rawConfigs = userStatus?.cascadeModelConfigData?.clientModelConfigs ?? [];
    const models = groupUsageModels(rawConfigs);

    const ps = userStatus?.planStatus ?? {};
    const planStatus = {
      planName: ps.planInfo?.planName ?? null,
    };

    return { models, planStatus };
  },
  'GET'
);

app.api(
  '/api/overlay/status',
  () => ({
    running: isOverlayRunning(),
    pid: isOverlayRunning() ? overlayProc.pid : null,
    executableExists: existsSync(OVERLAY_EXE),
  }),
  'GET'
);

app.api(
  '/api/overlay/start',
  () => {
    if (isOverlayRunning()) {
      return { running: true, pid: overlayProc.pid, alreadyRunning: true };
    }

    const port = process.env.ANTIGRAVITY_SIDECAR_WEB_PORT;
    const useExe = existsSync(OVERLAY_EXE);
    const cmd = useExe ? OVERLAY_EXE : 'dotnet';
    const args = useExe
      ? ['--port', String(port)]
      : ['run', '--project', OVERLAY_PROJECT, '--no-build', '-c', 'Release', '--', '--port', String(port)];

    const child = spawn(cmd, args, {
      detached: false,
      windowsHide: false,
      stdio: 'ignore',
    });

    child.on('exit', () => {
      if (overlayProc === child) {
        overlayProc = null;
      }
    });
    child.on('error', () => {
      if (overlayProc === child) {
        overlayProc = null;
      }
    });

    overlayProc = child;
    return { running: true, pid: child.pid, alreadyRunning: false };
  },
  'POST'
);

app.api(
  '/api/overlay/stop',
  () => {
    const stopped = stopOverlay();
    return { running: false, stopped };
  },
  'POST'
);

app.run();
