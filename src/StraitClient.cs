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
        /// <summary>Use <see cref="StraitCore.PortraitScreenWidth"/>(logical width, logical height) — B2, B17.</summary>
        public int ScreenWidth { get; set; }
        public double PixelRatio { get; set; }
        /// <summary>BCP-47 locale, e.g. "en-IN".</summary>
        public string Language { get; set; } = "en";
        /// <summary>IANA zone, e.g. "Asia/Kolkata".</summary>
        public string Timezone { get; set; } = "XX";
    }

    public sealed class StraitConfig
    {
        /// <summary>Workspace publishable key (st_pub_live_…), Dashboard → Get started. Never a secret key.</summary>
        public string PublishableKey { get; set; } = "";
        /// <summary>Your Strait link host, e.g. https://go.yourbrand.com</summary>
        public string Endpoint { get; set; } = "";
        /// <summary>Extra hosts serving your short links (custom domains): "go.brand.com" or "https://go.brand.com".</summary>
        public IList<string> LinkHosts { get; set; } = new List<string>();
        /// <summary>Persists "deferred check done" and unsent open reports across launches (e.g. a PlayerPrefs store). Default: in-memory.</summary>
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
        /// <summary>Unique per open; also the id Strait records this open under (B14).</summary>
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
    /// Every open is reported to Strait exactly once (B14); reports that don't get through are
    /// saved in <see cref="StraitConfig.Storage"/> and retried.
    /// </summary>
    public sealed class StraitClient
    {
        public const string DeferredFlag = "strait.deferredChecked";
        /// <summary>Storage key of the unsent open reports (JSON array).</summary>
        public const string QueueKey = "strait.pendingOpens";
        /// <summary>Storage key of the remembered tap (contract B15): <c>{"clickId":…,"at":…}</c>, or empty.</summary>
        public const string TapKey = "strait.lastTap";

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
        // Queue operations run one at a time (storage is async).
        private readonly SemaphoreSlim _queueLock = new SemaphoreSlim(1, 1);
        private Task? _flushing;
        // Remembered-tap writes are chained so a TrackEvent right after an open sees them.
        private Task _tapWrite = Task.CompletedTask;
        private readonly object _tapGate = new object();
        private bool _started;
        private volatile bool _stopped;

        public StraitClient(StraitConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.PublishableKey))
                throw new ArgumentException("StraitClient: publishableKey is required", nameof(config));
            if (string.IsNullOrWhiteSpace(config.Endpoint))
                throw new ArgumentException("StraitClient: endpoint is required", nameof(config));
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
        /// or else run the deferred check once per install (B6): skipped, but still marked done,
        /// when the first launch was itself opened by a link; marked done only once the engine
        /// answered. Then sends any saved open reports. Call once; later calls are no-ops.
        /// </summary>
        public async Task Start(string? initialUrl)
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
                _stopped = false;
            }
            DropStaleTap(Now()); // B18: an expired remembered tap is deleted, not kept until the next event
            bool hasInitial = !string.IsNullOrEmpty(initialUrl);
            string? flag;
            try { flag = await _storage.GetItemAsync(DeferredFlag); }
            catch { flag = "1"; } // unreadable storage = already checked: don't risk a stale deferred jump every launch
            bool firstLaunch = flag != "1";
            if (hasInitial)
            {
                // Opened by a link on first launch = the user's intent right now: no deferred
                // check, but this open still counts as the install's first.
                if (firstLaunch) await SetFlag();
                await HandleUrlInternal(initialUrl!, LinkAppStates.Closed, firstLaunch);
            }
            else if (firstLaunch)
            {
                // Marked done only once the engine answered: offline → next launch.
                var e = await RunDeferred(true);
                if (e.Reason != "network") await SetFlag();
            }
            _ = Flush();
        }

        private async Task SetFlag()
        {
            try { await _storage.SetItemAsync(DeferredFlag, "1"); }
            catch { /* best effort */ }
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

        /// <summary>
        /// Feed lifecycle changes: "active", "background" or "inactive" (<see cref="AppLifecycle"/>).
        /// Becoming active also sends any saved open reports.
        /// </summary>
        public void OnAppState(string state, long nowMs)
        {
            if (_stopped || state == null) return;
            _tracker.OnState(state, nowMs);
            if (state == AppLifecycle.Active) _ = Flush();
        }

        /// <summary>Same as <see cref="OnAppState(string, long)"/> using the configured clock.</summary>
        public void OnAppState(string state) => OnAppState(state, Now());

        /// <summary>Stop handling URLs and lifecycle changes. Subscribers and past events are kept.</summary>
        public void Stop()
        {
            lock (_gate) { _stopped = true; _started = false; }
        }

        /// <summary>
        /// Re-run the deferred check now (debugging); doesn't touch the once-per-install flag and
        /// sends no openId, so it never adds an install.
        /// </summary>
        public Task<LinkEvent> CheckDeferred() => RunDeferred(false);

        /// <summary>Open reports saved while offline, waiting to be sent (debugging).</summary>
        public Task<int> PendingOpenReports() => Serial(async () => (await ReadQueue()).Count);

        /// <summary>Send saved open reports now (also happens on Start and when the app becomes active).</summary>
        public Task FlushOpenReports() => Flush();

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

        /// <summary>
        /// Conversion / revenue event. True when the engine accepted it (2xx). Carries the tap id of the
        /// last attributed link open (at most 7 days old, contract B15) unless <paramref name="clickId"/> is given.
        /// </summary>
        public async Task<bool> TrackEvent(string name, double? value = null, string? currency = null, string? linkId = null, string? clickId = null)
        {
            try
            {
                Task pending;
                lock (_tapGate) pending = _tapWrite;
                await pending;
                string? stored = null;
                try { stored = await _storage.GetItemAsync(TapKey); }
                catch { stored = null; }
                if (StraitCore.StaleTap(stored, Now())) DropStaleTap(Now()); // B18: delete, don't just ignore
                var tapId = StraitCore.EventClickId(stored, Now(), clickId);
                var fields = new List<KeyValuePair<string, object?>>
                {
                    Kv("publishableKey", _config.PublishableKey),
                    Kv("event", name),
                    Kv("platform", _config.Platform),
                    Kv("value", value),
                    Kv("currency", currency),
                    Kv("linkId", linkId),
                    Kv("clickId", tapId),
                };
                return (await Call("POST", "/v1/event", fields)).Ok;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Remember the tap id of an attributed open (B15), or forget it (null) when a newer attributed
        /// open has no tap id the SDK knows. Never throws; failures are ignored.
        /// </summary>
        private void NoteTap(string? clickId, long at)
        {
            var value = clickId != null ? StraitCore.RememberTap(clickId, at) : "";
            lock (_tapGate)
            {
                var prev = _tapWrite;
                _tapWrite = Write(prev);
            }

            async Task Write(Task prev)
            {
                try { await prev; } catch { }
                try { await _storage.SetItemAsync(TapKey, value); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// B18: delete an expired remembered tap instead of only ignoring it. The value is re-read inside the
        /// chained write so a newer tap written meanwhile is never lost. Never throws.
        /// </summary>
        private void DropStaleTap(long now)
        {
            lock (_tapGate)
            {
                var prev = _tapWrite;
                _tapWrite = Drop(prev);
            }

            async Task Drop(Task prev)
            {
                try { await prev; } catch { }
                try
                {
                    if (StraitCore.StaleTap(await _storage.GetItemAsync(TapKey), now))
                        await _storage.SetItemAsync(TapKey, "");
                }
                catch { /* best effort */ }
            }
        }

        private long Now()
        {
            try { return _now(); } catch { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        }

        private async Task<LinkEvent> HandleUrlInternal(string raw, string appState, bool firstLaunch = false)
        {
            long t0 = Now();
            var id = StraitCore.NewOpenId(t0);
            var platform = _config.Platform;
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
                    // The lookup is also the open report (openId); the engine says whether it
                    // recorded it, and anything short of that is retried via /v1/open.
                    // B18: only host + path (+ utm_source) leave the device or reach storage.
                    var reportedUrl = StraitCore.ReportUrl(raw);
                    var report = NewReport(id, "direct", LinkRoutes.AppLink, appState, platform, reportedUrl, null, false, firstLaunch, t0);
                    try
                    {
                        var res = await Call("POST", "/v1/resolve", new List<KeyValuePair<string, object?>>
                        {
                            Kv("publishableKey", _config.PublishableKey),
                            Kv("url", reportedUrl),
                            Kv("platform", platform),
                            Kv("openId", id),
                            Kv("appState", appState),
                            Kv("firstLaunch", firstLaunch),
                            Kv("at", t0),
                        });
                        bool matched = IsTrue(res.Json, "matched");
                        ev.Matched = matched;
                        ev.Reason = matched ? null : (Str(res.Json, "reason") ?? Str(res.Json, "error"));
                        if (matched) SetDestination(ev, Str(res.Json, "longUrl"));
                        if (matched) NoteTap(StraitCore.ReplyClickId(Get(res.Json, "clickId")), t0);
                        ev.LinkId = Str(res.Json, "linkId");
                        if (!IsTrue(res.Json, "recorded"))
                        {
                            report["matched"] = matched;
                            report["reason"] = ev.Reason;
                            report["linkId"] = ev.LinkId;
                            _ = Report(report);
                        }
                    }
                    catch
                    {
                        ev.Matched = false;
                        ev.Reason = "network";
                        report["reason"] = "network";
                        _ = Enqueue(report);
                    }
                }
                else
                {
                    if (c.ClickId != null) NoteTap(c.ClickId, t0);
                    // Navigation never waits for the report.
                    _ = Report(NewReport(id, "direct", c.Route, appState, platform, StraitCore.ReportUrl(c.Url), c.ClickId, true, firstLaunch, t0));
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

        /// <summary>
        /// The deferred check. <paramref name="record"/> (the once-per-install run) sends the openId
        /// so the engine records this first open + install exactly once; the debug re-check doesn't.
        /// </summary>
        private async Task<LinkEvent> RunDeferred(bool record)
        {
            long t0 = Now();
            var id = StraitCore.NewOpenId(t0);
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
                        var referrerClick = StraitCore.ParseStraitClick(referrer);
                        var body = new List<KeyValuePair<string, object?>>
                        {
                            Kv("publishableKey", _config.PublishableKey),
                            Kv("linkId", linkId),
                            Kv("clickId", referrerClick),
                            Kv("platform", "android"),
                        };
                        if (record) { body.Add(Kv("openId", id)); body.Add(Kv("at", t0)); }
                        var res = await Answered("/v1/referrer", body);
                        if (IsTrue(res.Json, "matched"))
                        {
                            ev.Route = LinkRoutes.InstallReferrer;
                            ev.Matched = true;
                            SetDestination(ev, Str(res.Json, "longUrl"));
                            ev.LinkId = Str(res.Json, "linkId") ?? linkId;
                            if (record) NoteTap(StraitCore.ReplyClickId(Get(res.Json, "clickId"), referrerClick), t0);
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
                    if (record) { fields.Add(Kv("openId", id)); fields.Add(Kv("at", t0)); }
                    var res = await Answered("/v1/match", fields);
                    bool matched = IsTrue(res.Json, "matched");
                    ev.Matched = matched;
                    ev.Reason = matched ? null : "no_match";
                    if (matched) SetDestination(ev, Str(res.Json, "longUrl"));
                    if (record && matched) NoteTap(StraitCore.ReplyClickId(Get(res.Json, "clickId")), t0);
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

        /// <summary>Like <see cref="Call"/>, but no answer, 429 or 5xx throws (= try again next launch).</summary>
        private async Task<Response> Answered(string path, List<KeyValuePair<string, object?>> body)
        {
            var res = await Call("POST", path, body);
            if (StraitCore.ShouldRetryReport(res.Status)) throw new HttpRequestException("HTTP " + res.Status);
            return res;
        }

        // ------------------------------------------------------------------ open reports (B14)

        /// <summary>One app open as reported to POST /v1/open. Null values are left out of the JSON.</summary>
        private static Dictionary<string, object?> NewReport(string openId, string kind, string route, string appState,
            string platform, string? url, string? clickId, bool matched, bool firstLaunch, long at) =>
            new Dictionary<string, object?>
            {
                ["openId"] = openId, ["kind"] = kind, ["route"] = route, ["appState"] = appState,
                ["platform"] = platform, ["url"] = url, ["clickId"] = clickId, ["linkId"] = null,
                ["matched"] = matched, ["reason"] = null, ["firstLaunch"] = firstLaunch, ["at"] = at,
            };

        private async Task<T> Serial<T>(Func<Task<T>> fn)
        {
            await _queueLock.WaitAsync();
            try { return await fn(); }
            finally { _queueLock.Release(); }
        }

        private async Task<List<Dictionary<string, object?>>> ReadQueue()
        {
            var q = new List<Dictionary<string, object?>>();
            try
            {
                var text = await _storage.GetItemAsync(QueueKey);
                if (StraitJson.Parse(text ?? "[]") is List<object?> items)
                    foreach (var item in items)
                        if (item is Dictionary<string, object?> r)
                        {
                            // B18: reports saved by an older SDK may hold a full URL; strip it here so the
                            // next write leaves no query or fragment on the device.
                            if (r.TryGetValue("url", out var u) && u is string us) r["url"] = StraitCore.ReportUrl(us);
                            q.Add(r);
                        }
            }
            catch { /* unreadable → empty */ }
            return q;
        }

        private async Task WriteQueue(List<Dictionary<string, object?>> q)
        {
            try
            {
                var sb = new StringBuilder();
                StraitJson.WriteValue(sb, q, 0);
                await _storage.SetItemAsync(QueueKey, sb.ToString());
            }
            catch { /* best effort */ }
        }

        private static long AtOf(Dictionary<string, object?> r) =>
            r.TryGetValue("at", out var v) ? v switch { long l => l, int i => i, double d => (long)d, _ => 0 } : 0;

        private List<Dictionary<string, object?>> Prune(List<Dictionary<string, object?>> q) => StraitCore.PruneOpenQueue(q, Now(), AtOf);

        private Task Enqueue(Dictionary<string, object?> report) =>
            Serial(async () =>
            {
                var q = await ReadQueue();
                q.Add(report);
                await WriteQueue(Prune(q));
                return true;
            });

        /// <summary>POST /v1/open; the HTTP status, or null when there was no answer.</summary>
        private async Task<int?> SendReport(Dictionary<string, object?> report)
        {
            try
            {
                var body = new List<KeyValuePair<string, object?>> { Kv("publishableKey", _config.PublishableKey) };
                foreach (var kv in report) if (kv.Key != "publishableKey") body.Add(kv);
                return (await Call("POST", "/v1/open", body)).Status;
            }
            catch { return null; }
        }

        /// <summary>Report an open now; keep it for retry if it doesn't get through. Never throws.</summary>
        private async Task Report(Dictionary<string, object?> report)
        {
            try
            {
                var status = await SendReport(report);
                if (StraitCore.ShouldRetryReport(status)) await Enqueue(report);
                else _ = Flush(); // the network works: send anything saved earlier
            }
            catch { /* never throw */ }
        }

        /// <summary>Send the saved reports in order; stop at the first one that gets no answer.</summary>
        private Task Flush()
        {
            TaskCompletionSource<bool> done;
            lock (_gate)
            {
                if (_flushing != null && !_flushing.IsCompleted) return _flushing; // one flush at a time
                done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _flushing = done.Task;
            }
            _ = FlushCore(done);
            return done.Task;
        }

        private async Task FlushCore(TaskCompletionSource<bool> done)
        {
            try
            {
                await Serial(async () =>
                {
                    var keep = new List<Dictionary<string, object?>>();
                    bool offline = false;
                    foreach (var rep in Prune(await ReadQueue()))
                    {
                        // Once one gets no answer at all, keep the rest for later.
                        if (offline) { keep.Add(rep); continue; }
                        var status = await SendReport(rep);
                        offline = status == null;
                        if (StraitCore.ShouldRetryReport(status)) keep.Add(rep);
                    }
                    await WriteQueue(keep);
                    return true;
                });
            }
            catch { /* never throw */ }
            finally { done.TrySetResult(true); }
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

        private static object? Get(Dictionary<string, object?> j, string key) =>
            j.TryGetValue(key, out var v) ? v : null;

        private static string? Str(Dictionary<string, object?> j, string key) =>
            j.TryGetValue(key, out var v) && v is string s ? s : null;
    }
}
