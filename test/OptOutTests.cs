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
    /// Opt-out (F6, port of sdk-react-native/test/opt-out.test.ts): SetEnabled(false) = no reports, no events,
    /// no install matching, kept across launches; a link that carries its own destination still reaches OnLink;
    /// a Strait short link is looked up through /v1/lookup, which records nothing.
    /// </summary>
    public class OptOutTests
    {
        private const string PK = "st_pub_test_x";
        private const string Endpoint = "https://acme.strait.link";
        private const long T = 1_800_000_000_000;
        private const string Click = "3f2a9c1e-7b4d-4e8a-9f10-2c3d4e5f6a7b";
        private const string LookupHit = "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42?color=red\",\"linkId\":\"lnk_42\",\"slug\":\"sale\"}";

        /// <summary>Fake engine: records every call; the reply function answers (or throws = no network).</summary>
        private sealed class Engine : IStraitTransport
        {
            private readonly Func<string, Task<(int, string)>> _reply;
            public readonly List<string> Calls = new List<string>();
            public readonly List<JsonElement> Bodies = new List<JsonElement>();

            public Engine(Func<string, Task<(int, string)>>? reply = null)
            {
                _reply = reply ?? (_ => Task.FromResult((202, "{\"matched\":false}")));
            }

            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                lock (Calls)
                {
                    Calls.Add(path);
                    Bodies.Add(JsonDocument.Parse(jsonBody ?? "{}").RootElement.Clone());
                }
                return _reply(path);
            }
        }

        private static Engine Replying(Func<string, (int, string)> reply) => new Engine(p => Task.FromResult(reply(p)));

        private static StraitClient Make(Engine engine, IKeyValueStore storage, List<LinkEvent>? events = null, string platform = "android")
        {
            var s = new StraitClient(new StraitConfig
            {
                PublishableKey = PK,
                Endpoint = Endpoint,
                Storage = storage,
                Platform = platform,
                InstallReferrer = () => Task.FromResult<string?>(null),
                DeviceFields = () => new DeviceFields { ScreenWidth = 411, PixelRatio = 2.625, Language = "en", Timezone = "Asia/Kolkata" },
                Transport = engine,
                Clock = () => T,
            });
            if (events != null) s.OnLink += e => { lock (events) events.Add(e); };
            return s;
        }

        private static async Task Settle()
        {
            for (int i = 0; i < 20; i++) await Task.Delay(1);
        }

        private static async Task<string> Get(IKeyValueStore s, string key) => await s.GetItemAsync(key) ?? "";

        [Fact]
        public async Task DisabledSendsNothingAndLinksWithTheirOwnDestinationStillRoute()
        {
            var e = new Engine();
            var store = new MemoryKeyValueStore();
            await store.SetItemAsync(StraitClient.QueueKey,
                "[{\"openId\":\"o_1\",\"kind\":\"direct\",\"route\":\"app_link\",\"appState\":\"closed\",\"platform\":\"android\",\"matched\":true,\"firstLaunch\":false,\"at\":" + T + "}]");
            var events = new List<LinkEvent>();
            var s = Make(e, store, events);
            await s.SetEnabled(false);
            await s.Start(null);
            await s.HandleUrl("acme://product/42");
            Assert.False(await s.TrackEvent("purchase", 5));
            await s.FlushOpenReports();
            var deferred = await s.CheckDeferred();
            Assert.False(deferred.Matched);
            Assert.Equal("disabled", deferred.Reason);
            Assert.Null(await s.ReportFingerprint());
            Assert.Null(await s.CompareFingerprint());
            Assert.Equal("disabled", (await s.ClaimHandoff("https://acme.strait.link/h/AbCdEfGhIjKlMnOpQrStUv")).Reason);
            await Settle();
            Assert.Empty(e.Calls);
            Assert.Contains(events, x => x.Matched && x.RawUrl == "acme://product/42");
            Assert.Equal(0, await s.PendingOpenReports()); // reports saved before the opt-out are deleted
            Assert.Equal("", await Get(store, StraitClient.DeferredFlag)); // the check is still due
            Assert.Equal("", await Get(store, StraitClient.TapKey));
        }

        [Fact]
        public async Task DisabledShortLinkRoutesThroughLookupWithKeyAndHostPathOnly()
        {
            var e = Replying(p => p == "/v1/lookup" ? (200, LookupHit) : (202, "{}"));
            var store = new MemoryKeyValueStore();
            var events = new List<LinkEvent>();
            var s = Make(e, store, events);
            await s.SetEnabled(false);
            await s.Start(null);
            await s.HandleUrl("https://acme.strait.link/sale?utm_source=sms&secret=1");
            await s.FlushOpenReports();
            await Settle();
            Assert.Equal(new[] { "/v1/lookup" }, e.Calls);
            // Only the key and host + path leave the device: no query, openId, platform, device or app state.
            var body = e.Bodies[0];
            Assert.Equal(new[] { "publishableKey", "url" }, body.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
            Assert.Equal(PK, body.GetProperty("publishableKey").GetString());
            Assert.Equal("https://acme.strait.link/sale", body.GetProperty("url").GetString());
            var ev = Assert.Single(events);
            Assert.True(ev.Matched);
            Assert.Equal("/p/42", ev.Path);
            Assert.Equal("red", ev.Params!["color"]);
            Assert.Equal("lnk_42", ev.LinkId);
            Assert.Equal(0, await s.PendingOpenReports());
            Assert.Equal("", await Get(store, StraitClient.TapKey)); // no tap remembered
        }

        [Theory]
        [InlineData("404", "disabled")]
        [InlineData("offline", "disabled")]
        [InlineData("miss", "expired")]
        public async Task DisabledLookupFailureIsDisabledAndAMissKeepsItsReason(string reply, string reason)
        {
            var e = new Engine(_ => reply switch
            {
                "404" => Task.FromResult((404, "{\"error\":\"not found\"}")),
                "offline" => Task.FromException<(int, string)>(new HttpRequestException("Network request failed")),
                _ => Task.FromResult((200, "{\"matched\":false,\"reason\":\"expired\"}")),
            });
            var store = new MemoryKeyValueStore();
            var events = new List<LinkEvent>();
            var s = Make(e, store, events);
            await s.SetEnabled(false);
            await s.Start(null);
            await s.HandleUrl("https://acme.strait.link/sale");
            await Settle();
            var ev = Assert.Single(events);
            Assert.False(ev.Matched);
            Assert.Equal(reason, ev.Reason);
            Assert.Equal(0, await s.PendingOpenReports());
        }

        /// <summary>F4: the switch goes off while /v1/resolve is out.</summary>
        private static (StraitClient s, MemoryKeyValueStore store, List<string> calls, List<LinkEvent> events) InFlight(bool resolveAnswers)
        {
            StraitClient? client = null;
            var engine = new Engine(async p =>
            {
                if (p != "/v1/resolve") return (200, "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\"}");
                await client!.SetEnabled(false);
                if (!resolveAnswers) throw new HttpRequestException("offline");
                return (200, "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\",\"clickId\":\"" + Click + "\"}");
            });
            var store = new MemoryKeyValueStore();
            store.SetItemAsync(StraitClient.DeferredFlag, "1").Wait(); // not the first launch
            var events = new List<LinkEvent>();
            client = Make(engine, store, events);
            return (client, store, engine.Calls, events);
        }

        [Fact]
        public async Task SwitchedOffWhileResolveIsOutAndItFailsRoutesViaLookup()
        {
            var (s, store, calls, events) = InFlight(resolveAnswers: false);
            await s.Start(null);
            await Settle();
            calls.Clear();
            await s.HandleUrl("https://acme.strait.link/sale");
            await Settle();
            Assert.Equal(new[] { "/v1/resolve", "/v1/lookup" }, calls);
            var ev = events.Last();
            Assert.True(ev.Matched);
            Assert.Equal("/p/42", ev.Path);
            Assert.Null(ev.Reason);
            Assert.Equal(0, await s.PendingOpenReports());
            Assert.Equal("", await Get(store, StraitClient.TapKey));
        }

        [Fact]
        public async Task SwitchedOffWhileResolveIsOutAndItAnswersKeepsNoTap()
        {
            var (s, store, calls, events) = InFlight(resolveAnswers: true);
            await s.Start(null);
            await Settle();
            calls.Clear();
            await s.HandleUrl("https://acme.strait.link/sale");
            await Settle();
            Assert.True(events.Last().Matched);
            Assert.Equal(new[] { "/v1/resolve" }, calls);
            Assert.Equal(0, await s.PendingOpenReports());
            Assert.Equal("", await Get(store, StraitClient.TapKey));
        }

        [Fact]
        public async Task SwitchingOffDeletesTheRememberedTap()
        {
            var e = Replying(p => p == "/v1/resolve"
                ? (200, "{\"matched\":true,\"recorded\":true,\"longUrl\":\"https://shop.example/p/42\",\"clickId\":\"" + Click + "\"}")
                : (202, "{}"));
            var store = new MemoryKeyValueStore();
            await store.SetItemAsync(StraitClient.DeferredFlag, "1");
            var s = Make(e, store);
            await s.Start(null);
            await s.HandleUrl("https://acme.strait.link/sale");
            await Settle();
            Assert.Contains(Click, await Get(store, StraitClient.TapKey)); // remembered while on
            await s.SetEnabled(false);
            Assert.Equal("", await Get(store, StraitClient.TapKey));
        }

        private sealed class SpyClipboard : IStraitClipboard
        {
            public int Uses, Detects, Reads;
            /// <summary>Runs inside the prompt-free detect, before it answers.</summary>
            public Func<Task>? DuringDetect;
            public async Task<bool> HasProbableWebUrlAsync()
            {
                Uses++; Detects++;
                if (DuringDetect != null) await DuringDetect();
                return true;
            }
            public Task<string?> ReadTextAsync() { Uses++; Reads++; return Task.FromResult<string?>("https://acme.strait.link/h/AbCdEfGhIjKlMnOpQrStUv"); }
        }

        [Fact]
        public async Task SwitchedOffBetweenTheDetectAndTheReadDoesNotRead()
        {
            var engine = new Engine(_ => Task.FromResult((200, "{\"matched\":false,\"ios\":{\"pasteHandoff\":true}}")));
            var clip = new SpyClipboard();
            var client = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Storage = new MemoryKeyValueStore(), Platform = "ios",
                DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en", Timezone = "Asia/Kolkata" },
                Clipboard = clip, Transport = engine, Clock = () => T,
            });
            clip.DuringDetect = () => client.SetEnabled(false);
            await client.Start(null);
            await Settle();
            Assert.Equal(1, clip.Detects);
            Assert.Equal(0, clip.Reads); // no paste prompt for an opted-out person
            Assert.Equal(new[] { "/v1/match" }, engine.Calls);
        }

        [Fact]
        public async Task SwitchedOffDuringTheFirstLaunchCheckLeavesTheClipboardAlone()
        {
            StraitClient? client = null;
            var engine = new Engine(async p =>
            {
                await client!.SetEnabled(false);
                return (200, "{\"matched\":false,\"ios\":{\"pasteHandoff\":true}}");
            });
            var clip = new SpyClipboard();
            client = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Storage = new MemoryKeyValueStore(), Platform = "ios",
                DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en", Timezone = "Asia/Kolkata" },
                Clipboard = clip, Transport = engine, Clock = () => T,
            });
            await client.Start(null);
            await Settle();
            Assert.Equal(0, clip.Uses); // no paste prompt for an opted-out person
            Assert.Equal(new[] { "/v1/match" }, engine.Calls);
        }

        [Fact]
        public async Task PersistsAcrossLaunches()
        {
            var store = new MemoryKeyValueStore();
            await Make(new Engine(), store).SetEnabled(false);
            Assert.Equal("0", await Get(store, StraitClient.EnabledKey));
            var e = new Engine();
            var s = Make(e, store); // next launch, same storage
            Assert.False(await s.IsEnabled());
            await s.Start(null);
            await s.TrackEvent("signup");
            await Settle();
            Assert.Empty(e.Calls);
        }

        [Fact]
        public async Task ReEnableRunsTheSkippedFirstLaunchCheckAndSendsEvents()
        {
            var e = new Engine();
            var store = new MemoryKeyValueStore();
            var s = Make(e, store);
            await s.SetEnabled(false);
            await s.Start(null);
            await Settle();
            Assert.Empty(e.Calls);
            await s.SetEnabled(true);
            await Settle();
            Assert.True(await s.IsEnabled());
            Assert.Contains("/v1/match", e.Calls);
            Assert.Equal("1", await Get(store, StraitClient.DeferredFlag));
            Assert.True(await s.TrackEvent("purchase"));
            Assert.Contains("/v1/event", e.Calls);
        }

        [Fact]
        public async Task UnreadableStorageCountsAsOn()
        {
            var e = new Engine();
            var s = Make(e, new ThrowingStore());
            Assert.True(await s.IsEnabled());
            Assert.True(await s.TrackEvent("signup"));
            Assert.Equal(new[] { "/v1/event" }, e.Calls);
        }

        private sealed class ThrowingStore : IKeyValueStore
        {
            public Task<string?> GetItemAsync(string key) => throw new InvalidOperationException("locked");
            public Task SetItemAsync(string key, string value) => throw new InvalidOperationException("locked");
        }
    }
}
