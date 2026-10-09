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
    /// <summary>Port of sdk-react-native/test/strait.test.ts with a fake transport + clock.</summary>
    public class StraitClientTests
    {
        private const string PK = "st_pub_test_appowner01";
        private const string Endpoint = "https://links.test";

        private static DeviceFields Device() => new DeviceFields { ScreenWidth = 411, PixelRatio = 2.625, Language = "en", Timezone = "Asia/Kolkata" };

        private sealed class Call
        {
            public string Method = "";
            public string Url = "";
            public string Path = "";
            public JsonElement? Body;
        }

        /// <summary>Fake engine: routes by path, records every call. Unknown path → 404.</summary>
        private sealed class FakeEngine : IStraitTransport
        {
            private readonly Dictionary<string, string> _routes;
            public readonly List<Call> Calls = new List<Call>();
            public bool Offline;

            public FakeEngine(Dictionary<string, string>? routes = null) { _routes = routes ?? new Dictionary<string, string>(); }

            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                Calls.Add(new Call
                {
                    Method = method, Url = url, Path = path,
                    Body = jsonBody == null ? (JsonElement?)null : JsonDocument.Parse(jsonBody).RootElement.Clone(),
                });
                if (Offline) throw new HttpRequestException("offline");
                return Task.FromResult(_routes.TryGetValue(path, out var body) ? (200, body) : (404, ""));
            }
        }

        private sealed class Clock { public long T = 1_000_000; public long Now() => T; }

        private sealed class Harness
        {
            public StraitClient Strait = null!;
            public List<LinkEvent> Events = new List<LinkEvent>();
            public FakeEngine Engine = null!;
            public Clock Clock = null!;
            public MemoryKeyValueStore Storage = null!;
        }

        private static Harness Make(FakeEngine engine, string? referrer = null, MemoryKeyValueStore? storage = null,
            string platform = "android", IList<string>? linkHosts = null)
        {
            var h = new Harness { Engine = engine, Clock = new Clock(), Storage = storage ?? new MemoryKeyValueStore() };
            h.Strait = new StraitClient(new StraitConfig
            {
                PublishableKey = PK,
                Endpoint = Endpoint,
                LinkHosts = linkHosts ?? new List<string>(),
                Storage = h.Storage,
                Platform = platform,
                InstallReferrer = () => Task.FromResult(referrer),
                DeviceFields = Device,
                Transport = engine,
                Clock = h.Clock.Now,
            });
            h.Strait.OnLink += e => h.Events.Add(e);
            return h;
        }

        private static Dictionary<string, string> Routes(params (string path, string json)[] r) => r.ToDictionary(x => x.path, x => x.json);

        private static Dictionary<string, string> Resolved() => Routes(("/v1/resolve",
            "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42?color=red\",\"linkId\":\"lnk_42\",\"slug\":\"sale\"}"));

        private static Dictionary<string, string> ReferrerHit() => Routes(("/v1/referrer",
            "{\"matched\":true,\"longUrl\":\"https://shop.example/promo/DIWALI20\",\"linkId\":\"lnk_7\",\"matchMethod\":\"install_referrer\"}"));

        private static string S(JsonElement? body, string key) => body!.Value.GetProperty(key).GetString()!;

        private static void AssertDevice(JsonElement body)
        {
            Assert.Equal(411, body.GetProperty("screenWidth").GetInt32());
            Assert.Equal(2.625, body.GetProperty("pixelRatio").GetDouble());
            Assert.Equal("en", body.GetProperty("language").GetString());
            Assert.Equal("Asia/Kolkata", body.GetProperty("timezone").GetString());
        }

        // ------------------------------------------------------------ direct links

        [Fact]
        public async Task AppClosed_VerifiedLinkResolvesShortUrlToDestination()
        {
            var h = Make(new FakeEngine(Resolved()));
            await h.Strait.Start("https://links.test/sale");
            Assert.Single(h.Events);
            var e = h.Events[0];
            Assert.Equal("direct", e.Kind);
            Assert.Equal("app_link", e.Route);
            Assert.Equal("closed", e.AppState);
            Assert.True(e.Matched);
            Assert.Null(e.Reason);
            Assert.Equal("https://links.test/sale", e.RawUrl);
            Assert.Equal("https://shop.example/p/42?color=red", e.Url);
            Assert.Equal("/p/42", e.Path);
            Assert.Equal("red", e.Params!["color"]);
            Assert.Equal("lnk_42", e.LinkId);
            Assert.Equal(1_000_000, e.At);
            Assert.Matches("^o_lfls_[a-z0-9]{12}$", e.Id); // newOpenId(1_000_000)

            var call = h.Engine.Calls.Single(c => c.Path == "/v1/resolve");
            Assert.Equal("POST", call.Method);
            Assert.Equal("https://links.test/v1/resolve", call.Url);
            Assert.Equal(PK, S(call.Body, "publishableKey"));
            Assert.Equal("https://links.test/sale", S(call.Body, "url"));
            Assert.Equal("android", S(call.Body, "platform"));
            Assert.False(call.Body!.Value.TryGetProperty("appId", out _));
        }

        [Fact]
        public async Task AppInBackground_ClassifiedAsBackground()
        {
            var h = Make(new FakeEngine(Resolved()));
            await h.Strait.Start(null);
            h.Strait.OnAppState("background");
            h.Clock.T += 60_000;
            h.Strait.OnAppState("active");
            h.Clock.T += 300;
            await h.Strait.HandleUrl("https://links.test/sale");
            var e = h.Events.Last();
            Assert.Equal("direct", e.Kind);
            Assert.Equal("background", e.AppState);
            Assert.True(e.Matched);
        }

        [Fact]
        public async Task AppInBackground_LinkDeliveredBeforeActive_RealAndroidOrder()
        {
            var h = Make(new FakeEngine(Resolved()));
            await h.Strait.Start(null);
            h.Strait.OnAppState("background");
            h.Clock.T += 60_000;
            var pending = h.Strait.HandleUrl("https://links.test/sale"); // onNewIntent before onResume
            h.Strait.OnAppState("active");
            await pending;
            Assert.Equal("background", h.Events.Last().AppState);
        }

        [Fact]
        public async Task AppOnScreen_BriefDeliveryPauseIsNotBackground()
        {
            var h = Make(new FakeEngine(Resolved()));
            await h.Strait.Start(null);
            h.Clock.T += 30_000;
            h.Strait.OnAppState("background"); // onPause caused by the incoming intent
            h.Clock.T += 40;
            var pending = h.Strait.HandleUrl("https://links.test/sale");
            h.Clock.T += 30;
            h.Strait.OnAppState("active");
            await pending;
            Assert.Equal("foreground", h.Events.Last().AppState);
        }

        [Fact]
        public async Task AppOnScreen_ClassifiedAsForeground()
        {
            var h = Make(new FakeEngine(Resolved()));
            await h.Strait.Start(null);
            h.Clock.T += 30_000;
            await h.Strait.HandleUrl("https://links.test/sale");
            var e = h.Events.Last();
            Assert.Equal("direct", e.Kind);
            Assert.Equal("foreground", e.AppState);
        }

        [Fact]
        public async Task CustomSchemeHandOff_CarriesDestination_NoResolve()
        {
            var h = Make(new FakeEngine());
            await h.Strait.Start("straitlink://shop.example/p/42?color=red");
            var e = h.Events[0];
            Assert.Equal("direct", e.Kind);
            Assert.Equal("custom_scheme", e.Route);
            Assert.Equal("closed", e.AppState);
            Assert.True(e.Matched);
            Assert.Equal("https://shop.example/p/42?color=red", e.Url);
            Assert.Equal("/p/42", e.Path);
            Assert.Equal("red", e.Params!["color"]);
            Assert.DoesNotContain(h.Engine.Calls, c => c.Path == "/v1/resolve");
        }

        [Fact]
        public async Task ExpiredShortLink_IsReportedNotDropped()
        {
            var h = Make(new FakeEngine(Routes(("/v1/resolve", "{\"matched\":false,\"reason\":\"expired\"}"))));
            await h.Strait.Start("https://links.test/old");
            var e = h.Events[0];
            Assert.Equal("direct", e.Kind);
            Assert.Equal("app_link", e.Route);
            Assert.False(e.Matched);
            Assert.Equal("expired", e.Reason);
            Assert.Null(e.Url);
        }

        [Fact]
        public async Task ResolveErrorField_UsedWhenNoReason()
        {
            var h = Make(new FakeEngine(Routes(("/v1/resolve", "{\"error\":\"not_found\"}"))));
            await h.Strait.Start("https://links.test/nope");
            Assert.Equal("not_found", h.Events[0].Reason);
        }

        [Fact]
        public async Task LateSubscribers_StillReceivePastEvents()
        {
            var h = Make(new FakeEngine());
            await h.Strait.Start("straitlink://shop.example/cart");
            var late = new List<LinkEvent>();
            h.Strait.OnLink += e => late.Add(e);
            Assert.Single(late);
            Assert.Equal("/cart", late[0].Path);
        }

        [Fact]
        public async Task NetworkFailure_DirectLink_IsMatchedFalseReasonNetwork_NeverThrows()
        {
            var engine = new FakeEngine(Resolved()) { Offline = true };
            var h = Make(engine);
            await h.Strait.Start("https://links.test/sale");
            var e = h.Events[0];
            Assert.Equal("direct", e.Kind);
            Assert.Equal("app_link", e.Route);
            Assert.False(e.Matched);
            Assert.Equal("network", e.Reason);
        }

        [Fact]
        public async Task OnLinkStart_FiresBeforeResolving_WithSameId()
        {
            var h = Make(new FakeEngine(Resolved()));
            var starts = new List<(LinkStart start, int eventsAtStart)>();
            h.Strait.OnLinkStart += s => starts.Add((s, h.Events.Count));
            await h.Strait.Start(null); // deferred check → one start
            h.Clock.T += 30_000;
            await h.Strait.HandleUrl("https://links.test/sale");
            Assert.Equal(2, starts.Count);
            Assert.Equal("deferred", starts[0].start.Kind);
            Assert.Equal("closed", starts[0].start.AppState);
            var direct = starts[1];
            Assert.Equal("direct", direct.start.Kind);
            Assert.Equal("foreground", direct.start.AppState);
            Assert.Equal("https://links.test/sale", direct.start.RawUrl);
            Assert.Equal(1, direct.eventsAtStart); // announced before its event was emitted
            Assert.Equal(direct.start.Id, h.Events.Last().Id);
            Assert.NotEqual(starts[0].start.Id, direct.start.Id);
        }

        [Fact]
        public async Task InvalidUrl_ReportsInvalidUrl()
        {
            var h = Make(new FakeEngine());
            await h.Strait.Start("not a url");
            Assert.False(h.Events[0].Matched);
            Assert.Equal("invalid_url", h.Events[0].Reason);
            Assert.Equal("closed", h.Events[0].AppState);
        }

        [Fact]
        public async Task ThrowingSubscriber_DoesNotBreakHandlingOrOthers()
        {
            var h = Make(new FakeEngine(Resolved()));
            h.Strait.OnLink += _ => throw new InvalidOperationException("game bug");
            var after = new List<LinkEvent>();
            h.Strait.OnLink += e => after.Add(e);
            await h.Strait.Start("https://links.test/sale");
            Assert.Single(h.Events);
            Assert.True(h.Events[0].Matched);
            Assert.Single(after);
        }

        [Fact]
        public async Task CustomDomainLinkHosts_BareOrUrl_AreShortLinks()
        {
            var resolved = Resolved();
            var h = Make(new FakeEngine(resolved), linkHosts: new List<string> { "go.brand.com", "https://Short.Brand.com/" });
            await h.Strait.Start(null);
            await h.Strait.HandleUrl("https://go.brand.com/x");
            await h.Strait.HandleUrl("https://short.brand.com/y");
            Assert.Equal(new[] { "links.test", "go.brand.com", "short.brand.com" }, h.Strait.LinkHosts.ToArray());
            Assert.Equal(2, h.Engine.Calls.Count(c => c.Path == "/v1/resolve"));
        }

        // ------------------------------------------------------------ old Firebase page.link links (B22)

        [Fact]
        public async Task PageLinkShortLink_IsResolvedByTheEngine_WithItsHost()
        {
            var h = Make(new FakeEngine(Routes(("/v1/resolve",
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/7\",\"linkId\":\"lnk_7\",\"slug\":\"aBcD\",\"recorded\":true}"))),
                linkHosts: new List<string> { "acme.page.link" });
            await h.Strait.Start("https://acme.page.link/aBcD");
            var call = h.Engine.Calls.Single(c => c.Path == "/v1/resolve");
            Assert.Equal("https://acme.page.link/aBcD", S(call.Body, "url"));
            var e = h.Events[0];
            Assert.Equal("app_link", e.Route);
            Assert.True(e.Matched);
            Assert.Equal("https://shop.example/p/7", e.Url);
            Assert.Equal("lnk_7", e.LinkId);
        }

        [Fact]
        public async Task PageLinkLongLink_OpensItsLinkDestination_NoResolve()
        {
            var h = Make(new FakeEngine(Routes(("/v1/open", "{\"ok\":true}"))), linkHosts: new List<string> { "acme.page.link" });
            await h.Strait.Start("https://acme.page.link/?link=https%3A%2F%2Fshop.example%2Fp%2F42%3Fcolor%3Dred&apn=com.acme.app");
            var e = h.Events[0];
            Assert.Equal("app_link", e.Route);
            Assert.True(e.Matched);
            Assert.Equal("https://shop.example/p/42?color=red", e.Url);
            Assert.Equal("/p/42", e.Path);
            Assert.Equal("red", e.Params!["color"]);
            Assert.DoesNotContain(h.Engine.Calls, c => c.Path == "/v1/resolve");
        }

        [Fact]
        public async Task EndpointTrailingSlashes_AreStripped()
        {
            var engine = new FakeEngine(Resolved());
            var strait = new StraitClient(new StraitConfig { PublishableKey = PK, Endpoint = "https://links.test//", Transport = engine, Platform = "ios" });
            await strait.Start("https://links.test/sale");
            Assert.Equal("https://links.test/v1/resolve", engine.Calls[0].Url);
        }

        [Fact]
        public async Task Stop_IgnoresLaterUrls_KeepsPastEvents()
        {
            var h = Make(new FakeEngine());
            await h.Strait.Start("straitlink://shop.example/cart");
            h.Strait.Stop();
            Assert.Null(await h.Strait.HandleUrl("straitlink://shop.example/other"));
            Assert.Single(h.Strait.Events);
        }

        [Fact]
        public async Task Start_IsIdempotent()
        {
            var h = Make(new FakeEngine());
            await h.Strait.Start("straitlink://shop.example/cart");
            await h.Strait.Start("straitlink://shop.example/cart");
            Assert.Single(h.Events);
        }

        // ------------------------------------------------------------ deferred links

        [Fact]
        public async Task FirstLaunch_PlayInstallReferrer_GivesTheLinkTappedBeforeInstalling()
        {
            var h = Make(new FakeEngine(ReferrerHit()), referrer: "utm_source=google-play&strait_link=lnk_7");
            await h.Strait.Start(null);
            var e = h.Events[0];
            Assert.Equal("deferred", e.Kind);
            Assert.Equal("install_referrer", e.Route);
            Assert.Equal("closed", e.AppState);
            Assert.True(e.Matched);
            Assert.Equal("https://shop.example/promo/DIWALI20", e.Url);
            Assert.Equal("/promo/DIWALI20", e.Path);
            Assert.Equal("lnk_7", e.LinkId);
            var call = h.Engine.Calls.Single(c => c.Path == "/v1/referrer");
            Assert.Equal(PK, S(call.Body, "publishableKey"));
            Assert.Equal("lnk_7", S(call.Body, "linkId"));
            Assert.Equal("android", S(call.Body, "platform"));
            Assert.DoesNotContain(h.Engine.Calls, c => c.Path == "/v1/match");
        }

        [Fact]
        public async Task Deferred_RunsOnlyOncePerInstall()
        {
            var storage = new MemoryKeyValueStore();
            await Make(new FakeEngine(ReferrerHit()), referrer: "strait_link=lnk_7", storage: storage).Strait.Start(null);
            var second = Make(new FakeEngine(ReferrerHit()), referrer: "strait_link=lnk_7", storage: storage);
            await second.Strait.Start(null);
            Assert.DoesNotContain(second.Events, e => e.Kind == "deferred");
            Assert.Empty(second.Engine.Calls);
        }

        [Fact]
        public async Task NoReferrerLink_FingerprintMatch_ReportedNotMatched()
        {
            var h = Make(new FakeEngine(Routes(("/v1/match", "{\"matched\":false,\"matchMethod\":\"none\"}"))),
                referrer: "utm_source=google-play&utm_medium=organic");
            await h.Strait.Start(null);
            var e = h.Events[0];
            Assert.Equal("deferred", e.Kind);
            Assert.Equal("fingerprint", e.Route);
            Assert.False(e.Matched);
            Assert.Equal("no_match", e.Reason);
            var call = h.Engine.Calls.Single(c => c.Path == "/v1/match");
            Assert.Equal(PK, S(call.Body, "publishableKey"));
            Assert.Equal("android", S(call.Body, "platform"));
            AssertDevice(call.Body!.Value);
            Assert.DoesNotContain(h.Engine.Calls, c => c.Path == "/v1/referrer");
        }

        [Fact]
        public async Task ReferrerMiss_FallsBackToMatch()
        {
            var engine = new FakeEngine(Routes(
                ("/v1/referrer", "{\"matched\":false}"),
                ("/v1/match", "{\"matched\":true,\"longUrl\":\"https://shop.example/y?a=1\",\"linkId\":\"lnk_9\"}")));
            var h = Make(engine, referrer: "strait_link=lnk_7");
            await h.Strait.Start(null);
            Assert.Equal(new[] { "/v1/referrer", "/v1/match" }, engine.Calls.Select(c => c.Path).ToArray());
            var e = h.Events[0];
            Assert.Equal("fingerprint", e.Route);
            Assert.True(e.Matched);
            Assert.Null(e.Reason);
            Assert.Equal("/y", e.Path);
            Assert.Equal("1", e.Params!["a"]);
            Assert.Equal("lnk_9", e.LinkId);
        }

        [Fact]
        public async Task Ios_UsesMatchWithDeviceFields_NeverReadsReferrer()
        {
            var engine = new FakeEngine(Routes(("/v1/match", "{\"matched\":true,\"longUrl\":\"https://shop.example/z\"}")));
            bool referrerRead = false;
            var strait = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Platform = "ios", Transport = engine, DeviceFields = Device,
                InstallReferrer = () => { referrerRead = true; return Task.FromResult<string?>("strait_link=lnk_7"); },
            });
            var ev = await strait.CheckDeferred();
            Assert.False(referrerRead);
            Assert.Equal("/v1/match", engine.Calls.Single().Path);
            Assert.Equal("ios", S(engine.Calls[0].Body, "platform"));
            AssertDevice(engine.Calls[0].Body!.Value);
            Assert.True(ev.Matched);
            Assert.Equal("fingerprint", ev.Route);
            Assert.Equal("/z", ev.Path);
        }

        [Fact]
        public async Task ThrowingReferrerProvider_FallsBackToMatch()
        {
            var engine = new FakeEngine(Routes(("/v1/match", "{\"matched\":false}")));
            var strait = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Platform = "android", Transport = engine, DeviceFields = Device,
                InstallReferrer = () => throw new InvalidOperationException("no play services"),
            });
            var ev = await strait.CheckDeferred();
            Assert.Equal("/v1/match", engine.Calls.Single().Path);
            Assert.Equal("no_match", ev.Reason);
        }

        [Fact]
        public async Task NetworkFailure_Deferred_IsMatchedFalseReasonNetwork()
        {
            var h = Make(new FakeEngine(ReferrerHit()) { Offline = true }, referrer: "strait_link=lnk_7");
            await h.Strait.Start(null);
            var e = h.Events[0];
            Assert.Equal("deferred", e.Kind);
            Assert.Equal("fingerprint", e.Route);
            Assert.False(e.Matched);
            Assert.Equal("network", e.Reason);
            Assert.Null(await h.Storage.GetItemAsync(StraitClient.DeferredFlag)); // B6: retried next launch
        }

        [Fact]
        public async Task FirstLaunchOpenedByLink_SkipsDeferred_ButMarksItDone()
        {
            var h = Make(new FakeEngine(ReferrerHit()), referrer: "strait_link=lnk_7");
            await h.Strait.Start("straitlink://shop.example/cart");
            Assert.Equal(new[] { "direct" }, h.Events.Select(e => e.Kind).ToArray());
            Assert.Equal("1", await h.Storage.GetItemAsync("strait.deferredChecked"));
        }

        [Fact]
        public async Task EmptyInitialUrl_TreatedAsNoLaunchLink()
        {
            var h = Make(new FakeEngine(ReferrerHit()), referrer: "strait_link=lnk_7");
            await h.Strait.Start(""); // Unity's Application.absoluteURL is "" when not opened by a link
            Assert.Equal(new[] { "deferred" }, h.Events.Select(e => e.Kind).ToArray());
        }

        [Fact]
        public async Task CheckDeferred_DoesNotTouchTheFlag()
        {
            var h = Make(new FakeEngine(Routes(("/v1/match", "{\"matched\":false}"))));
            await h.Strait.CheckDeferred();
            Assert.Null(await h.Storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task ReferrerWithQuotes_CannotInjectJsonFields()
        {
            var engine = new FakeEngine(ReferrerHit());
            var h = Make(engine, referrer: "strait_link=" + Uri.EscapeDataString("x\",\"publishableKey\":\"evil"));
            await h.Strait.Start(null);
            var body = engine.Calls.Single(c => c.Path == "/v1/referrer").Body!.Value;
            Assert.Equal("x\",\"publishableKey\":\"evil", body.GetProperty("linkId").GetString());
            Assert.Equal(PK, body.GetProperty("publishableKey").GetString());
        }

        // ------------------------------------------------------------ fingerprint + events

        [Fact]
        public async Task Fingerprint_ReportsAppSide_AndReadsComparison()
        {
            var engine = new FakeEngine(Routes(("/v1/debug/fingerprint", "{\"extHash\":\"abc\",\"coreHash\":\"def\",\"inputs\":{}}")));
            var h = Make(engine);
            var reported = await h.Strait.ReportFingerprint();
            var post = engine.Calls.Single(c => c.Method == "POST" && c.Path == "/v1/debug/fingerprint");
            Assert.Equal(PK, S(post.Body, "publishableKey"));
            Assert.Equal("app", S(post.Body, "origin"));
            AssertDevice(post.Body!.Value);
            Assert.Equal("abc", reported!["extHash"]);

            var cmp = await h.Strait.CompareFingerprint();
            var get = engine.Calls.Single(c => c.Method == "GET");
            Assert.Equal("/v1/debug/fingerprint", get.Path);
            Assert.Equal("https://links.test/v1/debug/fingerprint?publishableKey=" + PK, get.Url);
            Assert.Null(get.Body);
            Assert.Equal("def", cmp!["coreHash"]);
        }

        [Fact]
        public async Task Fingerprint_NetworkFailure_ReturnsNull()
        {
            var h = Make(new FakeEngine { Offline = true });
            Assert.Null(await h.Strait.ReportFingerprint());
            Assert.Null(await h.Strait.CompareFingerprint());
        }

        [Fact]
        public async Task TrackEvent_SendsPublishableKey()
        {
            var engine = new FakeEngine(Routes(("/v1/event", "{\"ok\":true}")));
            var h = Make(engine);
            Assert.True(await h.Strait.TrackEvent("purchase", 49.99, "USD", "lnk_42"));
            var body = engine.Calls.Last().Body!.Value;
            Assert.Equal(PK, body.GetProperty("publishableKey").GetString());
            Assert.Equal("purchase", body.GetProperty("event").GetString());
            Assert.Equal("android", body.GetProperty("platform").GetString());
            Assert.Equal(49.99, body.GetProperty("value").GetDouble());
            Assert.Equal("49.99", body.GetProperty("value").GetRawText());
            Assert.Equal("USD", body.GetProperty("currency").GetString());
            Assert.Equal("lnk_42", body.GetProperty("linkId").GetString());
        }

        [Fact]
        public async Task TrackEvent_OmitsUnsetFields_AndIsFalseOnFailure()
        {
            var engine = new FakeEngine();
            var h = Make(engine);
            Assert.False(await h.Strait.TrackEvent("signup")); // 404
            var body = engine.Calls.Last().Body!.Value;
            Assert.False(body.TryGetProperty("value", out _));
            Assert.False(body.TryGetProperty("currency", out _));
            Assert.False(body.TryGetProperty("linkId", out _));
            engine.Offline = true;
            Assert.False(await h.Strait.TrackEvent("signup"));
        }

        // ------------------------------------------------ conversion events carry the tap id (B15)

        private const string Tap = "3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f";
        private const string OtherTap = "11111111-2222-4333-8444-555555555555";
        private const long Day = 24L * 60 * 60 * 1000;
        private static string HandOff => "straitlink://shop.example/p/42?strait_click=" + Tap;

        private static JsonElement LastEvent(FakeEngine e) => e.Calls.Last(c => c.Path == "/v1/event").Body!.Value;
        private static Dictionary<string, string> EventRoutes(params (string, string)[] extra) =>
            Routes(new[] { ("/v1/event", "{\"ok\":true}"), ("/v1/open", "{\"ok\":true}") }.Concat(extra).ToArray());

        [Fact]
        public async Task B15_HandOffTap_IsRememberedAndAttached()
        {
            var engine = new FakeEngine(EventRoutes());
            var h = Make(engine);
            await h.Strait.Start(HandOff);
            h.Clock.T += Day;
            Assert.True(await h.Strait.TrackEvent("purchase", 5, "USD"));
            Assert.Equal(Tap, LastEvent(engine).GetProperty("clickId").GetString());
            var stored = JsonDocument.Parse((await h.Storage.GetItemAsync(StraitClient.TapKey))!).RootElement;
            Assert.Equal(Tap, stored.GetProperty("clickId").GetString());
            Assert.Equal(1_000_000, stored.GetProperty("at").GetInt64());
        }

        [Fact]
        public async Task B15_NotAfter7Days()
        {
            var engine = new FakeEngine(EventRoutes());
            var h = Make(engine);
            await h.Strait.Start(HandOff);
            h.Clock.T += 7 * Day + 1;
            await h.Strait.TrackEvent("purchase");
            Assert.False(LastEvent(engine).TryGetProperty("clickId", out _));
        }

        [Fact]
        public async Task B15_ExplicitClickId_Overrides()
        {
            var engine = new FakeEngine(EventRoutes());
            var h = Make(engine);
            await h.Strait.Start(HandOff);
            await h.Strait.TrackEvent("purchase", clickId: OtherTap);
            Assert.Equal(OtherTap, LastEvent(engine).GetProperty("clickId").GetString());
        }

        [Fact]
        public async Task B15_NoRememberedTap_NoClickId()
        {
            var engine = new FakeEngine(EventRoutes(("/v1/match", "{\"matched\":false}")));
            var h = Make(engine);
            await h.Strait.Start(null);
            await h.Strait.TrackEvent("signup");
            Assert.False(LastEvent(engine).TryGetProperty("clickId", out _));
        }

        [Fact]
        public async Task B15_PlayReferrerTap_IsRememberedOnDeferredInstall()
        {
            var engine = new FakeEngine(EventRoutes(("/v1/referrer",
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/7\",\"linkId\":\"lnk_7\"}")));
            var h = Make(engine, referrer: "strait_link=lnk_7&strait_click=" + Tap);
            await h.Strait.Start(null);
            await h.Strait.TrackEvent("purchase");
            Assert.Equal(Tap, LastEvent(engine).GetProperty("clickId").GetString());
        }

        [Fact]
        public async Task B15_NewerShortLinkOpen_NoReplyTapId_ForgetsOlderTap()
        {
            var engine = new FakeEngine(EventRoutes(("/v1/resolve",
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\"}")));
            var h = Make(engine);
            await h.Strait.Start(HandOff);
            await h.Strait.HandleUrl("https://links.test/sale");
            await h.Strait.TrackEvent("purchase");
            Assert.False(LastEvent(engine).TryGetProperty("clickId", out _));
        }

        [Fact]
        public async Task B16_ShortLinkOpen_RemembersReplyTapId()
        {
            var engine = new FakeEngine(EventRoutes(("/v1/resolve",
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\",\"recorded\":true,\"clickId\":\"" + Tap.ToUpperInvariant() + "\"}")));
            var h = Make(engine);
            await h.Strait.Start("straitlink://shop.example/p/42?strait_click=" + OtherTap);
            h.Clock.T += 1000;
            await h.Strait.HandleUrl("https://links.test/sale");
            await h.Strait.TrackEvent("purchase");
            Assert.Equal(Tap, LastEvent(engine).GetProperty("clickId").GetString());
            var stored = JsonDocument.Parse((await h.Storage.GetItemAsync(StraitClient.TapKey))!).RootElement;
            Assert.Equal(1_001_000, stored.GetProperty("at").GetInt64());
        }

        [Fact]
        public async Task B16_FingerprintMatch_RemembersReplyTapId()
        {
            var engine = new FakeEngine(EventRoutes(("/v1/match",
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/9\",\"linkId\":\"lnk_9\",\"clickId\":\"" + Tap + "\"}")));
            var h = Make(engine);
            await h.Strait.Start(null);
            await h.Strait.TrackEvent("purchase");
            Assert.Equal(Tap, LastEvent(engine).GetProperty("clickId").GetString());
        }

        [Fact]
        public async Task B16_ReferrerReplyTapId_WinsOverParsed()
        {
            var engine = new FakeEngine(EventRoutes(("/v1/referrer",
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/7\",\"linkId\":\"lnk_7\",\"clickId\":\"" + Tap + "\"}")));
            var h = Make(engine, referrer: "strait_link=lnk_7&strait_click=" + OtherTap);
            await h.Strait.Start(null);
            await h.Strait.TrackEvent("purchase");
            Assert.Equal(Tap, LastEvent(engine).GetProperty("clickId").GetString());
        }

        [Fact]
        public async Task B16_MalformedReplyTapId_Forgets()
        {
            var engine = new FakeEngine(EventRoutes(("/v1/resolve",
                "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\",\"clickId\":\"nope\"}")));
            var h = Make(engine);
            await h.Strait.Start(HandOff);
            await h.Strait.HandleUrl("https://links.test/sale");
            await h.Strait.TrackEvent("purchase");
            Assert.False(LastEvent(engine).TryGetProperty("clickId", out _));
        }

        private sealed class FailingStore : IKeyValueStore
        {
            public Task<string?> GetItemAsync(string key) => throw new InvalidOperationException("io");
            public Task SetItemAsync(string key, string value) => throw new InvalidOperationException("io");
        }

        [Fact]
        public async Task B15_UnreadableStorage_NeverBlocksTheEvent()
        {
            var engine = new FakeEngine(EventRoutes());
            var strait = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Storage = new FailingStore(), Platform = "android",
                DeviceFields = Device, Transport = engine, Clock = () => 1_000_000,
            });
            await strait.HandleUrl("straitlink://x.example/?strait_click=" + Tap);
            Assert.True(await strait.TrackEvent("purchase"));
            Assert.False(LastEvent(engine).TryGetProperty("clickId", out _));
        }
    }
}
