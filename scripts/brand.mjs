#!/usr/bin/env node
// brand.mjs — applies brand.json (this package's public identity) to the files a
// registry reads: the manifest (package.json / pubspec.yaml / Unity package.json),
// the README install block, the LICENSE holder line, and (Dart) the library name.
// brand.json is the ONLY place a brand name, npm scope, domain or repo URL is typed.
//
// Byte-identical copy in every SDK repo (scripts/brand.mjs). Canonical copy + tests:
// shared-spec/scripts/brand.mjs, brand.test.mjs. Zero dependencies, Node >= 18.
//
//   node scripts/brand.mjs                    # dry run: list files that would change (exit 1 if any)
//   node scripts/brand.mjs --write            # apply brand.json
//   node scripts/brand.mjs --release          # CI gate before publishing: in sync AND brand marked final
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const KINDS = ['npm', 'dart', 'gradle', 'swift', 'unity'];
const START = '<!-- brand:install -->';
const END = '<!-- /brand:install -->';

/** Validates brand.json and derives every public name from it. Throws on bad input. */
export function identity(b) {
  const need = (k, re, hint) => {
    if (typeof b[k] !== 'string' || !re.test(b[k])) throw new Error(`brand.json: "${k}" must be ${hint}`);
    return b[k];
  };
  need('brand', /^\S.{0,39}$/, 'a display name (1-40 chars)');
  const slug = need('slug', /^[a-z][a-z0-9]{0,30}$/, 'lowercase letters/digits, e.g. "godwit"');
  const scope = need('npmScope', /^@[a-z0-9][a-z0-9._-]{0,50}$/, 'an npm scope, e.g. "@godwit"');
  const owner = need('githubOwner', /^[A-Za-z0-9][A-Za-z0-9-]{0,38}$/, 'a GitHub user/org');
  const prefix = need('repoPrefix', /^[a-z0-9][a-z0-9-]{0,30}$/, 'a repo name prefix, e.g. "godwit"');
  need('website', /^https:\/\/[a-z0-9.-]+\.[a-z]{2,}\/?$/, 'an https origin, e.g. "https://getgodwit.com"');
  need('linkDomain', /^[a-z0-9.-]+\.[a-z]{2,}$/, 'a bare domain, e.g. "godwit.link"');
  need('legalName', /^\S.{0,99}$/, 'the copyright holder');
  const kind = need('kind', new RegExp(`^(${KINDS.join('|')})$`), KINDS.join(' | '));
  const pkg = need('package', /^[a-z0-9][a-z0-9-]{0,40}$/, 'the package suffix, e.g. "sdk-web"');
  const repo = `${prefix}-${pkg}`;
  const repoUrl = `https://github.com/${owner}/${repo}`;
  const name = {
    npm: `${scope}/${pkg}`,
    dart: `${slug}_sdk`,
    gradle: `com.github.${owner}:${repo}`,
    swift: repo,
    unity: `com.${slug}.sdk`,
  }[kind];
  return { ...b, kind, repo, repoUrl, issues: `${repoUrl}/issues`, name, website: b.website.replace(/\/$/, '') };
}

/** True when brand.json is marked final and holds no placeholder values. */
export function isFinal(b) {
  return b.final === true && !/yourbrand|\[|\]/i.test(`${b.website} ${b.linkDomain} ${b.legalName}`);
}

const read = (root, f) => (fs.existsSync(path.join(root, f)) ? fs.readFileSync(path.join(root, f), 'utf8') : null);
const walk = (root, dir, ext) => {
  const abs = path.join(root, dir);
  if (!fs.existsSync(abs)) return [];
  return fs.readdirSync(abs, { withFileTypes: true }).flatMap((e) => {
    const rel = path.join(dir, e.name);
    if (e.isDirectory()) return e.name.startsWith('.') ? [] : walk(root, rel, ext);
    return rel.endsWith(ext) ? [rel] : [];
  });
};
const json = (o) => JSON.stringify(o, null, 2) + '\n';
const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Returns `key` set on `obj`, keeping the key's position when it already exists. */
function setKeys(obj, entries) {
  const out = { ...obj };
  for (const [k, v] of Object.entries(entries)) out[k] = v;
  return out;
}

