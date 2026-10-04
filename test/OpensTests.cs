using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>
    /// Port of sdk-react-native/test/opens.test.ts. Contract B14: every link open is reported
    /// exactly once, retried until it gets through, and never delays navigation. Plus the B6/B7 revisions.
    /// </summary>
    public class OpensTests
    {
        private const string PK = "st_pub_test_appowner01";
        private const string Endpoint = "https://links.test";
        private const string Click = "3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f";
        private const string OpenIdPattern = "^o_[a-z0-9]+_[a-z0-9]{12}$";

        private static DeviceFields Device() => new DeviceFields { ScreenWidth = 411, PixelRatio = 2.625, Language = "en", Timezone = "Asia/Kolkata" };

        private sealed class Reply
        {
            public int Status = 200;
            public string Body = "{}";
            public bool Offline;
            public bool Hang;
        }

        private static Reply Ok(string body) => new Reply { Body = body };
        private static Reply Status(int status, string body = "{}") => new Reply { Status = status, Body = body };
        private static Reply Offline() => new Reply { Offline = true };
        private static Reply Hang() => new Reply { Hang = true };

        private static Reply Resolved(bool recorded = true) => Ok(
            "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\",\"slug\":\"sale\",\"recorded\":" +
            (recorded ? "true" : "false") + "}");
        private static Reply Accepted() => Status(202, "{\"ok\":true,\"duplicate\":false}");
        private static Reply NoMatch() => Ok("{\"matched\":false,\"matchMethod\":\"none\"}");

        private sealed class Call
        {
            public string Path = "";
            public JsonElement Body;
        }

        /// <summary>Fake engine whose answer per path can change mid-test. Unknown path → 404.</summary>
        private sealed class FakeEngine : IStraitTransport
        {
            public readonly Dictionary<string, Reply> Routes;
            public readonly List<Call> Calls = new List<Call>();

            public FakeEngine(params (string path, Reply reply)[] routes) { Routes = routes.ToDictionary(r => r.path, r => r.reply); }

            public List<Call> Of(string path) { lock (Calls) return Calls.Where(c => c.Path == path).ToList(); }

            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                lock (Calls) Calls.Add(new Call { Path = path, Body = JsonDocument.Parse(jsonBody ?? "{}").RootElement.Clone() });
                var r = Routes.TryGetValue(path, out var reply) ? reply : Status(404, "{\"error\":\"not found\"}");
                if (r.Offline) throw new HttpRequestException("Network request failed");
                if (r.Hang) return new TaskCompletionSource<(int, string)>().Task;
                return Task.FromResult((r.Status, r.Body));
            }
        }

        private sealed class Harness
        {
            public StraitClient Strait = null!;
            public List<LinkEvent> Events = new List<LinkEvent>();
            public IKeyValueStore Storage = null!;
            public long T = 1_800_000_000_000;
            public void Tap(string url) => _ = Strait.HandleUrl(url); // like the OS delivering a URL
            public void SetState(string s) => Strait.OnAppState(s);
            public void Advance(long ms) => T += ms;
        }

        private static Harness Make(FakeEngine engine, IKeyValueStore? storage = null, string? referrer = null, string platform = "android")
        {
            var h = new Harness { Storage = storage ?? new MemoryKeyValueStore() };
            h.Strait = new StraitClient(new StraitConfig
            {
                PublishableKey = PK,
                Endpoint = Endpoint,
                Storage = h.Storage,
                Platform = platform,
                InstallReferrer = () => Task.FromResult(referrer),
                DeviceFields = Device,
                Transport = engine,
                Clock = () => h.T,
            });
            h.Strait.OnLink += e => { lock (h.Events) h.Events.Add(e); };
            return h;
        }

        private static MemoryKeyValueStore Returning()
        {
            var s = new MemoryKeyValueStore();
            s.SetItemAsync(StraitClient.DeferredFlag, "1").Wait(); // not the first launch
            return s;
        }

        private static async Task Settle()
        {
            for (int i = 0; i < 20; i++) await Task.Delay(1);
        }

        /// <summary>Like toMatchObject: each listed key has this value (null = absent).</summary>
        private static void AssertBody(JsonElement body, params (string key, object? value)[] expected)
        {
            foreach (var (key, value) in expected)
            {
                bool has = body.TryGetProperty(key, out var v);
                switch (value)
                {
                    case null: Assert.False(has, key + " should be absent"); break;
                    case string s: Assert.True(has, key); Assert.Equal(s, v.GetString()); break;
                    case bool b: Assert.True(has, key); Assert.Equal(b, v.GetBoolean()); break;
                    case long l: Assert.True(has, key); Assert.Equal(l, v.GetInt64()); break;
                    case int i: Assert.True(has, key); Assert.Equal(i, v.GetInt32()); break;
                    case double d: Assert.True(has, key); Assert.Equal(d, v.GetDouble()); break;
                    default: throw new ArgumentException(key);
                }
            }
        }

        private static void AssertDevice(JsonElement body) =>
            AssertBody(body, ("screenWidth", 411), ("pixelRatio", 2.625), ("language", "en"), ("timezone", "Asia/Kolkata"));

        // ---------------------------------------------- browser hand-off (custom scheme) with a tap id

        [Fact]
        public async Task HandOff_ReportsTheOpenWithItsTapId_AppNeverSeesTheTapId()
        {
            var engine = new FakeEngine(("/v1/open", Accepted()));
            var h = Make(engine, Returning());
            await h.Strait.Start(null);
            h.SetState("background"); h.Advance(5000); h.SetState("active"); h.Advance(200);
            h.Tap($"straitlink://shop.example/p/42?color=red&strait_click={Click}");
            await Settle();
            var e = h.Events.Last();
            Assert.Equal("custom_scheme", e.Route);
            Assert.Equal("https://shop.example/p/42?color=red", e.Url);
            Assert.Equal(new Dictionary<string, string> { ["color"] = "red" }, e.Params!.ToDictionary(kv => kv.Key, kv => kv.Value));
            Assert.Equal("background", e.AppState);
            Assert.Matches(OpenIdPattern, e.Id);
            var open = Assert.Single(engine.Of("/v1/open"));
            AssertBody(open.Body, ("publishableKey", PK), ("openId", e.Id), ("kind", "direct"), ("route", "custom_scheme"),
                ("appState", "background"), ("platform", "android"), ("url", "https://shop.example/p/42"), // B18: no query
                ("clickId", Click), ("matched", true), ("firstLaunch", false));
        }

        [Fact]
        public async Task HandOff_NavigationNeverWaitsForTheReport()
        {
            var engine = new FakeEngine(("/v1/open", Hang()));
            var h = Make(engine, Returning());
            await h.Strait.Start($"straitlink://shop.example/p/1?strait_click={Click}");
            Assert.Single(h.Events);
            Assert.Equal("https://shop.example/p/1", h.Events[0].Url);
        }

        [Fact]
        public async Task CustomersOwnHttpsLink_IsReportedToo_NoTapId()
        {
            var engine = new FakeEngine(("/v1/open", Accepted()));
            await Make(engine, Returning()).Strait.Start("https://shop.example/p/9");
            await Settle();
            var body = engine.Of("/v1/open")[0].Body;
            AssertBody(body, ("route", "app_link"), ("url", "https://shop.example/p/9"), ("appState", "closed"), ("clickId", null));
        }

        // ---------------------------------------------- short link: the lookup is the report

        [Fact]
        public async Task ShortLink_SendsOpenIdAndAppStateWithResolve_NothingElseOnceRecorded()
        {
            var engine = new FakeEngine(("/v1/resolve", Resolved()), ("/v1/open", Accepted()));
            var h = Make(engine, Returning());
            await h.Strait.Start("https://links.test/sale");
            await Settle();
            AssertBody(engine.Of("/v1/resolve")[0].Body, ("openId", h.Events[0].Id), ("appState", "closed"),
                ("firstLaunch", false), ("at", h.Events[0].At));
            Assert.Empty(engine.Of("/v1/open"));
        }

        [Fact]
        public async Task ShortLink_AnsweredButNotRecorded_RetriedViaOpenWithSameOpenId()
        {
            var engine = new FakeEngine(("/v1/resolve", Resolved(recorded: false)), ("/v1/open", Accepted()));
            var h = Make(engine, Returning());
            await h.Strait.Start("https://links.test/sale");
            await Settle();
            Assert.True(h.Events[0].Matched);
            AssertBody(engine.Of("/v1/open")[0].Body, ("openId", h.Events[0].Id), ("route", "app_link"),
                ("url", "https://links.test/sale"), ("matched", true), ("linkId", "lnk_42"));
        }

        [Fact]
        public async Task ShortLink_Offline_SavedThenSentWithSameOpenIdWhenTheAppComesBack()
        {
            var storage = Returning();
            var engine = new FakeEngine(("/v1/resolve", Offline()), ("/v1/open", Offline()));
            var h = Make(engine, storage);
            await h.Strait.Start(null);
            h.Tap("https://links.test/sale");
            await Settle();
            var last = h.Events.Last();
            Assert.False(last.Matched);
            Assert.Equal("network", last.Reason);
            Assert.Equal(1, await h.Strait.PendingOpenReports());
            // network returns; user leaves and comes back
            engine.Routes["/v1/open"] = Accepted();
            h.SetState("background"); h.Advance(10_000); h.SetState("active");
            await Settle();
            var sent = engine.Of("/v1/open").Where(c => c.Body.GetProperty("openId").GetString() == last.Id).ToList();
            AssertBody(sent.Last().Body, ("route", "app_link"), ("url", "https://links.test/sale"), ("matched", false), ("reason", "network"));
            Assert.Equal(0, await h.Strait.PendingOpenReports());
        }

        // ---------------------------------------------- the retry queue

        [Fact]
        public async Task Queue_KeepsReportsOn5xxAnd429_DropsThemOn4xx()
        {
            var engine = new FakeEngine(("/v1/open", Status(503)));
            var h = Make(engine, Returning());
            await h.Strait.Start(null);
            h.Tap("straitlink://a.b/1");
            await Settle();
            Assert.Equal(1, await h.Strait.PendingOpenReports());
            engine.Routes["/v1/open"] = Status(429);
            await h.Strait.FlushOpenReports();
            Assert.Equal(1, await h.Strait.PendingOpenReports());
            engine.Routes["/v1/open"] = Status(400, "{\"error\":\"bad\"}");
            await h.Strait.FlushOpenReports();
            Assert.Equal(0, await h.Strait.PendingOpenReports());
        }

        [Fact]
        public async Task Queue_SurvivesAnAppRestart_AndIsSentOnTheNextStart()
        {
            var storage = Returning();
            var e1 = new FakeEngine(("/v1/open", Offline()));
            var first = Make(e1, storage);
            await first.Strait.Start(null);
            first.Tap("straitlink://a.b/1");
            first.Tap("straitlink://a.b/2");
            await Settle();
            Assert.Equal(2, await first.Strait.PendingOpenReports());
            first.Strait.Stop();

            var e2 = new FakeEngine(("/v1/open", Accepted()));
            var second = Make(e2, storage);
            await second.Strait.Start(null);
            await Settle();
            Assert.Equal(new[] { "https://a.b/1", "https://a.b/2" }, e2.Of("/v1/open").Select(c => c.Body.GetProperty("url").GetString()).ToArray());
            Assert.Equal(0, await second.Strait.PendingOpenReports());
        }

        [Fact]
        public async Task Queue_ASuccessfulReportAlsoSendsAnythingSavedEarlier()
        {
            var engine = new FakeEngine(("/v1/open", Offline()));
            var h = Make(engine, Returning());
            await h.Strait.Start(null);
            h.Tap("straitlink://a.b/old");
            await Settle();
            engine.Routes["/v1/open"] = Accepted();
            h.Tap("straitlink://a.b/new");
            await Settle();
            Assert.Equal(0, await h.Strait.PendingOpenReports());
            var oldIds = engine.Of("/v1/open").Where(c => c.Body.GetProperty("url").GetString() == "https://a.b/old")
                .Select(c => c.Body.GetProperty("openId").GetString()).Distinct();
            Assert.Single(oldIds);
        }

        [Fact]
        public async Task EveryOpenHasItsOwnId()
        {
            var engine = new FakeEngine(("/v1/open", Accepted()));
            var h = Make(engine, Returning());
            await h.Strait.Start(null);
            for (int i = 0; i < 5; i++) h.Tap($"straitlink://a.b/{i}");
            await Settle();
            Assert.Equal(5, h.Events.Select(e => e.Id).Distinct().Count());
        }

        // ---------------------------------------------- first launch and the deferred check (B6/B7 revised)

        [Fact]
        public async Task FirstLaunchOpenedByALink_NoDeferredCheck_TheOpenCountsAsTheInstall()
        {
            var engine = new FakeEngine(("/v1/resolve", Resolved()));
            var h = Make(engine);
            await h.Strait.Start("https://links.test/sale");
            AssertBody(engine.Of("/v1/resolve")[0].Body, ("firstLaunch", true));
            Assert.Empty(engine.Of("/v1/referrer"));
            Assert.Empty(engine.Of("/v1/match"));
            Assert.Equal("1", await h.Storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task PlayReferrer_SendsTheTapIdAndTheOpenId()
        {
            var engine = new FakeEngine(("/v1/referrer", Ok(
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\",\"matchMethod\":\"install_referrer\"}")));
            var h = Make(engine, referrer: $"utm_source=google-play&strait_link=lnk_42&strait_click={Click}");
            await h.Strait.Start(null);
            AssertBody(engine.Of("/v1/referrer")[0].Body, ("linkId", "lnk_42"), ("clickId", Click), ("openId", h.Events[0].Id),
                ("at", h.Events[0].At), ("platform", "android"));
            var e = h.Events[0];
            Assert.Equal("deferred", e.Kind);
            Assert.Equal("install_referrer", e.Route);
            Assert.True(e.Matched);
        }

        [Fact]
        public async Task Fingerprint_Ios_SendsTheOpenId()
        {
            var engine = new FakeEngine(("/v1/match", NoMatch()));
            var h = Make(engine, platform: "ios");
            await h.Strait.Start(null);
            var body = engine.Of("/v1/match")[0].Body;
            AssertBody(body, ("openId", h.Events[0].Id), ("platform", "ios"));
            AssertDevice(body);
        }

        [Fact]
        public async Task DeferredOffline_NotMarkedDone_SoTheNextLaunchChecksAgain()
        {
            var storage = new MemoryKeyValueStore();
            var e1 = new FakeEngine(("/v1/match", Offline()));
            var first = Make(e1, storage);
            await first.Strait.Start(null);
            Assert.Equal("deferred", first.Events[0].Kind);
            Assert.Equal("network", first.Events[0].Reason);
            Assert.Null(await storage.GetItemAsync(StraitClient.DeferredFlag));

            var e2 = new FakeEngine(("/v1/match", NoMatch()));
            await Make(e2, storage).Strait.Start(null);
            Assert.Single(e2.Of("/v1/match"));
            Assert.Equal("1", await storage.GetItemAsync(StraitClient.DeferredFlag));

            var e3 = new FakeEngine(("/v1/match", NoMatch()));
            await Make(e3, storage).Strait.Start(null);
            Assert.Empty(e3.Of("/v1/match")); // once per install
        }

        [Fact]
        public async Task DeferredServerError5xx_CountsAsNotAnswered()
        {
            var storage = new MemoryKeyValueStore();
            var h = Make(new FakeEngine(("/v1/match", Status(502))), storage);
            await h.Strait.Start(null);
            Assert.Equal("network", h.Events[0].Reason);
            Assert.Null(await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task DebugRecheck_NeverRecordsAnInstall_NoOpenId()
        {
            var engine = new FakeEngine(("/v1/match", NoMatch()));
            var h = Make(engine, Returning());
            await h.Strait.Start(null);
            await h.Strait.CheckDeferred();
            var call = Assert.Single(engine.Of("/v1/match"));
            AssertBody(call.Body, ("openId", null), ("at", null));
        }

        private sealed class BrokenStore : IKeyValueStore
        {
            public bool ReadFails;
            public Task<string?> GetItemAsync(string key) =>
                ReadFails ? throw new System.IO.IOException("io") : Task.FromResult<string?>(null);
            public Task SetItemAsync(string key, string value) => throw new System.IO.IOException("full");
        }

        [Fact]
        public async Task UnreadableStorage_IsAlreadyChecked_NoDeferredJump_WriteFailuresNeverThrow()
        {
            var engine = new FakeEngine(("/v1/match", NoMatch()), ("/v1/resolve", Resolved()));
            await Make(engine, new BrokenStore { ReadFails = true }).Strait.Start(null);
            Assert.Empty(engine.Of("/v1/match"));
            var e2 = new FakeEngine(("/v1/match", NoMatch()));
            await Make(e2, new BrokenStore()).Strait.Start(null); // completes without throwing
            var e3 = new FakeEngine(("/v1/resolve", Resolved()));
            var h3 = Make(e3, new BrokenStore());
            await h3.Strait.Start("https://links.test/sale");
            Assert.Single(h3.Events);
        }

        // ---------------------------------------------- B18: no query or fragment leaves the device or reaches storage

        private static string? Stored(IKeyValueStore s, string key) => s.GetItemAsync(key).Result;

        [Fact]
        public async Task B18_OpenReportKeepsOnlyHostPathAndUtmSource_AppStillGetsFullUrl()
        {
            var engine = new FakeEngine(("/v1/open", Accepted()));
            var h = Make(engine, Returning());
            await h.Strait.Start(null);
            h.Tap("https://shop.example/p/42?email=jo%40x.com&utm_source=sms#reset-token");
            await Settle();
            var e = h.Events.Last();
            Assert.Equal("https://shop.example/p/42?email=jo%40x.com&utm_source=sms#reset-token", e.Url);
            Assert.Equal("jo@x.com", e.Params!["email"]);
            Assert.Equal("sms", e.Params!["utm_source"]);
            Assert.Equal("https://shop.example/p/42?utm_source=sms", engine.Of("/v1/open")[0].Body.GetProperty("url").GetString());
        }

        [Fact]
        public async Task B18_FailedShortLinkLookupIsQueuedAndResolvedWithoutQueryOrFragment()
        {
            var engine = new FakeEngine(("/v1/resolve", Offline()), ("/v1/open", Offline()));
            var h = Make(engine, Returning());
            await h.Strait.Start(null);
            h.Tap("https://links.test/sale?session=s3cr3t&utm_source=wa#frag");
            await Settle();
            Assert.Equal("https://links.test/sale?utm_source=wa", engine.Of("/v1/resolve")[0].Body.GetProperty("url").GetString());
            var saved = Stored(h.Storage, StraitClient.QueueKey)!;
            Assert.DoesNotContain("s3cr3t", saved);
            Assert.Equal("https://links.test/sale?utm_source=wa", JsonDocument.Parse(saved).RootElement[0].GetProperty("url").GetString());
        }

        [Fact]
        public async Task B18_ReportsSavedByAnOlderSdkAreStrippedBeforeSentOrSavedAgain()
        {
            var engine = new FakeEngine(("/v1/open", Offline()));
            var storage = Returning();
            await storage.SetItemAsync(StraitClient.QueueKey,
                "[{\"openId\":\"o_old_aaaaaaaaaaaa\",\"kind\":\"direct\",\"route\":\"app_link\",\"appState\":\"closed\",\"platform\":\"android\"," +
                "\"url\":\"https://shop.example/p?token=abc#x\",\"matched\":true,\"firstLaunch\":false,\"at\":1799999999000}]");
            var h = Make(engine, storage);
            await h.Strait.Start(null);
            await h.Strait.FlushOpenReports();
            Assert.Equal("https://shop.example/p", engine.Of("/v1/open")[0].Body.GetProperty("url").GetString());
            Assert.DoesNotContain("token", Stored(storage, StraitClient.QueueKey)!);
        }

        [Fact]
        public async Task B18_ExpiredRememberedTapIsDeletedAtStart()
        {
            var storage = Returning();
            await storage.SetItemAsync(StraitClient.TapKey, StraitCore.RememberTap(Click, 1_800_000_000_000 - 8L * 24 * 3600 * 1000));
            var h = Make(new FakeEngine(), storage);
            await h.Strait.Start(null);
            await Settle();
            Assert.Equal("", Stored(storage, StraitClient.TapKey));
        }

        [Fact]
        public async Task B18_TapThatExpiresWhileRunningIsDeletedByTrackEventAndNotSent()
        {
            var engine = new FakeEngine(("/v1/event", Accepted()));
            var storage = Returning();
            await storage.SetItemAsync(StraitClient.TapKey, StraitCore.RememberTap(Click, 1_800_000_000_000));
            var h = Make(engine, storage);
            await h.Strait.Start(null);
            await Settle();
            Assert.Contains(Click, Stored(storage, StraitClient.TapKey)!); // still valid at start
            h.Advance(7L * 24 * 3600 * 1000 + 1);
            await h.Strait.TrackEvent("purchase");
            await Settle();
            Assert.False(engine.Of("/v1/event")[0].Body.TryGetProperty("clickId", out _));
            Assert.Equal("", Stored(storage, StraitClient.TapKey));
        }

        [Fact]
        public async Task B18_ValidRememberedTapIsKeptAndSent()
        {
            var engine = new FakeEngine(("/v1/event", Accepted()));
            var storage = Returning();
            await storage.SetItemAsync(StraitClient.TapKey, StraitCore.RememberTap(Click, 1_800_000_000_000 - 1000));
            var h = Make(engine, storage);
            await h.Strait.Start(null);
            await h.Strait.TrackEvent("purchase");
            await Settle();
            Assert.Equal(Click, engine.Of("/v1/event")[0].Body.GetProperty("clickId").GetString());
            Assert.Contains(Click, Stored(storage, StraitClient.TapKey)!);
        }

        // ---------------------------------------------- empty key / endpoint

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyPublishableKeyIsRejected(string? key)
        {
            var ex = Assert.Throws<ArgumentException>(() => new StraitClient(new StraitConfig { PublishableKey = key!, Endpoint = Endpoint }));
            Assert.StartsWith("StraitClient: publishableKey is required", ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyEndpointIsRejected(string? endpoint)
        {
            var ex = Assert.Throws<ArgumentException>(() => new StraitClient(new StraitConfig { PublishableKey = PK, Endpoint = endpoint! }));
            Assert.StartsWith("StraitClient: endpoint is required", ex.Message);
        }
    }
}
