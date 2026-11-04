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
    /// <summary>Fake engine: a status (or offline) per path, every call recorded. Unknown path → 404.</summary>
    internal sealed class RouteEngine : IStraitTransport
    {
        public const int Offline = -1;
        public readonly Dictionary<string, int> Routes = new Dictionary<string, int>();
        public readonly List<(string Path, JsonElement Body)> Calls = new List<(string, JsonElement)>();

        public List<JsonElement> Of(string path) { lock (Calls) return Calls.Where(c => c.Path == path).Select(c => c.Body).ToList(); }

        public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
        {
            var path = new Uri(url).AbsolutePath;
            lock (Calls) Calls.Add((path, JsonDocument.Parse(jsonBody ?? "{}").RootElement.Clone()));
            int status = Routes.TryGetValue(path, out var s) ? s : 404;
            if (status == Offline) throw new HttpRequestException("Network request failed");
            return Task.FromResult((status, path == "/v1/match" ? "{\"matched\":false}" : "{\"ok\":true}"));
        }

        public static async Task Settle()
        {
            for (int i = 0; i < 20; i++) await Task.Delay(1);
        }
    }

    /// <summary>
    /// Port of sdk-react-native/test/events-v2.test.ts. Contract B13a (shared-spec ADR 0004): TrackEvent sends an
    /// eventId (the app's order id, else one the SDK makes) and keeps events that don't get through in the
    /// open-report queue.
    /// </summary>
    public class EventsV2Tests
    {
        private const string PK = "st_pub_test_x";
        private const string EventIdPattern = "^e_[a-z0-9]+_[a-z0-9]{12}$";
        private const long T = 1_800_000_000_000;

        private static async Task<(StraitClient Strait, RouteEngine Engine, MemoryKeyValueStore Store)> Make()
        {
            var e = new RouteEngine();
            e.Routes["/v1/event"] = 202;
            e.Routes["/v1/open"] = 202;
            var s = new MemoryKeyValueStore();
            await s.SetItemAsync(StraitClient.DeferredFlag, "1");
            var strait = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = "https://links.test", Storage = s, Platform = "android", Transport = e, Clock = () => T,
            });
            await strait.Start(null);
            await RouteEngine.Settle();
            return (strait, e, s);
        }

        private static async Task<List<JsonElement>> Queue(MemoryKeyValueStore s) =>
            JsonDocument.Parse(await s.GetItemAsync(StraitClient.QueueKey) ?? "[]").RootElement.EnumerateArray().ToList();

        private static Dictionary<string, string> Fields(JsonElement o) =>
            o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());

        [Fact]
        public void NewEventId_HasTheEngineEventIdShapeAndIsUnique()
        {
            var a = StraitCore.NewEventId(T);
            Assert.Matches(EventIdPattern, a);
            Assert.Matches("^[A-Za-z0-9_.:-]{1,64}$", a);
            Assert.NotEqual(a, StraitCore.NewEventId(T));
            Assert.Equal("e_0_aaaaaaaaaaaa", StraitCore.NewEventId(0, () => 0));
        }

        [Fact]
        public async Task SendsTheAppsEventIdAsIs()
        {
            var (strait, e, _) = await Make();
            Assert.True(await strait.TrackEvent("purchase", 499, "INR", eventId: "ord_1001"));
            var body = e.Of("/v1/event")[0];
            Assert.Equal(PK, body.GetProperty("publishableKey").GetString());
            Assert.Equal("purchase", body.GetProperty("event").GetString());
            Assert.Equal(499, body.GetProperty("value").GetDouble());
            Assert.Equal("INR", body.GetProperty("currency").GetString());
            Assert.Equal("ord_1001", body.GetProperty("eventId").GetString());
            Assert.Equal("android", body.GetProperty("platform").GetString());
        }

        [Fact]
        public async Task MakesAnEventIdWhenTheAppGivesNone()
        {
            var (strait, e, _) = await Make();
            await strait.TrackEvent("signup");
            await strait.TrackEvent("signup2", eventId: "");
            Assert.Matches(EventIdPattern, e.Of("/v1/event")[0].GetProperty("eventId").GetString());
            Assert.Matches(EventIdPattern, e.Of("/v1/event")[1].GetProperty("eventId").GetString());
        }

        [Fact]
        public async Task Offline_ReturnsFalse_KeepsTheEventWithoutTheKey_AndSendsItWithTheSameEventIdLater()
        {
            var (strait, e, s) = await Make();
            e.Routes["/v1/event"] = RouteEngine.Offline;
            Assert.False(await strait.TrackEvent("purchase", 5, "USD", eventId: "ord_7"));
            await RouteEngine.Settle();
            var saved = Assert.Single(await Queue(s));
            Assert.Equal(new Dictionary<string, string>
            {
                ["type"] = "event", ["at"] = T.ToString(), ["event"] = "purchase", ["platform"] = "android",
                ["eventId"] = "ord_7", ["value"] = "5", ["currency"] = "USD",
            }, Fields(saved));
            Assert.DoesNotContain("st_pub_", await s.GetItemAsync(StraitClient.QueueKey));
            Assert.Equal(1, await strait.PendingOpenReports());

            e.Routes["/v1/event"] = 202;
            strait.OnAppState(AppLifecycle.Active);
            await RouteEngine.Settle();
            Assert.Equal(new Dictionary<string, string>
            {
                ["publishableKey"] = PK, ["event"] = "purchase", ["platform"] = "android",
                ["eventId"] = "ord_7", ["value"] = "5", ["currency"] = "USD",
            }, Fields(e.Of("/v1/event").Last()));
            Assert.Empty(e.Of("/v1/open")); // never sent as an open report
            Assert.Empty(await Queue(s));
        }

        [Fact]
        public async Task ServerErrorsAnd429AreKept_AnotherClientErrorIsNot()
        {
            var (strait, e, s) = await Make();
            e.Routes["/v1/event"] = 400;
            Assert.False(await strait.TrackEvent("c", eventId: "bad id!"));
            await RouteEngine.Settle();
            e.Routes["/v1/event"] = 503;
            await strait.TrackEvent("a");
            e.Routes["/v1/event"] = 429;
            await strait.TrackEvent("b");
            await RouteEngine.Settle();
            Assert.Equal(new[] { "a", "b" }, (await Queue(s)).Select(q => q.GetProperty("event").GetString()).ToArray());
        }

        [Fact]
        public async Task OpensAndEventsShareTheQueueInOrder_EachSentToItsEndpointThenDropped()
        {
            var (strait, e, s) = await Make();
            e.Routes["/v1/event"] = RouteEngine.Offline;
            await strait.TrackEvent("purchase", eventId: "ord_9");
            await RouteEngine.Settle();
            // An open report saved earlier by the B14 path sits in the same queue.
            var queued = (await s.GetItemAsync(StraitClient.QueueKey))!;
            await s.SetItemAsync(StraitClient.QueueKey, "[{\"openId\":\"o_1_aaaaaaaaaaaa\",\"kind\":\"direct\",\"route\":\"app_link\",\"appState\":\"closed\"," +
                "\"platform\":\"android\",\"matched\":true,\"firstLaunch\":false,\"at\":" + T + "}," + queued.Substring(1));
            e.Routes["/v1/event"] = 202;
            await strait.FlushOpenReports();
            Assert.Equal(new[] { "o_1_aaaaaaaaaaaa" }, e.Of("/v1/open").Select(b => b.GetProperty("openId").GetString()).ToArray());
            Assert.False(e.Of("/v1/open")[0].TryGetProperty("eventId", out _));
            Assert.Equal(2, e.Of("/v1/event").Count(b => b.GetProperty("eventId").GetString() == "ord_9")); // the failed try + one retry
            Assert.Empty(await Queue(s));
        }

        [Fact]
        public async Task AnAcceptedEventSendsWhatWasSavedEarlier()
        {
            var (strait, e, s) = await Make();
            e.Routes["/v1/event"] = 503;
            await strait.TrackEvent("first", eventId: "ord_1");
            await RouteEngine.Settle();
            e.Routes["/v1/event"] = 202;
            Assert.True(await strait.TrackEvent("second", eventId: "ord_2"));
            await RouteEngine.Settle();
            Assert.Equal(new[] { "ord_1", "ord_2", "ord_1" }, e.Of("/v1/event").Select(b => b.GetProperty("eventId").GetString()).ToArray());
            Assert.Empty(await Queue(s));
        }

        [Fact]
        public async Task TheTapIdIsChosenWhenTheAppCalls_NotAtRetryTime()
        {
            var (strait, e, s) = await Make();
            const string Tap = "3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f";
            await s.SetItemAsync(StraitClient.TapKey, StraitCore.RememberTap(Tap, T));
            e.Routes["/v1/event"] = RouteEngine.Offline;
            await strait.TrackEvent("purchase", eventId: "ord_10");
            await RouteEngine.Settle();
            await s.SetItemAsync(StraitClient.TapKey, "");
            e.Routes["/v1/event"] = 202;
            await strait.FlushOpenReports();
            Assert.Equal(Tap, e.Of("/v1/event").Last().GetProperty("clickId").GetString());
        }
    }
}
