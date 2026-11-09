using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>
    /// Contract B25 (shared-spec ADR 0007): every call that records an app open or install (/v1/resolve, /v1/open,
    /// /v1/referrer, /v1/match, /v1/handoff/claim) names this SDK, <c>sdkVersion = "unity/&lt;version&gt;"</c>, so the
    /// dashboard can tell a Unity app's SDK version (it sent none before 0.9.3). /v1/event and /v1/lookup carry none.
    /// </summary>
    public class SdkVersionTests
    {
        private const string PK = "st_pub_test_appowner01";
        private const string Endpoint = "https://links.test";
        private static readonly string Wire = "unity/" + PackageVersion();

        private static string PackageVersion()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "package.json"))) dir = dir.Parent;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "package.json")));
            return doc.RootElement.GetProperty("version").GetString()!;
        }

        private sealed class Engine : IStraitTransport
        {
            private readonly Dictionary<string, string> _routes;
            private readonly object _lock = new object();
            private readonly List<(string Path, JsonElement? Body)> _calls = new List<(string, JsonElement?)>();
            public Engine(params (string path, string json)[] routes) { _routes = routes.ToDictionary(r => r.path, r => r.json); }
            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                lock (_lock) _calls.Add((path, jsonBody == null ? (JsonElement?)null : JsonDocument.Parse(jsonBody).RootElement.Clone()));
                return Task.FromResult(_routes.TryGetValue(path, out var b) ? (200, b) : (202, "{}"));
            }
            public List<JsonElement> Of(string path) { lock (_lock) return _calls.Where(c => c.Path == path).Select(c => c.Body!.Value).ToList(); }
        }

        private static StraitClient Make(Engine e, string platform, string? referrer = null) => new StraitClient(new StraitConfig
        {
            PublishableKey = PK, Endpoint = Endpoint, Platform = platform, Storage = new MemoryKeyValueStore(), Transport = e,
            InstallReferrer = () => System.Threading.Tasks.Task.FromResult(referrer),
            DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en-IN", Timezone = "Asia/Kolkata" },
            Clock = () => 1_000_000,
        });

        private static async Task Settle() { for (int i = 0; i < 20; i++) await Task.Delay(1); }

        private static void Named(JsonElement body) => Assert.Equal(Wire, body.GetProperty("sdkVersion").GetString());

        [Fact]
        public void SdkVersionIsThePackageVersion() => Assert.Equal(PackageVersion(), StraitClient.SdkVersion);

        [Fact]
        public async Task FirstLaunch_Referrer_And_Match_NameTheSdk()
        {
            var android = new Engine(("/v1/referrer", "{\"matched\":true,\"longUrl\":\"https://shop.example/p\",\"linkId\":\"lnk_7\"}"));
            await Make(android, "android", "strait_link=lnk_7&strait_click=3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f").Start(null);
            Named(Assert.Single(android.Of("/v1/referrer")));

            var ios = new Engine(("/v1/match", "{\"matched\":false,\"reasons\":[\"no_candidate\"]}"));
            await Make(ios, "ios").Start(null);
            Named(Assert.Single(ios.Of("/v1/match")));
        }

        [Fact]
        public async Task ShortLink_ResolveAndItsOpenReport_NameTheSdk()
        {
            var e = new Engine(("/v1/resolve", "{\"matched\":true,\"longUrl\":\"https://shop.example/p\",\"linkId\":\"lnk_1\",\"recorded\":false}"));
            var s = Make(e, "android");
            await s.Start("https://links.test/sale");
            await s.FlushOpenReports();
            await Settle();
            Named(Assert.Single(e.Of("/v1/resolve")));
            Assert.NotEmpty(e.Of("/v1/open"));
            foreach (var b in e.Of("/v1/open")) Named(b);
        }

        [Fact]
        public async Task HandoffClaim_NamesTheSdk_EventDoesNot()
        {
            var e = new Engine(("/v1/handoff/claim", "{\"matched\":true,\"longUrl\":\"https://shop.example/p\",\"linkId\":\"lnk_1\"}"));
            var s = Make(e, "ios");
            await s.ClaimHandoff("https://links.test/h/AbCdEfGhIjKlMnOpQrStUv");
            Named(Assert.Single(e.Of("/v1/handoff/claim")));
            Assert.True(await s.TrackEvent("purchase", 10, "INR"));
            Assert.False(Assert.Single(e.Of("/v1/event")).TryGetProperty("sdkVersion", out _));
        }
    }
}
