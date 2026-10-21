# Changelog

## Unreleased

- New `StraitConfig.DeviceClass` (contract B14, engine ADR 0039): `"phone"` or `"tablet"` is sent with
  `/v1/resolve` and `/v1/open` (queued retries too). New `StraitCore.DeviceClassForSize` (>= 600 dp =
  tablet) and `StraitCore.DeviceClassForModel` (`SystemInfo.deviceModel` "iPad…" = tablet); the Quick
  start sample's `StraitDevice.Class` wires both. Left out when not supplied; older engines ignore it.
  Conformance vectors v9.

## 0.8.5

- Fix: a link that opens the game while the first-launch deferred check is still running is no
  longer followed by that check's *match* either (0.8.4 held back only its "no link" event). The
  game stays on the screen the player just tapped instead of jumping to a stale pre-install
  destination. The engine still counts the install and records and attributes the match as before,
  the check is still marked done, and the match is not replayed to late `OnLink` subscribers.
  `CheckDeferred()` still returns (and raises) its answer (contract B6).
- iPhone paste handoff: once a link has opened the game in this launch, the first-launch check no
  longer reads the clipboard, so iOS shows no "Allow Paste" prompt for an answer that won't be
  delivered. Checked again right after the prompt-free URL detect; a clipboard already read is
  claimed as before (contract B6/B19).
- Known gap (no API change): if the check had already raised `OnLinkStart` when the link arrived,
  no `OnLink` event with that start's `Id` follows. The documented pattern (loading on at any
  `OnLinkStart`, off at any `OnLink`) stays correct because the link's own start and event come
  after it; a game that tracks loading per `Id` should also clear pending deferred ids when a
  `direct` event arrives.

## 0.8.4

- Fix: a link that opens the game while the first-launch deferred check is still running is no
  longer followed by that check's "no link" event (`Matched = false`, `no_match` or `network`).
  Found with the React Native SDK on an Android emulator (13 Oct): the link was delivered and the
  check's `false` came right after it, so a game that routes home on `!Matched` left the right
  screen. Unity had the same race (the quick-start sample wires `deepLinkActivated` before
  `Start`). The check still runs and counts the install, and is marked done (or retried next
  launch) as before; only its no-link event is not delivered, and it raises no
  `OnLinkStart` once a link is here (a loading state would never end). A deferred match is still
  delivered; `CheckDeferred()` is unchanged (contract B6).

## 0.8.3

- Quick start sample (SDK-24): `StraitBootstrap` now passes `DeviceFields = StraitDevice.Collect`, and the
  sample ships the README's `StraitDevice.cs` (Android via `AndroidJavaClass`, iOS via `StraitDevice.mm`).
  Before, it sent no device fields, so `/v1/match` answered 400 and a game built from the sample never
  got its deferred link. Editor / desktop still send none. No SDK code change.

## 0.8.2

- `checkDeferred()` (the debug / settings re-check) now sends `"record": false` on `/v1/match` and `/v1/referrer`, so the
  engine answers the match without adding an install, an open or an `install.matched` webhook
  (contract B6: it never adds an install). The once-per-install check is unchanged. Takes effect
  once the engine with `record` support is live; an older engine ignores the field.

## 0.8.1

- README: the common Strait SDK header (logo, the promise "Straight to the screen. On the record.",
  badges, links to docs, platform status and the changelog), a platform features table in the
  availability words (Live / Beta / Planned / Not yet) with the iPhone beta truth (the method is
  the customer's choice, device matching off by default), and a "Docs and support" section
  (Talk to the Strait team). The same structure in all seven SDK READMEs (design system v5).
- iPhone deferred method is the customer's choice, applied at runtime (founder decision
  10 Oct 2026). Dashboard Settings → iPhone installs has two switches, device matching and
  paste handoff; off/off, device only, paste only and both all work. The SDK now reads the
  choice live from the engine's `/v1/match` reply (`ios: {deviceMatching, pasteHandoff}`)
  on the once-per-install check and never stores it, so a change needs no game update. The
  clipboard is read only when that reply found no match and says `pasteHandoff: true`; a
  failed `/v1/match` no longer reads the clipboard (reason `network`, retried next launch);
  an older engine without the field means off. Device matching is off by default for new
  workspaces (engine migration 0061). `StraitConfig.ClipboardBoost` is `[Obsolete]` and
  ignored.
- Clipboard boost order (B19): the first-launch iPhone check now runs device matching
  (`/v1/match`) first and reads the clipboard / claims the handoff only when it returns no
  match or fails. A device match no longer shows iOS's "Allow Paste" prompt. Same `openId`
  across both attempts; one event. `claimHandoff` is unchanged.
