# AGENTS.md: Strait Unity SDK (strait-sdk-unity)

Instructions for AI coding agents (Claude Code, Cursor, Codex, Copilot…) that add this SDK to an app or work on
this repo. Humans: see README.md.

C# library for Unity 2021.2+ (Api Compatibility Level .NET Standard 2.1). The game wires it to Unity: launch URL, deep-link events, storage, device fields.

## Install

Unity → Window → Package Manager → + → Add package from git URL:

```text
https://github.com/bazingga08/strait-sdk-unity.git?path=src#v0.8.0
```

Keep `?path=src`. Android also needs `com.android.installreferrer:installreferrer:2.2` in Gradle (custom
`mainTemplate.gradle` or EDM4U). Link settings go in the exported Android / Xcode projects as for any app.

## Keys (the rule agents get wrong most)

- **Publishable key** `st_pub_live_…` (Dashboard → Get started): goes in the app. It is the only key this SDK takes (`publishableKey`).
- **Secret key** `st_live_…` (Dashboard → Settings → Secret keys): server only. Never put it in an app: anyone can extract it and change your links.
- Never commit either key's real value to this repo, tests or examples. Use placeholders like `st_pub_live_…`.

## Receive links: the one pattern

```csharp
Client = new StraitClient(new StraitConfig {
    PublishableKey = "st_pub_live_…",          // never the secret key
    Endpoint = "https://acme.strait.link",         // the workspace's link domain
    Storage = new PlayerPrefsStore(),
    Platform = /* "android" | "ios" | "other" */,
    DeviceFields = StraitDevice.Collect,       // browser-equivalent values; not Screen.width
    InstallReferrer = Application.platform == RuntimePlatform.Android ? PlayReferrer.Get : null,
});
Client.OnLink += e => { if (e.Matched) Route(e.Path, e.Params); };    // past events replay
Application.deepLinkActivated += url => _ = Client.HandleUrl(url);     // while running
await Client.Start(Application.absoluteURL);                           // launch link + deferred check
```

Forward `OnApplicationPause` / `OnApplicationFocus` to `Client.OnAppState(...)`. On WebGL set
`Transport = new UnityWebRequestTransport()`.

## Verify

Run these; don't assume.

```sh
# 1. The link domain serves the verification files with this app in them
curl https://<handle>.strait.link/.well-known/assetlinks.json              # Android: package + every SHA-256
curl https://<handle>.strait.link/.well-known/apple-app-site-association   # iPhone: TeamID.bundleId
#    (or the free checker: https://straitlink.in/tools/  ·  MCP tool: check_app_links)

# 2. Android verified the host (fresh install). Want: verified
adb shell pm get-app-links <package.name>
```

3. Tap a link from WhatsApp or Gmail on a real phone: the app opens on the right screen and `onLink` fires
   with `matched: true`. The tap and the open appear in Dashboard → Analytics.
4. Deferred (Android): install from a Google Play internal-testing build, tap the link before installing, open
   the app: `onLink` fires with `kind: deferred`, `route: install_referrer`. iPhone install matching is in beta.

If links open the browser: a missing SHA-256 (most often the Play App Signing key from Play Console → App
integrity), a typo in the host, or the app was installed before the files were right (reinstall). See
https://straitlink.in/docs/troubleshooting/.

## Working on this repo

- Test: `dotnet test test/Strait.Signature.Tests.csproj   # .NET 8, what CI runs` (must pass before any commit; check the exit code).
- The match signature and the pure helpers are pinned by shared golden vectors
  (`test/*vectors*.json`): byte-identical copies live in every SDK and the engine. Never edit a vector file
  here alone; vectors change only through `shared-spec/` and land in every repo together.
- The package's public identity (name, scope, owner, domain) lives only in `brand.json`; change it with
  `shared-spec/scripts/rename-brand.sh` (all SDKs) or `node scripts/brand.mjs --write`.
- Wire names are part of the contract: query params `strait_click` / `strait_link`, storage keys `strait.*`,
  headers `X-Strait-*`. Don't rename them.
- Brand: Strait (never "Straight"). Don't write superlatives ("best", "cheapest") or speed / match-rate numbers in
  docs or comments. iPhone install matching is in beta.

## More

- Docs for this SDK: https://straitlink.in/docs/sdks/unity/
- All docs: https://straitlink.in/docs/ · REST API: https://straitlink.in/docs/api/
- Strait from AI tools (MCP server: create links, check App Links files, trace taps): https://straitlink.in/ai/
