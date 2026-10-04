# COUNCIL-F (Stream F, item F4): AGENTS.md

Branch `council/F`, based on `origin/main`. Docs only: no code, version or behaviour change.

## What changed
- New `AGENTS.md`: instructions for AI coding agents (Claude Code, Cursor, Codex, Copilot) that add this SDK
  to an app or work on this repo. It covers install, the key rule (publishable key in apps, secret key only on
  servers), the one `onLink` pattern (or the one call for web / server), how to verify, and the repo's test
  command and invariants (shared vectors, `brand.json`, wire names).
- Wording follows the website docs on `council/scaffold` (`site/content/docs/sdk-*.md`).

## How to verify
- Read `AGENTS.md` next to the SDK's docs page; the snippets match it.
- Tests (unchanged code): not run: `dotnet` is not installed on this machine (CI runs `dotnet test test/Strait.Signature.Tests.csproj`).

## Notes
- Install lines use the version tags (v0.8.0 / v0.7.2 / v0.6.2 / v0.5.2). Those tags are not pushed yet, so the
  install commands fail until they are.
- The MCP pointer links https://straitlink.in/ai/, a page added on the site's `council/F` branch (not live until
  the site branch is pushed).