- Old Firebase Dynamic Links (shared-spec/SDK-CONTRACT.md B22, conformance vectors v8). Add your
  `<x>.page.link` host to `LinkHosts` (and keep `applinks:<x>.page.link` / the page.link intent filter in
  your build): a page.link **short** link is looked up through `/v1/resolve` like any short link (the
  engine matches the old host + code of links you imported), and a page.link **long** link
  (`https://<x>.page.link/?link=<url>&…`) opens its `link=` destination straight away, on the device, with no
  lookup. Only `*.page.link` hosts at the root path, only an http(s) `link`. New core function
  `StraitCore.PageLinkLongLink`. Not yet tested on a real iPhone.

## 0.8.0

- iPhone clipboard boost, opt-in (shared-spec/SDK-CONTRACT.md B19). New `StraitConfig.ClipboardBoost`
  (default **false**: the SDK never touches the clipboard) and `StraitConfig.Clipboard`
  (`IStraitClipboard`). With it on, the once-per-install deferred check on iOS asks, without a prompt,
  whether the clipboard probably holds a web URL (`UIPasteboard detectPatterns`), reads it only then
  (iOS shows its "Allow Paste" prompt), and claims a Strait handoff link with
  `POST /v1/handoff/claim` for an exact match (`LinkEvent.Route` `"clipboard"`); otherwise the
  normal `/v1/match` runs with the same open id. `CheckDeferred()` never reads the clipboard.
- New `StraitClient.ClaimHandoff(text)` for a paste button (no prompt), new core function
  `StraitCore.ParseHandoffUrl` (conformance vectors v7), new route `LinkRoutes.Clipboard`.
- New `IosStraitClipboard` + native `Plugins/iOS/StraitClipboard.mm` (iOS player builds only; not
  compiled in CI, not yet tried on a device).

## 0.7.2

- Privacy hardening (shared-spec/SDK-CONTRACT.md B18): the URL sent with an open report
  (`/v1/open`, `/v1/resolve`) and saved in the offline queue (`strait.pendingOpens`) keeps only
  scheme, host, path and the first `utm_source` pair (the engine reads that for channel
  attribution and nothing else). Query and fragment never leave the device or reach storage;
  reports queued by an older version are stripped when the queue is next read. Your `OnLink`
  events still carry the full URL. New core functions `StraitCore.ReportUrl` and
  `StraitCore.StaleTap` (conformance vectors v6).
- An expired remembered tap id (`strait.lastTap`, older than 7 days) is now deleted at `Start`
  and when `TrackEvent` reads it, instead of only being ignored.
- `new StraitClient(...)` throws `ArgumentException` ("StraitClient: publishableKey is required" /
  "StraitClient: endpoint is required") when the publishable key or endpoint is empty or blank,
  instead of failing later on every request.

## 0.7.1

- Report the portrait screen width so a first launch in landscape still matches the tap
  (shared-spec/SDK-CONTRACT.md B17): new core function `StraitCore.PortraitScreenWidth(w, h)`
  (the shorter side, rounded like the browser; conformance vectors v5). The README's device
  snippet uses it.

## 0.7.0

- Every attributed open now supplies the tap id (shared-spec/SDK-CONTRACT.md B16): when
  `/v1/resolve` (verified short link), `/v1/match` (fingerprint) or `/v1/referrer` (Play
  install) returns `clickId`, it is remembered as `strait.lastTap` and sent with conversion
  events. A reply without one (older engine) keeps the 0.6.0 behaviour.
- New core function: `StraitCore.ReplyClickId` (conformance vectors v4).

## 0.6.0

- Conversion events carry the tap id (shared-spec/SDK-CONTRACT.md B15): the tap id of the last
  attributed link open (browser hand-off `strait_click`, or the Play referrer on a deferred
  install) is remembered under `strait.lastTap` and sent as `clickId` with `TrackEvent` for 7
  days. A newer short-link or fingerprint open forgets it. `TrackEvent(name, clickId: …)`
  overrides it.
- New core API: `StraitCore.EventClickId`, `StraitCore.RememberTap`,
  `StraitCore.AttributionWindowMs`, `StraitClient.TapKey` (conformance vectors v3).

## 0.5.0

Renamed to Strait (breaking, clean break: no `Strait` aliases are kept).

- Package `com.strait.sdk`; namespace `Strait`; assembly
  `Strait.Sdk` (`Strait.Sdk.asmdef`; `.meta` GUIDs unchanged, so GUID references
  keep resolving; asmdef references by name must change to `Strait.Sdk`); `Strait.Signature.csproj`.
