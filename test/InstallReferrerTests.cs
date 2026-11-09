using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>
    /// Contract B26 (engine ADR 0106, shared-spec ADR 0008): the once-per-install check sends the raw Play install
    /// referrer as <c>installReferrer</c> on the calls that record the install (/v1/referrer and its /v1/match
    /// fallback), so the install keeps its utm_* campaign even when the tap never went through a Strait link.
    /// 1..4096 characters, else left out; never on the record:false re-check, never off Android, nothing while off.
    /// </summary>
    public class InstallReferrerTests
    {
        private const string Ads = "utm_source=google&utm_medium=cpc&utm_campaign=strait-qa&gclid=Cj0KCQ";
        private const string StraitRef = "strait_link=lnk_1&strait_click=3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f&utm_source=google-play";

        private sealed class Engine : IStraitTransport
        {
            private readonly Dictionary<string, string> _routes;
            private readonly object _lock = new object();
            private readonly List<(string Path, JsonElement Body)> _calls = new List<(string, JsonElement)>();
            public Engine(params (string path, string json)[] routes) { _routes = routes.ToDictionary(r => r.path, r => r.json); }
            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                lock (_lock) _calls.Add((path, JsonDocument.Parse(jsonBody ?? "{}").RootElement.Clone()));
                return Task.FromResult(_routes.TryGetValue(path, out var b) ? (200, b) : (200, "{\"matched\":false}"));
            }
            public List<string> Paths { get { lock (_lock) return _calls.Select(c => c.Path).ToList(); } }
            public JsonElement Of(string path) { lock (_lock) return _calls.Single(c => c.Path == path).Body; }
        }

        private static StraitClient Make(Engine e, string? referrer, string platform = "android", IKeyValueStore? storage = null) => new StraitClient(new StraitConfig
        {
            PublishableKey = "st_pub_test_x", Endpoint = "https://links.test", Platform = platform,
            Storage = storage ?? new MemoryKeyValueStore(), Transport = e,
            InstallReferrer = () => Task.FromResult(referrer),
            DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en-IN", Timezone = "Asia/Kolkata" },
            Clock = () => 1_000_000,
        });

        private static string? Referrer(JsonElement body) => body.TryGetProperty("installReferrer", out var v) ? v.GetString() : null;

        [Fact]
        public void FieldRule()
        {
            Assert.Equal(4096, StraitCore.InstallReferrerMax);
            Assert.Equal(Ads, StraitCore.InstallReferrerField(Ads));
            Assert.Equal(new string('a', 4096), StraitCore.InstallReferrerField(new string('a', 4096)));
            Assert.Null(StraitCore.InstallReferrerField(new string('a', 4097)));
            Assert.Null(StraitCore.InstallReferrerField(""));
            Assert.Null(StraitCore.InstallReferrerField(null));
        }

        [Fact]
        public async Task GoogleAdsInstall_MatchCarriesTheRawReferrer()
        {
            var e = new Engine();
            await Make(e, Ads).Start(null);
            Assert.Equal(new[] { "/v1/match" }, e.Paths);
            Assert.Equal(Ads, Referrer(e.Of("/v1/match")));
            Assert.True(e.Of("/v1/match").TryGetProperty("openId", out _));
        }

        [Fact]
        public async Task StraitLinkInstall_ReferrerCarriesIt_AndTheMatchFallbackToo()
        {
            var m = new Engine(("/v1/referrer", "{\"matched\":true,\"longUrl\":\"https://shop.example/p/1\",\"linkId\":\"lnk_1\"}"));
            await Make(m, StraitRef).Start(null);
            Assert.Equal(StraitRef, Referrer(m.Of("/v1/referrer")));
            var u = new Engine();
            await Make(u, StraitRef).Start(null);
            Assert.Equal(new[] { "/v1/referrer", "/v1/match" }, u.Paths);
            Assert.Equal(StraitRef, Referrer(u.Of("/v1/match")));
        }

        [Theory]
        [InlineData(4097)]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task TooLongEmptyOrNone_NotSent(int length)
        {
            var r = length < 0 ? null : length == 0 ? "" : Ads + "&x=" + new string('a', length);
            var e = new Engine();
            await Make(e, r).Start(null);
            Assert.False(e.Of("/v1/match").TryGetProperty("installReferrer", out _));
        }

        [Fact]
        public async Task Iphone_NeverSent()
        {
            var e = new Engine();
            await Make(e, Ads, "ios").Start(null);
            Assert.False(e.Of("/v1/match").TryGetProperty("installReferrer", out _));
        }

        [Fact]
        public async Task CheckDeferred_NotSent()
        {
            var e = new Engine();
            var s = new MemoryKeyValueStore();
            await s.SetItemAsync("strait.deferredChecked", "1");
            var c = Make(e, Ads, storage: s);
            await c.Start(null);
            await c.CheckDeferred();
            Assert.False(e.Of("/v1/match").GetProperty("record").GetBoolean());
            Assert.False(e.Of("/v1/match").TryGetProperty("installReferrer", out _));
        }

        [Fact]
        public async Task OptedOut_SendsNothing()
        {
            var e = new Engine();
            var c = Make(e, Ads);
            await c.SetEnabled(false);
            await c.Start(null);
            Assert.Empty(e.Paths);
        }
    }
}
