#nullable enable
// Strait sample for Unity: add this component to a GameObject in your first scene.
// Keys come from Assets/Resources/strait-config.json (yours, git-ignored), else strait-config.example.json.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Strait;
using UnityEngine;

public class StraitSample : MonoBehaviour
{
    [Serializable]
    class Config { public string publishableKey = ""; public string endpoint = ""; }

    public static StraitClient? Client { get; private set; }
    readonly List<string> _log = new List<string>();

    async void Start()
    {
        DontDestroyOnLoad(gameObject);
        var file = Resources.Load<TextAsset>("strait-config") ?? Resources.Load<TextAsset>("strait-config.example");
        var config = file != null ? JsonUtility.FromJson<Config>(file.text) : new Config();
        if (string.IsNullOrEmpty(config.publishableKey) || config.publishableKey.Contains("REPLACE_ME") || string.IsNullOrEmpty(config.endpoint))
        {
            Add("Copy Assets/Resources/strait-config.example.json to strait-config.json and set your publishable key.");
            return;
        }

        Client = new StraitClient(new StraitConfig
        {
            PublishableKey = config.publishableKey,
            Endpoint = config.endpoint,
            Storage = new PlayerPrefsStore(), // keeps the once-per-install deferred flag and unsent reports
            Platform = Application.platform == RuntimePlatform.Android ? "android"
                     : Application.platform == RuntimePlatform.IPhonePlayer ? "ios" : "other",
            DeviceFields = SampleDevice.Collect,
        });
        // Direct links (straitsample://… or verified links) and the deferred link after an install.
        Client.OnLink += e => Add($"{e.Kind} link ({e.Route}, {e.AppState}): matched={e.Matched} path={e.Path ?? "-"}");
        // Links that arrive while the game is running.
        Application.deepLinkActivated += url => _ = Client.HandleUrl(url);
        // The link that launched the game ("" when none), then the deferred check once per install.
        await Client.Start(Application.absoluteURL);
    }

    void OnApplicationPause(bool paused) => Client?.OnAppState(paused ? AppLifecycle.Background : AppLifecycle.Active);
    void OnApplicationFocus(bool focused) => Client?.OnAppState(focused ? AppLifecycle.Active : AppLifecycle.Inactive);
    void OnDestroy() => Client?.Stop();

    void OnGUI()
    {
        if (GUILayout.Button("Log a purchase event")) _ = Purchase();
        lock (_log) foreach (var line in _log) GUILayout.Label(line);
    }

    async Task Purchase()
    {
        if (Client == null) { Add("Set your publishable key first."); return; }
        bool ok = await Client.TrackEvent("purchase", value: 4.99, currency: "USD");
        Add($"event purchase sent: {ok}");
    }

    void Add(string line)
    {
        lock (_log) _log.Add(line);
        Debug.Log("Strait: " + line);
    }
}

/// <summary>Keeps the deferred-check flag and unsent open reports across launches.</summary>
public sealed class PlayerPrefsStore : IKeyValueStore
{
    public Task<string?> GetItemAsync(string key) =>
        Task.FromResult<string?>(PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null);

    public Task SetItemAsync(string key, string value)
    {
        PlayerPrefs.SetString(key, value);
        PlayerPrefs.Save();
        return Task.CompletedTask;
    }
}

/// <summary>Device fields for the deferred match (Android, from the SDK README). Elsewhere: null, the SDK's defaults.</summary>
public static class SampleDevice
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
#else
        return null;
#endif
    }
}
