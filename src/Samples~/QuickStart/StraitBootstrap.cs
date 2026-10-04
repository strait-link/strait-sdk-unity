// Quick start: add this component to a GameObject in your first scene.
// It creates one client for the game's lifetime and routes every link.
// For an exact deferred match, also supply DeviceFields and (Android) the Play
// Install Referrer — see "Device fields" and "Play Install Referrer" in the README.
using System.Collections.Generic;
using System.Threading.Tasks;
using Strait;
using UnityEngine;

public class StraitBootstrap : MonoBehaviour
{
    [Tooltip("Dashboard → Get started → Publishable key (st_pub_live_…). Never the secret key.")]
    public string publishableKey = "st_pub_live_…";
    [Tooltip("Your workspace link domain, e.g. https://<your-handle>.strait.link")]
    public string endpoint = "https://<your-handle>.strait.link";

    public static StraitClient Client { get; private set; }

    async void Start()
    {
        DontDestroyOnLoad(gameObject);
        Client = new StraitClient(new StraitConfig
        {
            PublishableKey = publishableKey,
            Endpoint = endpoint,
            Storage = new PlayerPrefsStore(),
            Platform = Application.platform == RuntimePlatform.Android ? "android"
                     : Application.platform == RuntimePlatform.IPhonePlayer ? "ios" : "other",
        });

        Client.OnLink += e =>                           // past events are replayed to late subscribers
        {
            if (e.Matched) Route(e.Path, e.Params);
            else Debug.Log($"Link not matched: {e.Kind} ({e.Reason})");
        };

        Application.deepLinkActivated += url => _ = Client.HandleUrl(url);
        await Client.Start(Application.absoluteURL);    // launch link, then the once-per-install deferred check
    }

    void OnApplicationPause(bool paused) => Client?.OnAppState(paused ? AppLifecycle.Background : AppLifecycle.Active);
    void OnApplicationFocus(bool focused) => Client?.OnAppState(focused ? AppLifecycle.Active : AppLifecycle.Inactive);
    void OnDestroy() => Client?.Stop();

    void Route(string path, IReadOnlyDictionary<string, string> query)
    {
        Debug.Log($"Open {path}");                      // load the right scene / screen here
    }
}

/// <summary>Keeps the deferred-check flag and unsent open reports across launches.</summary>
public sealed class PlayerPrefsStore : IKeyValueStore
{
    public Task<string> GetItemAsync(string key) =>
        Task.FromResult(PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null);

    public Task SetItemAsync(string key, string value)
    {
        PlayerPrefs.SetString(key, value);
        PlayerPrefs.Save();
        return Task.CompletedTask;
    }
}
