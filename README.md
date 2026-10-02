# strait-sdk-unity (C#)

Deferred deep linking for Unity games. Part of [Strait](../). The match signature
is a C# port kept in lockstep with the server + every other SDK via
[`shared-spec`](../shared-spec) golden vectors (run by `dotnet test` in CI).
C# int overflow is wrapped with `unchecked` to match JS's 32-bit `|0`.

> ⚠️ The signature core (`Strait.Signature`) is CI-verified against the golden
> vectors via dotnet. The Unity-runtime wrapper (UnityWebRequest, Screen/SystemInfo
> device fields) needs the Unity editor to verify and is documented, not bundled.

## Use (sketch)

```csharp
var sig = Strait.StraitSignature.Compute(Screen.width, screenScale, lang, ip, tz);
// POST { publishableKey, platform:"ios"/"android", screenWidth, pixelRatio, language, timezone }
// to {endpoint}/v1/match via UnityWebRequest; route to result.longUrl
```

**Publishable key:** Dashboard → Get started → Publishable key (`bk_pub_live_…`).
It's safe to include in your app. Never put your secret key (`bk_live_…`) in an app.
