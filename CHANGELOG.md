# Changelog

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
