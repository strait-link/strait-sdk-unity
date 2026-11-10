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
    /// Port of sdk-react-native/test/return-note.test.ts. Contract B28 (shared-spec ADR 0012): at most three "came back"
    /// notes per install (day 1, 7, 30), carrying the install's tap id.
    /// </summary>
    public class ReturnNoteTests
    {
        private const string PK = "st_pub_test_x";
        private const string Click = "3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f";
        private const string Other = "11111111-2222-4333-8444-555555555555";
        private const long T = 1_800_000_000_000;
        private const long Day = StraitCore.DayMs;

        /// <summary>Fake engine: records every call; /v1/match answers a match with an upper-case tap id.</summary>
        private sealed class Engine : IStraitTransport
        {
            public bool Down;
            public readonly List<(string Path, JsonElement Body)> Calls = new List<(string, JsonElement)>();
            public List<JsonElement> Notes() { lock (Calls) return Calls.Where(c => c.Body.TryGetProperty("kind", out var k) && k.GetString() == "return").Select(c => c.Body).ToList(); }
            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                lock (Calls) Calls.Add((path, JsonDocument.Parse(jsonBody ?? "{}").RootElement.Clone()));
                if (Down) return Task.FromResult((503, "{}"));
                return Task.FromResult(path == "/v1/match"
                    ? (200, "{\"matched\":true,\"clickId\":\"" + Click.ToUpperInvariant() + "\",\"longUrl\":\"https://shop.example/p/1\"}")
                    : (202, "{\"ok\":true}"));
            }
        }

        /// <summary>Memory store; writes to <see cref="FailKey"/> throw.</summary>
        private sealed class Store : IKeyValueStore
        {
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            public string? FailKey;
            public string? this[string k] { get { lock (Data) return Data.TryGetValue(k, out var v) ? v : null; } }
            public Task<string?> GetItemAsync(string key) => Task.FromResult(this[key]);
            public Task SetItemAsync(string key, string value)
            {
                if (key == FailKey) throw new InvalidOperationException("disk full");
                lock (Data) Data[key] = value;
                return Task.CompletedTask;
            }
        }

        private static StraitClient Make(Engine e, Store s, Func<long> clock) => new StraitClient(new StraitConfig
        {
            PublishableKey = PK, Endpoint = "https://acme.strait.link", Storage = s, Platform = "ios", Transport = e, Clock = clock,
            DeviceClass = () => "phone",
        });

        private static async Task Settle()
        {
            for (int i = 0; i < 30; i++) await Task.Delay(1);
        }

        private static JsonElement Tap(Store s) => JsonDocument.Parse(s[StraitClient.InstallTapKey]!).RootElement;

        [Fact]
        public void InstallTapValue_AndReturnNote_DueExactlyOnDay1_7_30_OnceEach()
        {
            var v = StraitCore.InstallTapValue(Click.ToUpperInvariant(), T);
            Assert.Equal("{\"clickId\":\"" + Click + "\",\"at\":1800000000000,\"sent\":[]}", v);
            Assert.Equal("{\"clickId\":\"" + Click + "\",\"at\":1800000000000,\"sent\":[1,7]}", StraitCore.InstallTapValue(Click, T, new[] { 1, 7 }));
            Assert.Null(StraitCore.ReturnNote(v, T + Day - 1));
            var n = StraitCore.ReturnNote(v, T + Day)!;
            Assert.Equal((1, Click, "r1-" + Click), (n.Day, n.ClickId, n.OpenId));
            Assert.Null(StraitCore.ReturnNote(v, T + 2 * Day));
            Assert.Equal(7, StraitCore.ReturnNote(v, T + 7 * Day + 5)!.Day);
            Assert.Equal(30, StraitCore.ReturnNote(v, T + 30 * Day)!.Day);
            Assert.Null(StraitCore.ReturnNote(v, T + 31 * Day));
            Assert.Null(StraitCore.ReturnNote(StraitCore.InstallTapValue(Click, T, new[] { 1 }), T + Day));
        }

        [Fact]
        public async Task InstallMatchKeepsTheTap_Day1SendsOneNoteOnStart_ASecondForegroundSendsNothing()
        {
            long now = T;
            var e = new Engine();
            var s = new Store();
            await Make(e, s, () => now).Start(null);
            await Settle();
            Assert.Equal(StraitCore.InstallTapValue(Click, T), s[StraitClient.InstallTapKey]);
            Assert.Empty(e.Notes()); // day 0: nothing

            now = T + Day + 60_000;
            var again = Make(e, s, () => now);
            await again.Start(null);
            await Settle();
            again.OnAppState(AppLifecycle.Background);
            again.OnAppState(AppLifecycle.Active);
            await Settle();
            var note = Assert.Single(e.Notes());
            Assert.Equal(
                "{\"publishableKey\":\"st_pub_test_x\",\"openId\":\"r1-" + Click + "\",\"kind\":\"return\",\"day\":1,\"clickId\":\"" + Click
                + "\",\"platform\":\"ios\",\"at\":" + now + ",\"sdkVersion\":\"unity/" + StraitClient.SdkVersion + "\"}",
                note.GetRawText());
            Assert.Equal("[1]", Tap(s).GetProperty("sent").GetRawText());
        }

        [Fact]
        public async Task ALaterTapNeverReplacesTheInstallTap_AFreshFirstLaunchClearsARestoredOne()
        {
            var e = new Engine();
            var s = new Store();
            s.Data[StraitClient.InstallTapKey] = StraitCore.InstallTapValue(Other, T - 40 * Day); // a backup of another install
            var strait = Make(e, s, () => T);
            await strait.Start(null);
            await Settle();
            await strait.HandleUrl("acme://p/2?strait_click=" + Other);
            await Settle();
            Assert.Equal(Click, Tap(s).GetProperty("clickId").GetString());
            Assert.Equal(Other, JsonDocument.Parse(s[StraitClient.TapKey]!).RootElement.GetProperty("clickId").GetString());
        }

        [Fact]
        public async Task AFirstLaunchOpenedByALinkWithATapIdKeepsThatTap()
        {
            var e = new Engine();
            var s = new Store();
            await Make(e, s, () => T).Start("acme://p/2?strait_click=" + Click);
            await Settle();
            Assert.Equal(StraitCore.InstallTapValue(Click, T), s[StraitClient.InstallTapKey]);
        }

        [Fact]
        public async Task Offline_TheNoteIsQueuedAndFlushedLater_OptedOut_NothingAndTheInstallTapIsDeleted()
        {
            long now = T + 7 * Day;
            var e = new Engine { Down = true };
            var s = new Store();
            s.Data[StraitClient.DeferredFlag] = "1";
            s.Data[StraitClient.InstallTapKey] = "{\"clickId\":\"" + Click + "\",\"at\":" + T + ",\"sent\":[1]}";
            var strait = Make(e, s, () => now);
            await strait.Start(null);
            await Settle();
            var queued = Assert.Single(JsonDocument.Parse(s[StraitClient.QueueKey]!).RootElement.EnumerateArray().ToList());
            Assert.Equal("return", queued.GetProperty("type").GetString());
            Assert.Equal("r7-" + Click, queued.GetProperty("openId").GetString());
            Assert.Equal(7, queued.GetProperty("day").GetInt32());
            Assert.False(queued.TryGetProperty("publishableKey", out _));
            Assert.Equal("[1,7]", Tap(s).GetProperty("sent").GetRawText());

            e.Down = false;
            now += 3_600_001; // past the C1 hold
            await strait.FlushOpenReports();
            var last = e.Calls.Last();
            Assert.Equal("/v1/open", last.Path);
            Assert.Equal("r7-" + Click, last.Body.GetProperty("openId").GetString());
            Assert.Equal(PK, last.Body.GetProperty("publishableKey").GetString());
            Assert.False(last.Body.TryGetProperty("type", out _));
            Assert.False(last.Body.TryGetProperty("deviceClass", out _));
            Assert.Equal(0, await strait.PendingOpenReports());

            await strait.SetEnabled(false);
            Assert.Equal("", s[StraitClient.InstallTapKey]);
            now = T + 30 * Day;
            int before = e.Calls.Count;
            strait.OnAppState(AppLifecycle.Active);
            await Settle();
            Assert.Equal(before, e.Calls.Count);
        }

        [Fact]
        public async Task WhileOptedOut_NothingIsComputedWrittenOrSent()
        {
            var e = new Engine();
            var s = new Store();
            s.Data[StraitClient.DeferredFlag] = "1";
            s.Data[StraitClient.EnabledKey] = "0";
            var tap = StraitCore.InstallTapValue(Click, T);
            s.Data[StraitClient.InstallTapKey] = tap;
            var strait = Make(e, s, () => T + Day);
            await strait.Start(null);
            strait.OnAppState(AppLifecycle.Active);
            await Settle();
            Assert.Empty(e.Calls);
            Assert.Equal(tap, s[StraitClient.InstallTapKey]);
        }

        [Fact]
        public async Task AFailedMarkWriteSendsNothing()
        {
            var e = new Engine();
            var s = new Store { FailKey = StraitClient.InstallTapKey };
            s.Data[StraitClient.DeferredFlag] = "1";
            s.Data[StraitClient.InstallTapKey] = StraitCore.InstallTapValue(Click, T);
            var strait = Make(e, s, () => T + Day);
            await strait.Start(null);
            strait.OnAppState(AppLifecycle.Active);
            await Settle();
            Assert.Empty(e.Notes());
        }
    }
}
