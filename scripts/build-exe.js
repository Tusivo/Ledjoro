#!/usr/bin/env node
'use strict';
/**
 * Builds the engine executable (default dist/Ledjoro-Engine.exe; --out <path> to change) — a single-file Node app (Node Single Executable Application).
 *   1. bundle src/*.js into one CommonJS file with a tiny built-in bundler (no bundler dependency)
 *   2. write the SEA config (no web assets: there is no web UI)
 *   3. inject the blob into a copy of node.exe with `postject` (build-time tool only; NOT shipped, NOT a runtime dependency)
 *   4. flip the PE subsystem to "Windows GUI" so no console window appears
 * Requires: Node >= 24 on Windows (same machine/arch as the target).
 */
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const dist = path.join(root, 'dist');
const build = path.join(dist, '.build');
fs.rmSync(build, { recursive: true, force: true });
fs.mkdirSync(build, { recursive: true });

// ---- 1. bundle ----
const mods = new Map();
function resolveFile(from, spec) {
  const base = path.resolve(path.dirname(from), spec);
  for (const c of [base, base + '.js', path.join(base, 'index.js')]) if (fs.existsSync(c) && fs.statSync(c).isFile()) return c;
  throw new Error(`Cannot resolve ${spec} from ${from}`);
}
function collect(file) {
  const id = path.relative(root, file).split(path.sep).join('/');
  if (mods.has(id)) return id;
  const src = fs.readFileSync(file, 'utf8');
  mods.set(id, { src, deps: {} });
  for (const m of src.matchAll(/require\(\s*['"](\.{1,2}\/[^'"]+)['"]\s*\)/g)) mods.get(id).deps[m[1]] = collect(resolveFile(file, m[1]));
  return id;
}
const entry = collect(path.join(root, 'src', 'engine.js'));
let out = '"use strict";\nconst __defs = {\n';
for (const [id, m] of mods) {
  out += `${JSON.stringify(id)}: [${JSON.stringify(m.deps)}, function (module, exports, require, __dirname, __filename) {\n${m.src}\n}],\n`;
}
out += `};\nconst __cache = {};\nfunction __load(id) {\n  if (__cache[id]) return __cache[id].exports;\n  const [deps, fn] = __defs[id];\n  const module = (__cache[id] = { exports: {} });\n  fn(module, module.exports, (spec) => (spec.startsWith('.') ? __load(deps[spec]) : require(spec)), 'src', id);\n  return module.exports;\n}\n__load(${JSON.stringify(entry)});\n`;
const bundle = path.join(build, 'bundle.js');
fs.writeFileSync(bundle, out);

// ---- 2. SEA config ----
const blob = path.join(build, 'sea.blob');
const cfgPath = path.join(build, 'sea-config.json');
fs.writeFileSync(cfgPath, JSON.stringify({ main: bundle, output: blob, disableExperimentalSEAWarning: true, useSnapshot: false, useCodeCache: false }, null, 2));
let r = spawnSync(process.execPath, ['--experimental-sea-config', cfgPath], { stdio: 'inherit' });
if (r.status !== 0) process.exit(r.status || 1);

// ---- 3. inject into a copy of node.exe ----
const outArg = process.argv.indexOf('--out');
const exe = path.resolve(outArg >= 0 ? process.argv[outArg + 1] : path.join(dist, 'Ledjoro-Engine.exe'));
fs.mkdirSync(path.dirname(exe), { recursive: true });
fs.copyFileSync(process.execPath, exe);
const npx = process.platform === 'win32' ? 'npx.cmd' : 'npx';
r = spawnSync(npx, ['--yes', 'postject@1.0.0-alpha.6', exe, 'NODE_SEA_BLOB', blob, '--sentinel-fuse', 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2'], { stdio: 'inherit', shell: process.platform === 'win32' });
if (r.status !== 0) { console.error('postject failed'); process.exit(r.status || 1); }

// ---- 4. GUI subsystem (no console window) ----
if (process.platform === 'win32') {
  const fd = fs.openSync(exe, 'r+');
  const hdr = Buffer.alloc(4); fs.readSync(fd, hdr, 0, 4, 0x3c);
  const pe = hdr.readUInt32LE(0);
  const sub = Buffer.alloc(2); const off = pe + 4 + 20 + 68;
  fs.readSync(fd, sub, 0, 2, off);
  if (sub.readUInt16LE(0) === 3) { sub.writeUInt16LE(2, 0); fs.writeSync(fd, sub, 0, 2, off); }
  fs.closeSync(fd);
}

const sha = crypto.createHash('sha256').update(fs.readFileSync(exe)).digest('hex');
fs.rmSync(build, { recursive: true, force: true });
console.log(`Built ${exe}\nSize: ${(fs.statSync(exe).size / 1048576).toFixed(1)} MB\nSHA-256: ${sha}`);
