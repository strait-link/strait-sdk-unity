# Changelog

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
