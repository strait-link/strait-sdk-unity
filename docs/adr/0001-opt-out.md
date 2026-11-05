# ADR 0001: opt-out switch `SetEnabled(false)`

Date: 5 Nov 2026 · Status: accepted · Follow-up F6 (`ops/run-1105/sdk-privacy-followups.md`) · Same design as
strait-sdk-react-native ADR 0002 (td2-27, E3, F4) and the Android, Swift, Flutter and web SDKs

## Problem
Apps in India (DPDP Act) and the EU (GDPR) often must ask a person before an attribution SDK sends anything.
Five Strait SDKs had a switch; Unity had none, so a game with a consent screen could only skip `Start`
(losing direct-link routing and the install's deferred link) or pick a rival SDK.

## Options
1. **Do nothing**: games skip `Start` until consent. Loses routing; the privacy claim is untrue for Unity.
2. **A config flag at create time**: can't change after the consent answer without a new client.
3. **The shared persisted runtime switch** `SetEnabled(bool)` + `IsEnabled()`, default on. **Chosen** (reuse:
   the contract and tests already exist in the React Native SDK; this is a port, not a new design).

## Design (identical to the other SDKs)
- Stored as `strait.enabled` (`"0"`/`"1"`) in `StraitConfig.Storage`; absent or unreadable = on. Read once and
  cached; a `SetEnabled` during the read wins.
- Off: the one HTTP helper `Call` refuses every engine call (belt) and each entry point returns early (braces):
  no `/v1/open`, `/v1/event`, `/v1/match`, `/v1/referrer`, `/v1/handoff/claim`, `/v1/resolve`, debug
  fingerprint. Nothing is queued (`Enqueue` and `Flush` check the switch) and no tap id is remembered
  (`NoteTap` checks it inside the write chain, so a reply that lands after the switch keeps nothing).
  Turning it off deletes the saved queue and the remembered tap.
- Short links while off: `POST /v1/lookup` (engine ADR 0083, records nothing) with the publishable key and the
  link's host + path only. The one call allowed while off, by name (`lookupOnly`). Failure (offline, 429, an
  engine without it: 404) = `Matched = false, Reason = "disabled"`; a 200 miss keeps the engine's reason.
- Switched off while a `/v1/resolve` is out (F4): a failed resolve re-reads the switch and, when off, routes
  through `/v1/lookup`; nothing is queued.
- First launch while off: the once-per-install check is not marked done; `SetEnabled(true)` runs it (the
  consent-screen-on-first-launch flow keeps its deferred link), else the next `Start`. Switched off during
  the check: the call fails at the gate as "network", so it is not marked done either, and the iPhone
  clipboard (paste handoff) is not read (no paste prompt for an opted-out person): the switch is checked before
  the prompt-free detect and again before the read (F7). The "run it when back on"
  flag is taken with `Interlocked.Exchange`, so two `SetEnabled(true)` calls can't run the check twice.
- `CheckDeferred` and `ClaimHandoff` answer `Reason = "disabled"` with no call. `Doctor` (a GET of the public
  assetlinks / AASA file, no user data) still works.
- No legacy stateless API exists in this SDK (the F5 lesson): every engine call is in `StraitClient`.

## Security lens
Spoofing / tampering: none new (a local flag in the game's own storage). Data exposure: the point of the
change; accepted risk = a storage read that throws reads as "on" (an unreadable store can't hold an opt-out).
Abuse / DoS: fewer requests. `/v1/lookup` sends no query string, openId, platform or device fields.

## Architecture lens
No engine, wire or vector change. Open PR #9 (B6a install time + B13a event queue) must keep these gates when
it is rebased: its queued events go through `Enqueue`/`Flush`/`Call`, which are gated.
