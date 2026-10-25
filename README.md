<!-- Header: the same in every Strait SDK README (design system v5). -->
<p align="center">
  <a href="https://straitlink.in">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset=".github/assets/strait-lockup-dark.svg">
      <img src=".github/assets/strait-lockup.svg" alt="Strait" width="160" height="53">
    </picture>
  </a>
</p>

<h1 align="center">Strait SDK for Unity</h1>

<p align="center"><strong>Straight to the screen. On the record.</strong><br>
A tap opens the exact screen, and every link open and install is recorded in your Strait dashboard.</p>

<p align="center">
  <a href="https://github.com/strait-link/strait-sdk-unity/tags"><img alt="Latest version" src="https://img.shields.io/github/v/tag/strait-link/strait-sdk-unity?sort=semver&label=version&style=flat-square&labelColor=0F0D0A&color=423B33"></a>
  <a href="https://straitlink.in/platform-status/"><img alt="SDK: Beta" src="https://img.shields.io/badge/SDK-beta-423B33?style=flat-square&labelColor=0F0D0A"></a>
  <a href="https://straitlink.in/iphone-status/"><img alt="iPhone: Live" src="https://img.shields.io/badge/iPhone-live-423B33?style=flat-square&labelColor=0F0D0A"></a>
  <a href="https://github.com/strait-link/strait-sdk-unity/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/strait-link/strait-sdk-unity/ci.yml?branch=main&label=CI&style=flat-square&labelColor=0F0D0A"></a>
  <a href="LICENSE"><img alt="Licence: MIT" src="https://img.shields.io/badge/licence-MIT-423B33?style=flat-square&labelColor=0F0D0A"></a>
</p>

<p align="center">
  <a href="https://straitlink.in/docs/sdks/unity/">Docs</a> ·
  <a href="https://straitlink.in/platform-status/">Platform status</a> ·
  <a href="CHANGELOG.md">Changelog</a> ·
  <a href="#docs-and-support">Talk to the Strait team</a>
</p>