function setYaml(text, key, value, file) {
  const re = new RegExp(`^${key}:.*$`, 'm');
  if (!re.test(text)) throw new Error(`${file}: top-level "${key}:" line missing`);
  return text.replace(re, `${key}: ${value}`);
}

function versionOf(root, id) {
  const v = {
    npm: () => JSON.parse(read(root, 'package.json')).version,
    dart: () => /^version:\s*(\S+)/m.exec(read(root, 'pubspec.yaml'))?.[1],
    gradle: () => /^version\s*=\s*"([^"]+)"/m.exec(read(root, 'build.gradle.kts'))?.[1],
    swift: () => /^##\s*\[?(\d+\.\d+\.\d+[^\]\s]*)/m.exec(read(root, 'CHANGELOG.md') ?? '')?.[1],
    unity: () => JSON.parse(read(root, 'src/package.json')).version,
  }[id.kind]();
  if (!v) throw new Error(`cannot find the ${id.kind} package version`);
  return v;
}

/** The README install block for this kind (between the brand:install markers). */
export function installBlock(id, version, extra = {}) {
  switch (id.kind) {
    case 'npm':
      return ['```sh', `npm install ${id.name}`, '```'].join('\n');
    case 'dart':
      return ['```sh', `dart pub add ${id.name}      # Flutter apps: flutter pub add ${id.name}`, '```'].join('\n');
    case 'gradle':
      return [
        'Published on [JitPack](https://jitpack.io) from this repo\'s version tags.',
        '',
        '```kotlin',
        '// settings.gradle.kts',
        'dependencyResolutionManagement {',
        '    repositories { google(); mavenCentral(); maven("https://jitpack.io") }',
        '}',
        '',
        '// app/build.gradle.kts',
        `dependencies { implementation("${id.name}:v${version}") }`,
        '```',
      ].join('\n');
    case 'swift':
      return [
        'Xcode: **File → Add Package Dependencies…** and paste the repo URL, or in `Package.swift`:',
        '',
        '```swift',
        `.package(url: "${id.repoUrl}", from: "${version}")`,
        `// target dependency: .product(name: "${extra.product}", package: "${id.repo}")`,
        '```',
      ].join('\n');
    case 'unity':
      return [
        'Unity **Window → Package Manager → + → Add package from git URL…**:',
        '',
        '```text',
        `${id.repoUrl}.git?path=src#v${version}`,
        '```',
        '',
        `Or with [OpenUPM](https://openupm.com): \`openupm add ${id.name}\`.`,
      ].join('\n');
  }
}

export function licenseText(holder, year) {
  return `MIT License

Copyright (c) ${year} ${holder}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
`;
}

/**
 * Works out every change brand.json implies, without touching disk.
 * Returns { id, files: Map<relPath, newText>, renames: [[from, to]] } (only real changes).
 */
