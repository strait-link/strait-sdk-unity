using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Strait
{
    /// <summary>Persistent key/value storage (e.g. PlayerPrefs). Must survive app restarts.</summary>
    public interface IKeyValueStore
    {
        Task<string?> GetItemAsync(string key);
        Task SetItemAsync(string key, string value);
    }

    /// <summary>In-memory store: the default, and handy in tests. Not persistent.</summary>
    public sealed class MemoryKeyValueStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _data = new Dictionary<string, string>();
        public Task<string?> GetItemAsync(string key)
        {
            lock (_data) return Task.FromResult<string?>(_data.TryGetValue(key, out var v) ? v : null);
        }
        public Task SetItemAsync(string key, string value)
        {
            lock (_data) _data[key] = value;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// One HTTP request. <paramref name="jsonBody"/> is null for GET. Return the status code and
    /// body text; throw on network failure (the client turns that into reason "network").
    /// </summary>
    public interface IStraitTransport
    {
        Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody);
    }

    /// <summary>Default transport on System.Net.Http.HttpClient. (WebGL: supply a UnityWebRequest transport.)</summary>
    public sealed class HttpClientTransport : IStraitTransport
    {
        private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(() =>
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        private readonly HttpClient _http;

        public HttpClientTransport(HttpClient? http = null) { _http = http ?? Shared.Value; }

        public async Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
        {
            using (var req = new HttpRequestMessage(new HttpMethod(method), url))
            {
                if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using (var res = await _http.SendAsync(req))
                {
                    var body = res.Content == null ? "" : await res.Content.ReadAsStringAsync();
                    return ((int)res.StatusCode, body);
                }
            }
        }
    }

    /// <summary>Device fields for the deferred-match fingerprint (must agree with the browser at the tap).</summary>
    public sealed class DeviceFields
    {
        /// <summary>Use <see cref="StraitCore.BrowserScreenWidth"/>(portrait logical width) — B2.</summary>
        public int ScreenWidth { get; set; }
        public double PixelRatio { get; set; }
        /// <summary>BCP-47 locale, e.g. "en-IN".</summary>
        public string Language { get; set; } = "en";
        /// <summary>IANA zone, e.g. "Asia/Kolkata".</summary>
        public string Timezone { get; set; } = "XX";
    }

    public sealed class StraitConfig
    {
        /// <summary>Workspace publishable key (bk_pub_live_…), Dashboard → Get started. Never a secret key.</summary>
        public string PublishableKey { get; set; } = "";
        /// <summary>Your Strait link host, e.g. https://bridge-redirect-engine.onrender.com</summary>
        public string Endpoint { get; set; } = "";
        /// <summary>Extra hosts serving your short links (custom domains): "go.brand.com" or "https://go.brand.com".</summary>
        public IList<string> LinkHosts { get; set; } = new List<string>();
        /// <summary>Persists "deferred check done" across launches (e.g. a PlayerPrefs store). Default: in-memory.</summary>
        public IKeyValueStore? Storage { get; set; }
        /// <summary>Android: returns the Play Install Referrer string, or null. Only called when Platform is "android".</summary>
        public Func<Task<string?>>? InstallReferrer { get; set; }
        /// <summary>"ios", "android" or "other".</summary>
        public string Platform { get; set; } = "other";
        /// <summary>Collects the device fingerprint fields. Called on demand.</summary>
        public Func<DeviceFields?>? DeviceFields { get; set; }
        /// <summary>HTTP transport. Default: <see cref="HttpClientTransport"/>.</summary>
        public IStraitTransport? Transport { get; set; }
        /// <summary>Clock in Unix ms. Default: system clock. (Tests inject a fake.)</summary>
        public Func<long>? Clock { get; set; }
    }

    /// <summary>One event type for every link case (B9). Wire-identical to the React Native SDK's LinkEvent.</summary>
    public sealed class LinkEvent
    {
        public string Id { get; internal set; } = "";
        /// <summary>"direct" = opened by a link; "deferred" = link tapped before install.</summary>
        public string Kind { get; internal set; } = "";
        /// <summary>app_link · custom_scheme · install_referrer · fingerprint (<see cref="LinkRoutes"/>).</summary>
        public string Route { get; internal set; } = "";
        /// <summary>closed · background · foreground (<see cref="LinkAppStates"/>).</summary>
        public string AppState { get; internal set; } = "";
        public bool Matched { get; internal set; }
        /// <summary>Why it didn't match: not_found, expired, password_protected, no_match, network, invalid_url, …</summary>
        public string? Reason { get; internal set; }
        /// <summary>The URL the OS gave the app (direct links).</summary>
        public string? RawUrl { get; internal set; }
        /// <summary>The destination to navigate to.</summary>
        public string? Url { get; internal set; }
        public string? Path { get; internal set; }
        public IReadOnlyDictionary<string, string>? Params { get; internal set; }
        public string? LinkId { get; internal set; }
        /// <summary>Time spent resolving, ms.</summary>
        public long Ms { get; internal set; }
        /// <summary>When the link arrived, Unix ms.</summary>
        public long At { get; internal set; }
    }

    /// <summary>Fired the moment a link arrives, before resolving; the matching LinkEvent has the same Id.</summary>
    public sealed class LinkStart
    {
        public string Id { get; internal set; } = "";
        public string Kind { get; internal set; } = "";
        public string AppState { get; internal set; } = "";
        public string? RawUrl { get; internal set; }
        public long At { get; internal set; }
    }

    /// <summary>
    /// Strait deep-linking client: a port of sdk-react-native/src/strait.ts with no Unity
    /// dependency. The game feeds it URLs and lifecycle changes (see README); it never throws
    /// from link handling (B10). Continuations resume on the caller's SynchronizationContext,
    /// so when called from Unity's main thread, events are raised on the main thread.
    /// </summary>
    public sealed class StraitClient
    {
        public const string DeferredFlag = "strait.deferredChecked";

        private readonly StraitConfig _config;
        private readonly string _base;
        private readonly IReadOnlyList<string> _linkHosts;
        private readonly IKeyValueStore _storage;
        private readonly IStraitTransport _transport;
        private readonly Func<long> _now;
        private readonly AppStateTracker _tracker = new AppStateTracker();
        private readonly object _gate = new object();
        private readonly List<LinkEvent> _events = new List<LinkEvent>();
        private readonly List<Action<LinkEvent>> _listeners = new List<Action<LinkEvent>>();
        private readonly List<Action<LinkStart>> _startListeners = new List<Action<LinkStart>>();
        private int _seq;
        private bool _started;
        private volatile bool _stopped;

        public StraitClient(StraitConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _base = (config.Endpoint ?? "").TrimEnd('/');
            _linkHosts = StraitCore.NormalizeLinkHosts(_base, config.LinkHosts);
            _storage = config.Storage ?? new MemoryKeyValueStore();
            _transport = config.Transport ?? new HttpClientTransport();
            _now = config.Clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>Hosts treated as Strait short-link hosts (endpoint host + LinkHosts), lower-cased.</summary>
        public IReadOnlyList<string> LinkHosts => _linkHosts;

        // ------------------------------------------------------------------ events

        /// <summary>Every link event. A new subscriber first receives all past events (replay).</summary>
        public event Action<LinkEvent> OnLink
        {
            add
            {
                if (value == null) return;
                LinkEvent[] past;
                lock (_gate) { past = _events.ToArray(); _listeners.Add(value); }
                foreach (var e in past) SafeInvoke(value, e);
            }
            remove { if (value != null) lock (_gate) _listeners.Remove(value); }
        }

        /// <summary>A link just arrived and is being resolved (show a loading state until OnLink with the same Id).</summary>
        public event Action<LinkStart> OnLinkStart
        {
            add { if (value != null) lock (_gate) _startListeners.Add(value); }
            remove { if (value != null) lock (_gate) _startListeners.Remove(value); }
        }

        /// <summary>Snapshot of every event so far.</summary>
        public IReadOnlyList<LinkEvent> Events { get { lock (_gate) return _events.ToArray(); } }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>
        /// Handle the launch URL (Unity: Application.absoluteURL; null/empty if none) as "closed",
        /// then run the deferred check once per install (B6) — skipped, but still marked done,
        /// when the first launch was itself opened by a link. Call once; later calls are no-ops.
        /// </summary>
        public async Task Start(string? initialUrl)
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
                _stopped = false;
            }
            bool hasInitial = !string.IsNullOrEmpty(initialUrl);
            if (hasInitial) await HandleUrlInternal(initialUrl!, LinkAppStates.Closed);

            string? flag;
            try { flag = await _storage.GetItemAsync(DeferredFlag); }
            catch { return; } // unreadable storage: don't risk a stale deferred jump on every launch
            if (flag == "1") return;
            try { await _storage.SetItemAsync(DeferredFlag, "1"); }
            catch { /* best effort */ }
            // Opened by a link on first launch = the user's intent right now.
            if (!hasInitial) await RunDeferred();
        }

        /// <summary>
        /// A URL delivered while the app is running (Unity: Application.deepLinkActivated). Labelled
        /// background/foreground by the lifecycle tracker. Returns null after <see cref="Stop"/>.
        /// </summary>
        public async Task<LinkEvent?> HandleUrl(string raw)
        {
            if (_stopped) return null;
            return await HandleUrlInternal(raw, _tracker.Classify(Now()));
        }

        /// <summary>Feed lifecycle changes: "active", "background" or "inactive" (<see cref="AppLifecycle"/>).</summary>
        public void OnAppState(string state, long nowMs)
        {
            if (_stopped || state == null) return;
            _tracker.OnState(state, nowMs);
        }

        /// <summary>Same as <see cref="OnAppState(string, long)"/> using the configured clock.</summary>
        public void OnAppState(string state) => OnAppState(state, Now());

        /// <summary>Stop handling URLs and lifecycle changes. Subscribers and past events are kept.</summary>
        public void Stop()
        {
            lock (_gate) { _stopped = true; _started = false; }
        }

        /// <summary>Re-run the deferred check now (debugging); doesn't touch the once-per-install flag.</summary>
        public Task<LinkEvent> CheckDeferred() => RunDeferred();

        // ------------------------------------------------------------------ fingerprint + events

        /// <summary>Send this app's fingerprint (origin "app") to the engine. Parsed response, or null on network failure.</summary>
        public async Task<Dictionary<string, object?>?> ReportFingerprint()
        {
            try
            {
                var fields = new List<KeyValuePair<string, object?>>
                {
                    Kv("publishableKey", _config.PublishableKey),
                    Kv("origin", "app"),
                };
                AddDevice(fields);
                return (await Call("POST", "/v1/debug/fingerprint", fields)).Json;
            }
            catch { return null; }
        }

        /// <summary>The engine's comparison of the app and browser fingerprints on this network; null on network failure.</summary>
        public async Task<Dictionary<string, object?>?> CompareFingerprint()
        {
            try
            {
                return (await Call("GET", "/v1/debug/fingerprint?publishableKey=" + Uri.EscapeDataString(_config.PublishableKey ?? ""), null)).Json;
            }
            catch { return null; }
        }

        /// <summary>Conversion / revenue event. True when the engine accepted it (2xx).</summary>
        public async Task<bool> TrackEvent(string name, double? value = null, string? currency = null, string? linkId = null)
        {
            try
            {
                var fields = new List<KeyValuePair<string, object?>>
                {
                    Kv("publishableKey", _config.PublishableKey),
                    Kv("event", name),
                    Kv("platform", _config.Platform),
                    Kv("value", value),
                    Kv("currency", currency),
                    Kv("linkId", linkId),
                };
                return (await Call("POST", "/v1/event", fields)).Ok;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ internals

        private long Now()
        {
            try { return _now(); } catch { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        }

        private string NewId(long at) => "evt_" + at + "_" + Interlocked.Increment(ref _seq);

        private async Task<LinkEvent> HandleUrlInternal(string raw, string appState)
        {
            long t0 = Now();
            var id = NewId(t0);
            Announce(new LinkStart { Id = id, Kind = "direct", AppState = appState, RawUrl = raw, At = t0 });
            var ev = new LinkEvent { Id = id, Kind = "direct", Route = LinkRoutes.AppLink, AppState = appState, RawUrl = raw, At = t0 };
            try
            {
                var c = StraitCore.ClassifyUrl(raw, _linkHosts);
                if (c == null)
                {
                    ev.Matched = false;
                    ev.Reason = "invalid_url";
                }
                else if (c.NeedsResolve)
                {
                    try
                    {
                        var res = await Call("POST", "/v1/resolve", new List<KeyValuePair<string, object?>>
                        {
                            Kv("publishableKey", _config.PublishableKey),
                            Kv("url", raw),
                            Kv("platform", _config.Platform),
                        });
                        bool matched = IsTrue(res.Json, "matched");
                        ev.Matched = matched;
                        ev.Reason = matched ? null : (Str(res.Json, "reason") ?? Str(res.Json, "error"));
                        if (matched) SetDestination(ev, Str(res.Json, "longUrl"));
                        ev.LinkId = Str(res.Json, "linkId");
                    }
                    catch
                    {
                        ev.Matched = false;
                        ev.Reason = "network";
                    }
                }
                else
                {
                    ev.Route = c.Route;
                    ev.Matched = true;
                    ev.Url = c.Url;
                    ev.Path = c.Path;
                    ev.Params = c.Params;
                }
            }
            catch
            {
                ev.Matched = false;
                ev.Reason ??= "invalid_url";
            }
            ev.Ms = Now() - t0;
            return Emit(ev);
        }

        private async Task<LinkEvent> RunDeferred()
        {
            long t0 = Now();
            var id = NewId(t0);
            Announce(new LinkStart { Id = id, Kind = "deferred", AppState = LinkAppStates.Closed, At = t0 });
            var ev = new LinkEvent { Id = id, Kind = "deferred", Route = LinkRoutes.Fingerprint, AppState = LinkAppStates.Closed, At = t0 };
            try
            {
                bool done = false;
                if (_config.Platform == "android")
                {
                    string? referrer = null;
                    try { if (_config.InstallReferrer != null) referrer = await _config.InstallReferrer(); }
                    catch { referrer = null; }
                    var linkId = StraitCore.ParseStraitLink(referrer);
                    if (linkId != null)
                    {
                        var res = await Call("POST", "/v1/referrer", new List<KeyValuePair<string, object?>>
                        {
                            Kv("publishableKey", _config.PublishableKey),
                            Kv("linkId", linkId),
                            Kv("platform", "android"),
                        });
                        if (IsTrue(res.Json, "matched"))
                        {
                            ev.Route = LinkRoutes.InstallReferrer;
                            ev.Matched = true;
                            SetDestination(ev, Str(res.Json, "longUrl"));
                            ev.LinkId = Str(res.Json, "linkId") ?? linkId;
                            done = true;
                        }
                    }
                }
                if (!done)
                {
                    var fields = new List<KeyValuePair<string, object?>>
                    {
                        Kv("publishableKey", _config.PublishableKey),
                        Kv("platform", _config.Platform),
                    };
                    AddDevice(fields);
                    var res = await Call("POST", "/v1/match", fields);
                    bool matched = IsTrue(res.Json, "matched");
                    ev.Matched = matched;
                    ev.Reason = matched ? null : "no_match";
                    if (matched) SetDestination(ev, Str(res.Json, "longUrl"));
                    ev.LinkId = Str(res.Json, "linkId");
                }
            }
            catch
            {
                ev.Route = LinkRoutes.Fingerprint;
                ev.Matched = false;
                ev.Reason = "network";
                ev.Url = null; ev.Path = null; ev.Params = null; ev.LinkId = null;
            }
            ev.Ms = Now() - t0;
            return Emit(ev);
        }

        private void AddDevice(List<KeyValuePair<string, object?>> fields)
        {
            DeviceFields? d = null;
            try { d = _config.DeviceFields?.Invoke(); } catch { d = null; }
            if (d == null) return;
            fields.Add(Kv("screenWidth", d.ScreenWidth));
            fields.Add(Kv("pixelRatio", d.PixelRatio));
            fields.Add(Kv("language", d.Language));
            fields.Add(Kv("timezone", d.Timezone));
        }

        private struct Response
        {
            public bool Ok;
            public int Status;
            public Dictionary<string, object?> Json;
        }

        /// <summary>Throws on transport failure; an unparseable body becomes an empty object.</summary>
        private async Task<Response> Call(string method, string path, List<KeyValuePair<string, object?>>? body)
        {
            var json = body == null ? null : StraitJson.Serialize(body);
            var (status, text) = await _transport.SendAsync(method, _base + path, json);
            return new Response { Ok = status >= 200 && status < 300, Status = status, Json = StraitJson.ParseObjectOrEmpty(text) };
        }

        private static void SetDestination(LinkEvent ev, string? url)
        {
            if (string.IsNullOrEmpty(url)) return;
            ev.Url = url;
            var p = StraitCore.SplitUrl(url);
            if (p != null) { ev.Path = p.Path; ev.Params = p.Params; }
        }

        private void Announce(LinkStart s)
        {
            Action<LinkStart>[] ls;
            lock (_gate) ls = _startListeners.ToArray();
            foreach (var cb in ls) SafeInvoke(cb, s);
        }

        private LinkEvent Emit(LinkEvent e)
        {
            Action<LinkEvent>[] ls;
            lock (_gate) { _events.Add(e); ls = _listeners.ToArray(); }
            foreach (var cb in ls) SafeInvoke(cb, e);
            return e;
        }

        /// <summary>A throwing subscriber must not break link handling or other subscribers.</summary>
        private static void SafeInvoke<T>(Action<T> cb, T arg)
        {
            try { cb(arg); } catch { /* subscriber bug; swallow */ }
        }

        private static KeyValuePair<string, object?> Kv(string k, object? v) => new KeyValuePair<string, object?>(k, v);

        private static bool IsTrue(Dictionary<string, object?> j, string key) =>
            j.TryGetValue(key, out var v) && v is bool b && b;

        private static string? Str(Dictionary<string, object?> j, string key) =>
            j.TryGetValue(key, out var v) && v is string s ? s : null;
    }
}
