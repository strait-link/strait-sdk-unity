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
    /// <summary>Contract B14 deviceClass (engine ADR 0039): sent with /v1/resolve and /v1/open.</summary>
    public class DeviceClassTests
    {
        private sealed class Engine : IStraitTransport
        {
            public readonly Dictionary<string, string?> Routes = new Dictionary<string, string?>(); // null body = offline
            public readonly List<(string Path, JsonElement Body)> Calls = new List<(string, JsonElement)>();
            public List<JsonElement> Of(string path) { lock (Calls) return Calls.Where(c => c.Path == path).Select(c => c.Body).ToList(); }
            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                lock (Calls) Calls.Add((path, JsonDocument.Parse(jsonBody ?? "{}").RootElement.Clone()));
                if (!Routes.TryGetValue(path, out var body)) return Task.FromResult((404, "{}"));
                if (body == null) throw new HttpRequestException("offline");
                return Task.FromResult((path == "/v1/open" ? 202 : 200, body));
            }
        }

        private const string Resolved = "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\",\"recorded\":true}";

        private static (StraitClient Client, MemoryKeyValueStore Storage) Make(Engine e, Func<string?>? deviceClass)
        {
            var storage = new MemoryKeyValueStore();
            storage.SetItemAsync(StraitClient.DeferredFlag, "1").Wait(); // not the first launch
            var c = new StraitClient(new StraitConfig
            {
                PublishableKey = "st_pub_test_appowner01",
                Endpoint = "https://links.test",
                Storage = storage,
                Platform = "android",
                DeviceFields = () => new DeviceFields { ScreenWidth = 411, PixelRatio = 2.625, Language = "en", Timezone = "Asia/Kolkata" },
                Transport = e,
                Clock = () => 1_800_000_000_000,
                DeviceClass = deviceClass,
            });
            return (c, storage);
        }

        [Fact]
        public void ModelRule()
        {
            Assert.Equal("tablet", StraitCore.DeviceClassForModel("iPad13,4"));
            Assert.Equal("phone", StraitCore.DeviceClassForModel("iPhone15,2"));
            Assert.Equal("phone", StraitCore.DeviceClassForModel("iPod9,1"));
            Assert.Null(StraitCore.DeviceClassForModel("samsung SM-X710"));
            Assert.Null(StraitCore.DeviceClassForModel(null));
            Assert.Null(StraitCore.DeviceClassForSize(double.NaN));
        }

        [Fact]
        public async Task ResolveAndItsOfflineRetryCarryIt()
        {
            var e = new Engine();
            e.Routes["/v1/resolve"] = null;
            var (c, _) = Make(e, () => "tablet");
            await c.Start("https://links.test/sale");
            Assert.Equal("tablet", e.Of("/v1/resolve").Single().GetProperty("deviceClass").GetString());
            e.Routes["/v1/open"] = "{\"ok\":true}";
            await c.FlushOpenReports();
            var open = e.Of("/v1/open").Last();
            Assert.Equal("network", open.GetProperty("reason").GetString());
            Assert.Equal("tablet", open.GetProperty("deviceClass").GetString());
        }

        [Fact]
        public async Task DirectOpenCarriesIt_QueueNeverStoresIt()
        {
            var e = new Engine();
            e.Routes["/v1/open"] = null;
            var (c, storage) = Make(e, () => "phone");
            await c.Start("https://shop.example/p/9");
            for (int i = 0; i < 20 && e.Of("/v1/open").Count == 0; i++) await Task.Delay(5);
            Assert.Equal("phone", e.Of("/v1/open").First().GetProperty("deviceClass").GetString());
            string? queued = null;
            for (int i = 0; i < 40 && queued == null; i++) { queued = await storage.GetItemAsync(StraitClient.QueueKey); if (queued == null) await Task.Delay(5); }
            Assert.Contains("openId", queued);
            Assert.DoesNotContain("deviceClass", queued);
        }

        [Fact]
        public async Task NoneInvalidOrThrowing_LeftOut()
        {
            foreach (var dc in new Func<string?>?[] { null, () => null, () => "watch", () => throw new InvalidOperationException() })
            {
                var e = new Engine();
                e.Routes["/v1/resolve"] = Resolved;
                var (c, _) = Make(e, dc);
                var events = new List<LinkEvent>();
                c.OnLink += ev => events.Add(ev);
                await c.Start("https://links.test/sale");
                Assert.True(events.Last().Matched);
                Assert.False(e.Of("/v1/resolve").Single().TryGetProperty("deviceClass", out _));
            }
        }
    }
}
