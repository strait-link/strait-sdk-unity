#!/usr/bin/env node
// Unity ignores any file in a git/OpenUPM package (an immutable folder) that has no
// .meta file. This keeps one next to every package file under src/, with a GUID
// derived from the path so it never changes between runs or machines.
//   node scripts/unity-meta.mjs            # exit 1 and list files missing a .meta
//   node scripts/unity-meta.mjs --write    # create the missing ones
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const ROOT = 'src';
// Not part of the Unity package: dotnet build output, and "~" folders Unity skips.
const SKIP = (name) => name === 'bin' || name === 'obj' || name.endsWith('~') || name.startsWith('.');

function files(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    if (SKIP(e.name) || e.name.endsWith('.meta')) return [];
    const rel = path.join(dir, e.name);
    return e.isDirectory() ? [rel, ...files(rel)] : [rel];
  });
}

const importer = (f) => {
  if (fs.statSync(f).isDirectory()) return 'folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  if (f.endsWith('.cs')) return 'MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  // Native iOS plugin source: compiled by Xcode in the game's iOS build only.
  if (f.endsWith('.mm')) return 'PluginImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n  - first:\n      Any: \n    second:\n      enabled: 0\n      settings: {}\n  - first:\n      Editor: Editor\n    second:\n      enabled: 0\n      settings:\n        DefaultValueInitialized: true\n  - first:\n      iPhone: iOS\n    second:\n      enabled: 1\n      settings: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  if (f.endsWith('.asmdef')) return 'AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  if (f.endsWith('.json') || f.endsWith('.md')) return 'TextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
  return 'DefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n';
};

const write = process.argv.includes('--write');
const missing = files(ROOT).filter((f) => !fs.existsSync(`${f}.meta`));
for (const f of missing) {
  const guid = crypto.createHash('md5').update(`unity-package:${path.relative(ROOT, f)}`).digest('hex');
  if (write) fs.writeFileSync(`${f}.meta`, `fileFormatVersion: 2\nguid: ${guid}\n${importer(f)}`);
}
if (!missing.length) console.log('unity-meta: every package file has a .meta');
else if (write) console.log(`unity-meta: wrote ${missing.map((f) => `${f}.meta`).join(', ')}`);
else {
  console.error(`unity-meta: missing .meta (run: node scripts/unity-meta.mjs --write): ${missing.join(', ')}`);
  process.exitCode = 1;
}
