// Device fields for the deferred match: the "Device fields" code from the README.
// /v1/match answers 400 without them, so the install is never matched.
// The values must match what the browser saw at the tap: CSS screen.width (portrait), devicePixelRatio,
// the locale and the IANA time zone. Not Screen.width, which follows render scaling and orientation.
// iOS reads them through StraitDevice.mm (next to this file).
using Strait;
using UnityEngine;

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

#if UNITY_IOS && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float _StraitScreenPortraitWidth();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float _StraitScreenScale();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string _StraitLanguage();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string _StraitTimezone();
#endif
}