- Types: `StraitClient`, `StraitConfig`, `StraitCore`, `StraitJson`, `StraitSignature`,
  `IStraitTransport`; `ParseStraitLink` / `ParseStraitClick` →
  `ParseStraitLink` / `ParseStraitClick`. Sample `StraitBootstrap`.
- Wire params are now `strait_click` and `strait_link`; the old `strait_*` names are no longer read.
- Storage keys are now `strait.*` (`strait.deferredChecked`, `strait.pendingOpens`); values saved
  under the old keys are ignored, so a deferred link may be checked once more after upgrading.
- Publishable keys are issued as `st_pub_live_…` / `st_pub_test_…`.

## 0.4.0

Reports every link open exactly once (`shared-spec/SDK-CONTRACT.md` B14, plus the B4/B6/B7/B8 revisions).

- `StraitCore`: `ParseStraitClick`, `TakeClickId`, `PruneOpenQueue`, `ShouldRetryReport`, `NewOpenId`,
  `OpenQueueMax` / `OpenQueueMaxAgeMs`. `ClassifyUrl` strips the `strait_click` tap id and returns it as `ClickId`.
- `StraitClient`: each open's id (`o_<base36 ms>_<12 chars>`) is the `LinkEvent.Id`. Short links send
  `openId, appState, firstLaunch, at` with `/v1/resolve` (falls back to `/v1/open` unless `recorded:true`);
  other URLs are reported via `/v1/open` without delaying `OnLink`. Unsent reports are saved under
  `strait.pendingOpens` and retried on `Start`, on `OnAppState("active")` and after any successful report.
  New `PendingOpenReports()` and `FlushOpenReports()`.
- Deferred: `/v1/referrer` sends the tap id; `/v1/referrer` and `/v1/match` carry `openId` + `at`. The
  once-per-install flag is set only once the engine answered (no answer / 429 / 5xx → retried next
  launch). `CheckDeferred()` sends no `openId`.
- `conformance-vectors.json` v2; tests port `sdk-react-native/test/opens.test.ts`. Unreadable storage counts as "deferred already checked". 97 tests.

### Packaging

- Installable with Unity Package Manager from the git URL (`?path=src`) and ready for
  OpenUPM: `src/package.json` (`unity` 2021.2, samples), a `Strait.Sdk` assembly
  definition (no engine references), `csc.rsp` (nullable annotations on), stable
  `.meta` files for every package file (`scripts/unity-meta.mjs`), and a
  **Quick start** sample (`Samples~/QuickStart`, not compiled by `dotnet test`).
- Package name (`com.<brand>.sdk`), display name, URLs and copyright holder come from
  `brand.json` (applied by `scripts/brand.mjs`). MIT `LICENSE` added.
- Tag `vX.Y.Z` → GitHub Actions runs the tests and checks the package; OpenUPM
  picks the tag up by itself. See PUBLISHING.md.

## 0.3.0

Brings the SDK to parity with the React Native reference (`shared-spec/SDK-CONTRACT.md`, B1–B13).

- `StraitCore`: 1:1 ports of `browserScreenWidth`, `splitUrl` (no `System.Uri`; JS
  `decodeURIComponent` and `trim` semantics), `parseStraitLink`, `classifyUrl` and
  `normalizeLinkHosts`. Also `AppStateTracker` (2000 / 1000 ms).
- `StraitClient`: launch and running links, short-link resolve (`/v1/resolve`), and a
  deferred check once per install (Android referrer → `/v1/referrer`, else `/v1/match`).
  Adds `OnLink` with replay, `OnLinkStart`, `TrackEvent`, `ReportFingerprint`,
  `CompareFingerprint` and `Stop`. It never throws; network failure gives `reason: "network"`.
- Pluggable `IKeyValueStore`, `IStraitTransport` (default `HttpClientTransport`), device
  fields, install referrer and clock. There is no UnityEngine dependency.
- `StraitJson`: a dependency-free escaping writer and strict parser (B11). Unity needs no
  extra DLLs.
- Tests: every `conformance-vectors.json` case, the RN client scenarios ported with a fake
  transport and clock, and JSON tests.
- README: how to wire `Application.deepLinkActivated` / `absoluteURL`,
  `OnApplicationPause` / `OnApplicationFocus`, PlayerPrefs, device fields, the Play
  referrer and a WebGL transport.

## 0.1.0

- C# port of the deferred-match signature, with golden-vector parity (dotnet CI).
