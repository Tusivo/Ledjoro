#!/usr/bin/env node
'use strict';
/**
 * Builds dist/Ledjoro.exe: the native Windows app with the accounting engine embedded (one file, no browser, no web view).
 *   1. build the engine (Node single-executable app) with scripts/build-exe.js
 *   2. build the native app once without the engine, ask it to write its own icon (--make-icon)
 *   3. build the native app again with the engine + icon embedded
 * Requires: Windows, Node >= 24, and the .NET SDK (the app itself targets .NET Framework 4.8, which ships with Windows 10/11).
 */
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const dist = path.join(root, 'dist');
const work = path.join(dist, '.native');
const proj = path.join(root, 'native', 'Ledjoro', 'Ledjoro.csproj');
const run = (cmd, args, opts = {}) => { const r = spawnSync(cmd, args, { stdio: 'inherit', ...opts }); if (r.status !== 0) { console.error(`failed: ${cmd} ${args.join(' ')}`); process.exit(r.status || 1); } };

fs.rmSync(work, { recursive: true, force: true });
fs.mkdirSync(work, { recursive: true });

const engine = path.join(work, 'Ledjoro-Engine.exe');
run(process.execPath, [path.join(__dirname, 'build-exe.js'), '--out', engine]);
const hash = crypto.createHash('sha256').update(fs.readFileSync(engine)).digest('hex');
const hashFile = path.join(work, 'engine.sha256');
fs.writeFileSync(hashFile, hash);

const stage = path.join(work, 'stage');
run('dotnet', ['build', proj, '-c', 'Release', '--no-incremental', '-nologo', '-v', 'q', '-o', stage]);
const icon = path.join(work, 'Ledjoro.ico');
run(path.join(stage, 'Ledjoro.exe'), ['--make-icon', icon]);
if (!fs.existsSync(icon)) { console.error('icon was not written'); process.exit(1); }

const out = path.join(work, 'final');
run('dotnet', ['build', proj, '-c', 'Release', '--no-incremental', '-nologo', '-v', 'q', '-o', out, `-p:EnginePath=${engine}`, `-p:EngineHashFile=${hashFile}`, `-p:IconPath=${icon}`]);

const exe = path.join(dist, 'Ledjoro.exe');
fs.copyFileSync(path.join(out, 'Ledjoro.exe'), exe);
const sha = crypto.createHash('sha256').update(fs.readFileSync(exe)).digest('hex');
fs.writeFileSync(path.join(dist, 'Ledjoro.exe.sha256'), `${sha}  Ledjoro.exe\n`);
fs.rmSync(work, { recursive: true, force: true });
console.log(`Built ${exe}\nSize: ${(fs.statSync(exe).size / 1048576).toFixed(1)} MB\nSHA-256: ${sha}`);