`strait-sdk-unity` (C#)

Deep links and deferred deep links for Unity games, part of [Strait](https://straitlink.in).
Version **0.8.7**. It follows the Strait SDK contract like the React Native reference SDK,
except that `LinkEvent` has no `referralCode` yet (referrals are a preview, contract B21).

The library is plain C# (`netstandard2.1`) with **no UnityEngine dependency**, so
`dotnet test` in CI checks it against the shared golden vectors
(`test/test-vectors.json` and `test/conformance-vectors.json`). The game connects it to
Unity's APIs, as shown below. It has no third-party DLLs: JSON is handled by a small
built-in writer and parser (`StraitJson`).

> ⚠️ The library is CI-verified with dotnet. The Unity glue below (MonoBehaviour,
> PlayerPrefs, AndroidJavaObject, iOS plugin, UnityWebRequest) has not been run in the
> Unity editor or on a device. It is a documented sketch: check it on real devices
> before you ship.

## Quick start (60 seconds)

**1. Install.** In **Window → Package Manager → + → Add package from git URL…**, paste the git URL from [Install](#install).

**2. Check your key and a link.** In **Dashboard → Links**, make a link with the path `summer` and the destination `https://shop.example.com/product/42`. Then ask Strait about it the way this SDK will:

```sh
curl -s https://<your-handle>.strait.link/v1/resolve -H 'Content-Type: application/json' \
  -d '{"publishableKey":"st_pub_live_…","url":"https://<your-handle>.strait.link/summer","platform":"android"}'
```

**You should see:** `{"matched":true,"longUrl":"https://shop.example.com/product/42",…}`. The key comes from **Dashboard → Get started** and is safe in apps. `"reason":"not_found"` means the key is from another workspace or the path is wrong. The check counts as one tap in **Dashboard → Analytics**.

**3. Wire it.** In Package Manager, open this package's **Samples** and import **Quick start**: it adds a ready-made `StraitBootstrap` MonoBehaviour. Put your key and link address in it, then follow [Wire it into a game](#wire-it-into-a-game).

## Platform features

| Feature | Status | Notes |
|---|---|---|
| Android: direct links and deferred links | ● Live |  |
| iPhone links (Universal Links) | ● Live | The links themselves (Apple's Universal Links plus our association file) were proven on a real iPhone with the Swift reference app on 21 Oct 2026. This SDK's iOS glue is covered by unit tests and hasn't run on a real iPhone. The Unity glue itself is beta (row below). |
| iPhone: install matching (deferred links) | Opt-in | Off until you choose it. Proven on iPhone simulators; the real-iPhone run waits on our App Store listing. Your choice in Dashboard → Settings → iPhone installs: device matching, paste handoff, both or neither, read from Strait at runtime. Device matching is off by default. iPhone matches are labelled Estimated until measured. |
| Unity glue (MonoBehaviour, PlayerPrefs, plugins) | ◐ Beta | Not yet run in the Unity editor or on a device. |
| C# library (`netstandard2.1`) | ◐ Beta | CI-checked with `dotnet test` against the shared vectors. |
| OpenUPM | – Not yet | Add it from the git URL until it is listed. |

● Live · ◐ Beta · ○ Planned · – Not yet · Opt-in: off until you switch it on. The same words as the [platform status](https://straitlink.in/platform-status/) page.

## Install

Requires Unity 2021.2+ with **Api Compatibility Level = .NET Standard 2.1**.

<!-- brand:install -->
Unity **Window → Package Manager → + → Add package from git URL…**:

```text
https://github.com/strait-link/strait-sdk-unity.git?path=src#v0.8.7
```

Or with [OpenUPM](https://openupm.com): `openupm add com.strait.sdk`.
<!-- /brand:install -->

The OpenUPM listing is still pending; the git URL above works today.

The package (folder `src/`) compiles into the `Strait.Sdk` assembly; everything is in
namespace `Strait`. Package Manager → this package → *Samples* → **Quick start** imports a
ready-made `StraitBootstrap` MonoBehaviour, with the device fields below already wired
(`StraitDevice.cs` + `StraitDevice.mm`; don't add a second copy of either).

Without Package Manager: copy `src/*.cs` into your project (for example
`Assets/Strait/`), plus `src/Plugins/iOS/StraitClipboard.mm` for paste handoff, or
build `src/Strait.Signature.csproj` and drop the DLL into `Assets/Plugins/`. A DLL built
outside Unity has no iOS clipboard (`IosStraitClipboard.Default` is null there), so for
paste handoff pass your own `Clipboard`.

**Publishable key:** Dashboard → Get started → Publishable key (`st_pub_live_…`). It is
safe to put in your app. Never put your secret key (`st_live_…`) in an app.

## Contract coverage

| # | Behaviour | Status |
|---|---|---|
| B1 | `publishableKey` in every body (`/v1/match`, `/v1/referrer`, `/v1/resolve`, `/v1/open`, `/v1/event`, `/v1/debug/fingerprint`) | ✓ |
| B2 | `StraitCore.BrowserScreenWidth` = `ceil(w - 0.001)` | ✓ (the game supplies the width, see below) |
| B3 | Short-link hosts (endpoint + `LinkHosts`, via `NormalizeLinkHosts`) → `POST /v1/resolve {publishableKey,url,platform}` | ✓ |
| B4 | `ClassifyUrl` (custom scheme → `https://host/path?q`; `strait_click` tap id removed via `TakeClickId`, returned as `ClickId`) | ✓ |
| B5 | `closed` for the launch URL, else `AppStateTracker` (2000 / 1000 ms) | ✓ |
| B6 | Deferred check once per install (`strait.deferredChecked`), skipped but marked when launched by a link; marked only once the engine answered (no answer / 429 / 5xx → `reason:"network"`, retried next launch); `CheckDeferred()` sends no `openId` | ✓ |
| B7 | Android: referrer `strait_link` → `/v1/referrer {linkId, clickId, openId, at}`, else or on a miss → `/v1/match` (same `openId`) | ✓ (the game supplies the referrer) |
| B8 | iOS: `/v1/match` with device fields + `openId`, `at` | ✓ |
| B9 | One `LinkEvent` type, replayed to late `OnLink` subscribers; `OnLinkStart` with the same id | ✓ |
| B10 | Never throws; network failure → `matched:false, reason:"network"` | ✓ |
| B11 | All JSON goes through an escaping writer | ✓ |
| B12 | `SplitUrl` matches the vectors exactly (no `System.Uri`) | ✓ |
| B13 | `TrackEvent`, `ReportFingerprint` (origin `app`), `CompareFingerprint` | ✓ |
| B14 | Every open reported exactly once (`NewOpenId` = `LinkEvent.Id`); failed reports saved under `strait.pendingOpens` and retried; `PendingOpenReports()`, `FlushOpenReports()`; `deviceClass` (`StraitConfig.DeviceClass`: `StraitCore.DeviceClassForModel` on iOS, `DeviceClassForSize(smallestScreenWidthDp)` on Android) on `/v1/resolve` and `/v1/open` | ✓ (the game supplies it, see below) |
| B15 | `TrackEvent` carries the tap id of the last attributed open (`strait.lastTap`, ≤7 days); `clickId:` overrides | ✓ |
| B16 | Every attributed open supplies the tap id: the `clickId` in the `/v1/resolve`, `/v1/match` and `/v1/referrer` replies is remembered (`ReplyClickId`) | ✓ |
| B17 | `screenWidth` is the portrait (shorter-side) width in any orientation: `StraitCore.PortraitScreenWidth(w, h)` | ✓ (the game supplies the size, see below) |
| B18 | Only host + path (+ the first `utm_source`) of a reported URL go to `/v1/open` / `/v1/resolve` or into `strait.pendingOpens` (`StraitCore.ReportUrl`; older queued reports stripped on read); an expired remembered tap is deleted at `Start` and by `TrackEvent` (`StraitCore.StaleTap`); an empty `PublishableKey` or `Endpoint` throws `ArgumentException` | ✓ |
| B19 | iPhone paste handoff, chosen in the Dashboard and read live from the `/v1/match` reply (`ios.pasteHandoff`): device matching first, then detect without a prompt, read only when a web URL is likely, `StraitCore.ParseHandoffUrl`, `POST /v1/handoff/claim`; `ClaimHandoff(text)` for a paste button | ✓ (C# core + client CI-tested; the native `StraitClipboard.mm` is not compiled in CI, see below) |
| B22 | Old Firebase page.link links: on a `*.page.link` link host, a short link → `/v1/resolve` (the engine matches the imported old host + code); a long link (`/?link=<http(s) URL>`) → its `link=` value is the destination, read on the device with no lookup (`StraitCore.PageLinkLongLink`) | ✓ (unit tests only, not on a device) |
| B23 | `Doctor()` setup check: key, endpoint, and the link host's `assetlinks.json` / `apple-app-site-association` lists this game (`Application.identifier`) and, on Android, its signing certificate (`Doctor.Findings`, ids and wording from shared-spec ADR 0002); one GET, never throws, logs each finding | ✓ (unit tests + a live run against a real link host; not on a device) |

### What Strait records automatically (no extra code)

Every time a link opens the game, the client reports it once (contract B14):

| How the game opened | Reported via | Joined to |
|---|---|---|
| Verified link tapped in WhatsApp, Gmail, Messages… | `/v1/resolve` (the lookup is the report) | the link; also counted as a tap |
| Browser handed off to the game (`yourgame://…`) | `/v1/open` | the exact tap (`strait_click`, removed before `OnLink` sees the URL) |
| First open after a Play install | `/v1/referrer` | the exact tap that sent the user to the store |
| First open after an App Store install | `/v1/match` (then `/v1/handoff/claim` when the workspace turned on paste handoff) | the matched tap |
| Your own https links | `/v1/open` | the URL (tap id removed); the server keeps host + path only, never the query |

Reports that can't be sent (offline, server busy) are saved in `Storage` (the same
store as the deferred flag, e.g. `PlayerPrefsStore`) and retried on the next `Start`,
whenever `OnAppState("active")` is called (so wire `OnApplicationPause` /
`OnApplicationFocus` as shown below), and after any report that gets through, for up
to 7 days (max 100). The engine de-duplicates by open id, so nothing is counted twice.
Navigation never waits for a report. The first launch of an install is marked as
such, so dashboards can tell **new users** (installed and opened) from **existing
users** (already had the game). The deferred check is only marked done once the
server answered, so an offline first launch is retried on the next launch.

## Wire it into a game

```csharp
using System;
using System.Threading.Tasks;
using Strait;
using UnityEngine;

public class StraitLinks : MonoBehaviour
{
    public static StraitClient Client { get; private set; }

    async void Start()
    {
        DontDestroyOnLoad(gameObject);
        Client = new StraitClient(new StraitConfig
        {
            PublishableKey = "st_pub_live_…",
            Endpoint = "https://<your-handle>.strait.link",
            LinkHosts = { "go.yourbrand.com" },          // custom domains (coming soon), if any
            Storage = new PlayerPrefsStore(),
            Platform = Application.platform == RuntimePlatform.Android ? "android"
                     : Application.platform == RuntimePlatform.IPhonePlayer ? "ios" : "other",
            DeviceFields = StraitDevice.Collect,
            DeviceClass = StraitDevice.Class,          // phone / tablet on open reports (see Device fields)
            InstallReferrer = Application.platform == RuntimePlatform.Android ? PlayReferrer.Get : null,
            // Transport = new UnityWebRequestTransport(),   // required on WebGL (no HttpClient there)
        });

        Client.OnLinkStart += s => ShowSpinner();       // resolving can take a few seconds on a slow network
        Client.OnLink += e =>                           // past events are replayed to you
        {
            HideSpinner();
            if (e.Matched) Route(e.Path, e.Params);     // e.Kind direct/deferred, e.AppState, e.LinkId …
            else Debug.Log($"Strait: {e.Kind} link not matched ({e.Reason})");
        };

        // Links that arrive while the game is running:
        Application.deepLinkActivated += url => _ = Client.HandleUrl(url);
        // The link that launched the game ("" when none), then the deferred check (once per install):
        await Client.Start(Application.absoluteURL);
    }

    // Lifecycle → app-state labels (background vs foreground). Coming back to the
    // front ("active") also sends any open reports saved while offline.
    void OnApplicationPause(bool paused) => Client?.OnAppState(paused ? AppLifecycle.Background : AppLifecycle.Active);
    void OnApplicationFocus(bool focused) => Client?.OnAppState(focused ? AppLifecycle.Active : AppLifecycle.Inactive);
    void OnDestroy() => Client?.Stop();

    void ShowSpinner() { /* … */ }
    void HideSpinner() { /* … */ }
    void Route(string path, System.Collections.Generic.IReadOnlyDictionary<string, string> query) { /* … */ }
}
```

Call `Start` and `HandleUrl` from the main thread. The client awaits without
`ConfigureAwait(false)`, so `OnLink` and `OnLinkStart` fire on Unity's main thread, and
`PlayerPrefs` is only touched there. A subscriber that throws is isolated: it cannot
break link handling or other subscribers.

Analytics and the debug fingerprint check:

```csharp
await StraitLinks.Client.TrackEvent("purchase", value: 4.99, currency: "USD", linkId: lastLink?.LinkId);
var mine = await StraitLinks.Client.ReportFingerprint();    // Dictionary<string, object?>, or null when offline
var cmp  = await StraitLinks.Client.CompareFingerprint();   // the engine's app-vs-browser comparison
```

`TrackEvent` carries the tap id of the last attributed link open for 7 days, so the dashboard
can place the revenue on that tap's channel and A/B variant (contracts B15/B16). Every
attributed open supplies one: a browser hand-off, a Play install, or the engine's reply to a
verified short link or a deferred match. A newer open replaces the older tap. Pass `clickId:`
to set it yourself.

### Check your setup (doctor)

Most "the link opens the browser, not my game" problems are setup: the game's package / bundle ID or its
Android signing certificate isn't listed in Dashboard → Apps & stores, or a secret key is in the game.
`Doctor()` checks your key, your endpoint and your link host's `/.well-known/assetlinks.json` (Android) or
`/.well-known/apple-app-site-association` (iOS) against this game (contract B23). It makes one GET, never
throws, logs each finding and returns a `DoctorReport`. Call it in debug builds only; it never runs by itself.

```csharp
if (Debug.isDebugBuild) _ = StraitLinks.Client.Doctor(Application.identifier, log: Debug.Log);
// Android: also pass this build's signing certificate SHA-256, if you have it: Doctor(Application.identifier, "FA:C6:…", Debug.Log)
```

You should see (all set up, Android):

```
[Strait doctor] ok key.ok: Publishable key is set
[Strait doctor] ok app.listed: <handle>.strait.link lists com.yourcompany.game
```

On iOS the listed app shows with its Team ID (`<handle>.strait.link lists ABCDE12345.com.yourcompany.game`).
`report.Ok` is false when any finding is an `error`; each one's `Fix` says what to change, e.g.
`[Strait doctor] error app.missing: <handle>.strait.link doesn't list com.yourcompany.game. Fix: Add
com.yourcompany.game in Dashboard → Apps & stores (listed now: …).` In the Editor (`Platform = "other"`) it
checks the key and endpoint only. Intent filters and Associated Domains stay with the free web checker and the
docs: the SDK can't see them. `Doctor.Findings(...)` is the same check as a pure function over a file you
fetched yourself.

### PlayerPrefs storage

```csharp
public sealed class PlayerPrefsStore : IKeyValueStore
{
    public Task<string> GetItemAsync(string key) =>
        Task.FromResult(PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null);
    public Task SetItemAsync(string key, string value)
    {
        PlayerPrefs.SetString(key, value);
        PlayerPrefs.Save();                     // persist now; the deferred flag and saved open reports must survive a crash
        return Task.CompletedTask;
    }
}
```

### Device fields (deferred-match fingerprint)

The values must match what the browser saw when the user tapped the link: CSS
`screen.width` (portrait, rounded like Chrome), `devicePixelRatio`, the locale and the IANA
time zone. Do not use `Screen.width`, which follows Unity's render scaling and orientation.
Without them `/v1/match` answers 400 and the install is never matched. Pass the collector
as `DeviceFields = StraitDevice.Collect` (the Quick start sample does).
`StraitDevice.Class` below gives `DeviceClass` (phone or tablet, sent with open reports):
the hardware model on iOS (an iPad says "iPad…" even for an iPhone-only game) and the
standard smallest-width rule on Android (600 dp or more = tablet).

```csharp
public static class StraitDevice
{
    public static DeviceFields Collect()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
        using var res = activity.Call<AndroidJavaObject>("getResources");
        using var dm = res.Call<AndroidJavaObject>("getDisplayMetrics");
        float density = dm.Get<float>("density");
        using var locale = new AndroidJavaClass("java.util.Locale").CallStatic<AndroidJavaObject>("getDefault");
        using var tz = new AndroidJavaClass("java.util.TimeZone").CallStatic<AndroidJavaObject>("getDefault");
        return new DeviceFields
        {
            ScreenWidth = StraitCore.PortraitScreenWidth(Display.main.systemWidth / density, Display.main.systemHeight / density),
            PixelRatio = density,
            Language = locale.Call<string>("toLanguageTag"),
            Timezone = tz.Call<string>("getID"),
        };
#elif UNITY_IOS && !UNITY_EDITOR
        return new DeviceFields
        {
            ScreenWidth = StraitCore.BrowserScreenWidth(_StraitScreenPortraitWidth()),
            PixelRatio = _StraitScreenScale(),
            Language = _StraitLanguage(),
            Timezone = _StraitTimezone(),
        };
#else
        return null;                            // editor / desktop: the match request omits device fields
#endif
    }

    /// <summary>Phone or tablet for open reports (contract B14). iPad Safari sends a Mac user agent, so only the game can say iPad.</summary>
    public static string? Class()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
        using var res = activity.Call<AndroidJavaObject>("getResources");
        using var conf = res.Call<AndroidJavaObject>("getConfiguration");
        return StraitCore.DeviceClassForSize(conf.Get<int>("smallestScreenWidthDp")); // >= 600 dp = tablet
#elif UNITY_IOS && !UNITY_EDITOR
        return StraitCore.DeviceClassForModel(SystemInfo.deviceModel);              // "iPad13,4" -> tablet
#else
        return null;                            // editor / desktop: not sent
#endif
    }

#if UNITY_IOS && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float _StraitScreenPortraitWidth();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float _StraitScreenScale();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string _StraitLanguage();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string _StraitTimezone();
#endif
}
```

`Assets/Plugins/iOS/StraitDevice.mm` (Unity has no API for `UIScreen.scale` or the IANA zone):

```objc
#import <UIKit/UIKit.h>
static char *StraitDup(NSString *s) { const char *c = [s UTF8String]; char *r = (char *)malloc(strlen(c) + 1); strcpy(r, c); return r; }
extern "C" {
  float _StraitScreenPortraitWidth() { CGSize s = UIScreen.mainScreen.bounds.size; return (float)MIN(s.width, s.height); }
  float _StraitScreenScale() { return (float)UIScreen.mainScreen.scale; }
  char *_StraitLanguage() { return StraitDup(NSLocale.preferredLanguages.firstObject ?: @"en"); }
  char *_StraitTimezone() { return StraitDup(NSTimeZone.localTimeZone.name); }
}
```

### Old Firebase page.link links (B22)

Moving off Firebase Dynamic Links? People who already have your game can keep opening it from old
`<x>.page.link` links, as long as Google keeps serving page.link's app-link files (it still does today;
nobody but Google controls page.link):

1. Keep `applinks:<x>.page.link` (iOS) and the page.link intent filter (Android) in your next build.
2. Add the host: `LinkHosts = { "<x>.page.link" }`.
3. Import your old links in the dashboard (Import → Firebase); each keeps its old host and code.

A page.link short link (`https://<x>.page.link/aBcD`) is then looked up like any Strait short link; a long link
(`https://<x>.page.link/?link=https://…`) opens its `link=` destination on the device, with no network call.
People without your game still land on whatever Google serves. Tested in unit tests only, not yet on a real
iPhone.

### iPhone deferred links (opt-in): you choose the method (B19)

On iPhone there is no install referrer. **You choose, in the Dashboard (Settings → iPhone
installs), how Strait finds the link after an install.** There are two switches, and any
combination works:

| Device matching | Paste handoff | What happens on the first launch after install |
|---|---|---|
| off | off | No deferred link on iPhone. The install is counted; the game opens normally. |
| on | off | Device matching only. |
| off | on | Paste handoff only. |
| on | on | Device matching first; paste handoff only when it finds nothing. |

- **Device matching** matches the first open to the tap with a few short-lived signals
  (IP kept only as a keyed hash plus its /24 or /48 network prefix, screen, language, time
  zone, iOS version), kept for one
  hour and used only to open the right screen in your game. It is routing, not tracking:
  no advertising, no sharing, no linking across apps. **Apple policy note:** Apple's
  rules say fingerprinting is not allowed, whether or not the user allows tracking. Read
  how it works and decide whether it fits your app's App Store review:
  https://straitlink.in/docs/iphone-install-matching/ (also has a ready-made privacy
  label section).
- **Paste handoff** makes the tap page's "Get the app" button copy a short-lived,
  single-use Strait link. On first launch the SDK asks iOS, without a prompt, whether the
  clipboard probably holds a web link (`UIPasteboard detectPatterns`, iOS 15+; on older iOS
  the clipboard is never read); only then
  does it read it, and **iOS shows its "Allow Paste" prompt** to the player. A Strait
  handoff link is claimed with `POST /v1/handoff/claim` for an exact match
  (`LinkEvent.Route` = `"clipboard"`); anything else is ignored on the device.

**Device matching is off by default for new workspaces** (existing workspaces keep their
settings). Paste handoff is off by default.

**The choice applies at runtime, with no game update.** The game sets nothing for this.
On the first launch after install (iOS only, once), the SDK calls `/v1/match`; the
engine's reply carries the workspace's current choice (`ios.pasteHandoff`), and the SDK
reads the clipboard only when that reply found no match and says paste handoff is on. The
choice is never stored on the device. If the request fails, the clipboard is not touched
and the check runs again on the next launch (`Reason` = `"network"`). Both attempts share
one `openId`. `CheckDeferred()` (debug) never reads the clipboard.

`StraitConfig.ClipboardBoost` is obsolete and ignored (kept so old code compiles).

The iOS clipboard comes from `IosStraitClipboard` (default in iOS player builds), backed
by `src/Plugins/iOS/StraitClipboard.mm`. You can pass your own `IStraitClipboard` as
`Clipboard`. **Paste-button alternative (no prompt):** show your own iOS paste control
(e.g. a native `UIPasteControl`, iOS 16+) and give the text it receives to
`await strait.ClaimHandoff(text)`; the player's tap on the system paste button is the
consent, and the result arrives as a normal `OnLink` event.

> ⚠️ `StraitClipboard.mm` and the `[DllImport("__Internal")]` wrapper have **not** been
> compiled or run: this repository's CI is .NET only and nobody has built it in Xcode or
> tried it on an iPhone yet. The C# logic around it (when to detect, read, claim or fall
> back) is unit-tested in CI with a clipboard spy, for all four Dashboard combinations,
> including that the clipboard is never touched unless the engine says paste handoff is on.

### Android Play Install Referrer

Add `com.android.installreferrer:installreferrer:2.2` to your Gradle dependencies, either
with a custom `mainTemplate.gradle` or with EDM4U. Then:

```csharp
public static class PlayReferrer
{
    public static async Task<string> Get()
    {
        var tcs = new TaskCompletionSource<string>();
        using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
        var client = new AndroidJavaClass("com.android.installreferrer.api.InstallReferrerClient")
            .CallStatic<AndroidJavaObject>("newBuilder", activity).Call<AndroidJavaObject>("build");
        client.Call("startConnection", new Listener(client, tcs));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        return done == tcs.Task ? tcs.Task.Result : null;   // no referrer → Strait falls back to /v1/match
    }

    sealed class Listener : AndroidJavaProxy
    {
        readonly AndroidJavaObject _client; readonly TaskCompletionSource<string> _tcs;
        public Listener(AndroidJavaObject c, TaskCompletionSource<string> t)
            : base("com.android.installreferrer.api.InstallReferrerStateListener") { _client = c; _tcs = t; }
        void onInstallReferrerSetupFinished(int code)
        {
            try
            {
                if (code != 0) { _tcs.TrySetResult(null); return; }         // 0 = OK
                using var details = _client.Call<AndroidJavaObject>("getInstallReferrer");
                _tcs.TrySetResult(details.Call<string>("getInstallReferrer"));
            }
            catch { _tcs.TrySetResult(null); }
            finally { _client.Call("endConnection"); }
        }
        void onInstallReferrerServiceDisconnected() => _tcs.TrySetResult(null);
    }
}
```

### WebGL: UnityWebRequest transport

`HttpClient` does not work on WebGL. Supply a transport there. It must throw on network
failure, which the client reports as `reason: "network"`:

```csharp
public sealed class UnityWebRequestTransport : IStraitTransport
{
    public Task<(int Status, string Body)> SendAsync(string method, string url, string json)
    {
        var tcs = new TaskCompletionSource<(int, string)>();
        var req = new UnityEngine.Networking.UnityWebRequest(url, method)
            { downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer() };
        if (json != null)
        {
            req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            req.SetRequestHeader("Content-Type", "application/json");
        }
        req.SendWebRequest().completed += _ =>
        {
            if (req.result == UnityEngine.Networking.UnityWebRequest.Result.ConnectionError)
                tcs.TrySetException(new Exception(req.error));
            else
                tcs.TrySetResult(((int)req.responseCode, req.downloadHandler.text ?? ""));
            req.Dispose();
        };
        return tcs.Task;
    }
}
```

## API summary

- `StraitCore`: `BrowserScreenWidth`, `PortraitScreenWidth`, `DeviceClassForSize`, `DeviceClassForModel`, `SplitUrl`, `ParseStraitLink`, `ParseStraitClick`, `TakeClickId`,
  `ClassifyUrl`, `NormalizeLinkHosts`, `ParseHandoffUrl`, `PruneOpenQueue`, `ShouldRetryReport`, `NewOpenId`
  (pure, vector-tested; `OpenQueueMax = 100`, `OpenQueueMaxAgeMs` = 7 days).
  `AppStateTracker` (`ResumeWindowMs = 2000`, `TransientPauseMs = 1000`).
- `StraitClient`: `Start(initialUrl)`, `HandleUrl(raw)`, `OnAppState(state[, nowMs])`,
  `event OnLink` (replays past events), `event OnLinkStart`, `Events`, `CheckDeferred()`
  (doesn't touch the once-per-install flag), `ReportFingerprint()`, `CompareFingerprint()`,
  `TrackEvent(name, value, currency, linkId, clickId)`, `ClaimHandoff(text)`, `PendingOpenReports()`, `FlushOpenReports()`, `Doctor(appId, certSha256, log)`, `Stop()`.
- `Doctor`: `Findings(publishableKey, endpoint, platform, appId, certSha256, file)` (pure, B23) → `DoctorReport {Ok, Findings}` of `DoctorFinding {Id, Severity, Title, Fix}`.
- `LinkEvent`: `Id, Kind, Route, AppState, Matched, Reason, RawUrl, Url, Path, Params, LinkId, Ms, At`.
  These are the React Native SDK's fields and wire values, minus `referralCode`.
- `StraitSignature`: deferred-match signature port (`H32`, `Compute`). C# int overflow is
  wrapped with `unchecked` to match JS's 32-bit `|0`.

## Size and speed

Measured 25 Oct 2026 on 0.8.7.

| What | Size or time | How it was measured |
|---|---|---|
| Package (`src/`) | 37 KB gzipped, 122 KB on disk (C# source plus one iOS `.mm`), no dependencies | `tar czf` of `src/` |
| Start-up | not measured yet: this Mac has no Unity or .NET. `new StraitClient(...)` does no I/O; network work starts in `Start(initialUrl)` | n/a |

## Example app

[`examples/unity`](examples/unity) is a Unity project whose `StraitSample` component opens a deep link
(`Application.deepLinkActivated` and the launch URL), shows the deferred link after an install and logs a
`purchase` event. Its `Packages/manifest.json` installs the SDK from this repo's `src/`.

1. Copy `examples/unity/Assets/Resources/strait-config.example.json` to `strait-config.json` (git-ignored)
   and set your publishable key (`st_pub_…`, Dashboard → Get started).
2. Open `examples/unity` in Unity Hub (2022.3), add the `StraitSample` component to a GameObject in your
   first scene.
3. Player Settings: add the `straitsample` URL scheme, build to a phone and open `straitsample://product/42`.

CI has no Unity licence, so it compiles the example script against `src/` and a stub of the UnityEngine
APIs it uses, with Unity's C# 9: `dotnet build examples/unity/ci/StraitSample.Lint.csproj -warnaserror`.

## Test

```sh
dotnet test test/Strait.Signature.Tests.csproj   # what CI runs (.NET 8)
```

## Docs and support

- **Docs:** [straitlink.in/docs/sdks/unity/](https://straitlink.in/docs/sdks/unity/) · [platform status](https://straitlink.in/platform-status/) · [troubleshooting](https://straitlink.in/docs/troubleshooting/)
- **Talk to the Strait team:** [support@straitlink.in](mailto:support@straitlink.in) (replies within 1 working day, IST) or call +91 81218 61890.
- **Bugs and feature requests:** [open an issue](https://github.com/strait-link/strait-sdk-unity/issues) on this repo.
- **Security:** never in a public issue. Write to security@straitlink.in (see [SECURITY.md](SECURITY.md)).