export function plan(root) {
  const id = identity(JSON.parse(read(root, 'brand.json') ?? 'null') ?? {});
  const files = new Map();
  const renames = [];
  const cur = (f) => (files.has(f) ? files.get(f) : read(root, f));
  const put = (f, text) => {
    if (text !== read(root, f)) files.set(f, text);
    else files.delete(f);
  };
  const replaceIn = (f, from, to) => {
    const t = cur(f);
    if (t != null && from !== to) put(f, t.replace(from, to));
  };

  // 1. Manifest (+ the old public name, so docs/imports follow a rename).
  let old = id.name;
  if (id.kind === 'npm') {
    const p = JSON.parse(read(root, 'package.json'));
    old = p.name;
    put('package.json', json(setKeys(p, {
      name: id.name,
      author: id.legalName,
      homepage: id.website,
      repository: { type: 'git', url: `git+${id.repoUrl}.git` },
      bugs: { url: id.issues },
    })));
    const lock = read(root, 'package-lock.json');
    if (lock) {
      const l = JSON.parse(lock);
      l.name = id.name;
      if (l.packages?.['']) l.packages[''].name = id.name;
      put('package-lock.json', json(l));
    }
  } else if (id.kind === 'dart') {
    let y = read(root, 'pubspec.yaml');
    old = /^name:\s*(\S+)/m.exec(y)[1];
    for (const [k, v] of [['name', id.name], ['homepage', id.website], ['repository', id.repoUrl], ['issue_tracker', id.issues]]) {
      y = setYaml(y, k, v, 'pubspec.yaml');
    }
    put('pubspec.yaml', y);
    if (old !== id.name) {
      const from = `lib/${old}.dart`;
      if (fs.existsSync(path.join(root, from))) renames.push([from, `lib/${id.name}.dart`]);
      for (const f of [...walk(root, 'lib', '.dart'), ...walk(root, 'test', '.dart'), ...walk(root, 'example', '.dart')]) {
        replaceIn(f, new RegExp(`package:${esc(old)}/${esc(old)}\\.dart`, 'g'), `package:${id.name}/${id.name}.dart`);
        replaceIn(f, new RegExp(`package:${esc(old)}/`, 'g'), `package:${id.name}/`);
      }
      replaceIn('README.md', new RegExp(`\\b${esc(old)}\\b`, 'g'), id.name);
    }
  } else if (id.kind === 'unity') {
    const p = JSON.parse(read(root, 'src/package.json'));
    old = p.name;
    put('src/package.json', json(setKeys(p, {
      name: id.name,
      displayName: `${id.brand} SDK`,
      documentationUrl: `${id.repoUrl}#readme`,
      changelogUrl: `${id.repoUrl}/blob/main/CHANGELOG.md`,
      licensesUrl: `${id.repoUrl}/blob/main/LICENSE`,
      author: { name: id.legalName, url: id.website },
    })));
  }
  if ((id.kind === 'npm' || id.kind === 'unity') && old !== id.name) {
    replaceIn('README.md', new RegExp(esc(old), 'g'), id.name);
  }

  // 2. README install block.
  const readme = cur('README.md');
  if (readme == null || !readme.includes(START) || !readme.includes(END)) {
    throw new Error(`README.md: needs an install block between ${START} and ${END}`);
  }
  const product = id.kind === 'swift' ? /\.library\(\s*name:\s*"([^"]+)"/.exec(read(root, 'Package.swift') ?? '')?.[1] : undefined;
  if (id.kind === 'swift' && !product) throw new Error('Package.swift: no .library product found');
  const block = installBlock(id, versionOf(root, id), { product });
  const i = readme.indexOf(START) + START.length;
  put('README.md', `${readme.slice(0, i)}\n${block}\n${readme.slice(readme.indexOf(END))}`);

  // 3. LICENSE (keeps the year it was first written).
  const year = /Copyright \(c\) (\d{4}(?:-\S+)?)/.exec(read(root, 'LICENSE') ?? '')?.[1] ?? String(new Date().getFullYear());
  put('LICENSE', licenseText(id.legalName, year));
  // A Unity package's root is src/, so it carries its own copy.
  if (id.kind === 'unity') put('src/LICENSE.md', licenseText(id.legalName, year));

  // A renamed file's edits belong to its new path.
  for (const [from, to] of renames) {
    if (files.has(from)) files.set(to, files.get(from));
    else files.set(to, read(root, from));
    files.delete(from);
  }
  return { id, files, renames };
}

export function apply(root, { files, renames }) {
  for (const [from, to] of renames) fs.renameSync(path.join(root, from), path.join(root, to));
  for (const [f, text] of files) fs.writeFileSync(path.join(root, f), text);
}

function main(argv) {
  const root = process.cwd();
  const write = argv.includes('--write');
  const release = argv.includes('--release');
  let p;
  try {
    p = plan(root);
  } catch (e) {
    console.error(`brand: ${e.message}`);
    return 2;
  }
  const changed = [...p.renames.map(([a, b]) => `${a} -> ${b}`), ...p.files.keys()];
  if (write) {
    apply(root, p);
    console.log(changed.length ? `brand: updated ${changed.join(', ')}` : 'brand: already in sync');
    return 0;
  }
  if (changed.length) {
    console.error(`brand: out of sync with brand.json (run: node scripts/brand.mjs --write): ${changed.join(', ')}`);
    return 1;
  }
  if (release && !isFinal(p.id)) {
    console.error('brand: brand.json is not final (set "final": true and replace every placeholder) — refusing to publish.');
    return 1;
  }
  console.log(`brand: in sync — ${p.id.name} (${p.id.repoUrl})${isFinal(p.id) ? '' : ' [placeholder brand]'}`);
  return 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  process.exitCode = main(process.argv.slice(2));
}
