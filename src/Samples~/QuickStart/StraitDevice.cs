// Device fields for the deferred match: the "Device fields" code from the README.
// /v1/match answers 400 without them, so the install is never matched.
// The values must match what the browser saw at the tap: CSS screen.width (portrait), devicePixelRatio,
// the locale and the IANA time zone. Not Screen.width, which follows render scaling and orientation.
// iOS reads them through StraitDevice.mm (next to this file).
#nullable enable // Assets/ compiles with nullable off; this keeps `string?` from warning (CS8632)
using Strait;
using UnityEngine;

public static class StraitDevice
{
    public static DeviceFields? Collect()
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

    /// <summary>
    /// The game's install time in Unix ms (contract B6a): PlayerPrefs comes back from Android Auto Backup and iPhone
    /// backups, and with it the "deferred check done" flag; a changed install time tells the SDK this is a new install.
    /// Android: PackageInfo.firstInstallTime (an update keeps it). iOS has no install-time API: a marker file in
    /// Library/Application Support/Strait, excluded from backup, holds the time it was created, so a restored or new
    /// iPhone (no marker) gets a new time. A throw is fine: the SDK treats it as unknown.
    /// </summary>
    public static long? InstallTime()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
        using var pm = activity.Call<AndroidJavaObject>("getPackageManager");
        using var info = pm.Call<AndroidJavaObject>("getPackageInfo", activity.Call<string>("getPackageName"), 0);
        return info.Get<long>("firstInstallTime");
#elif UNITY_IOS && !UNITY_EDITOR
        // persistentDataPath is <app>/Documents; Library is its sibling. The time is kept inside the file, so it
        // doesn't depend on how the runtime reports file creation times.
        var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.persistentDataPath), "Library", "Application Support", "Strait");
        var file = System.IO.Path.Combine(dir, "install-time");
        // An unreadable marker is unknown (the flag alone), never a new time: a new time would count a reinstall.
        if (System.IO.File.Exists(file)) return long.TryParse(System.IO.File.ReadAllText(file), out var saved) ? saved : (long?)null;
        System.IO.Directory.CreateDirectory(dir);
        UnityEngine.iOS.Device.SetNoBackupFlag(dir);
        var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tmp = file + ".tmp";                // written whole, then renamed: a kill mid-write leaves no half marker
        System.IO.File.WriteAllText(tmp, now.ToString(System.Globalization.CultureInfo.InvariantCulture));
        System.IO.File.Move(tmp, file);
        UnityEngine.iOS.Device.SetNoBackupFlag(file);
        return now;
#else
        return null;                            // editor / desktop: the once-per-install flag alone
#endif
    }

#if UNITY_IOS && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float _StraitScreenPortraitWidth();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float _StraitScreenScale();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string _StraitLanguage();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string _StraitTimezone();
#endif
}
