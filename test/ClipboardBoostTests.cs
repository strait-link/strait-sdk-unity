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
    /// <summary>Contract B19: the iPhone clipboard boost is opt-in, iOS-only, and claims only Strait handoff links.</summary>
    public class ClipboardBoostTests
    {
        private const string PK = "st_pub_test_appowner01";
        private const string Endpoint = "https://links.test";
        private const string Token = "AbCdEfGhIjKlMnOpQrStUv";
        private const string Click = "3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f";
        private static readonly string Handoff = $"https://links.test/h/{Token}";

        /// <summary>Records every clipboard access.</summary>
        private sealed class SpyClipboard : IStraitClipboard
        {
            public bool Probable = true;
            public string? Text;
            public bool Throws;
            public int Detects, Reads;
            public Task<bool> HasProbableWebUrlAsync()
            {
                Detects++;
                if (Throws) throw new InvalidOperationException("boom");
                return Task.FromResult(Probable);
            }
            public Task<string?> ReadTextAsync()
            {
                Reads++;
                return Task.FromResult(Text);
            }
            public int Total => Detects + Reads;
        }

        private sealed class Engine : IStraitTransport
        {
            public readonly Dictionary<string, (int, string)> Routes = new Dictionary<string, (int, string)>();
            public readonly List<(string Path, JsonElement? Body)> Calls = new List<(string, JsonElement?)>();
            public bool Offline;
            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var path = new Uri(url).AbsolutePath;
                Calls.Add((path, jsonBody == null ? (JsonElement?)null : JsonDocument.Parse(jsonBody).RootElement.Clone()));
                if (Offline) throw new HttpRequestException("offline");
                return Task.FromResult(Routes.TryGetValue(path, out var r) ? r : (404, ""));
            }
            public List<string> Paths => Calls.Select(c => c.Path).ToList();
        }

        private static Engine MatchMiss()
        {
            var e = new Engine();
            e.Routes["/v1/match"] = (200, "{\"matched\":false,\"matchMethod\":\"none\"}");
            return e;
        }

        private static (StraitClient, MemoryKeyValueStore, List<LinkEvent>) Make(Engine engine, SpyClipboard? clip, bool boost, string platform = "ios")
        {
            var storage = new MemoryKeyValueStore();
            var c = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Platform = platform, Storage = storage, Transport = engine,
                DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en-IN", Timezone = "Asia/Kolkata" },
                ClipboardBoost = boost, Clipboard = clip, Clock = () => 1_000_000,
            });
            var events = new List<LinkEvent>();
            c.OnLink += events.Add;
            return (c, storage, events);
        }

        [Fact]
        public void DefaultConfig_BoostIsOff()
        {
            Assert.False(new StraitConfig().ClipboardBoost);
            Assert.Null(new StraitConfig().Clipboard);
            Assert.Null(IosStraitClipboard.Default); // not an iOS player build: no clipboard at all
        }

        [Fact]
        public async Task Default_NeverTouchesTheClipboard()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            var (s, _, events) = Make(engine, clip, boost: false);
            await s.Start(null);
            await s.CheckDeferred();
            Assert.Equal(0, clip.Total);
            Assert.DoesNotContain("/v1/handoff/claim", engine.Paths);
            Assert.Equal("/v1/match", engine.Paths[0]);
        }

        [Fact]
        public async Task Boost_OnAndroid_NeverTouchesTheClipboard()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            var (s, _, _) = Make(engine, clip, boost: true, platform: "android");
            await s.Start(null);
            Assert.Equal(0, clip.Total);
        }

        [Fact]
        public async Task Boost_DebugCheckDeferred_NeverTouchesTheClipboard()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            var (s, _, _) = Make(engine, clip, boost: true);
            await s.CheckDeferred();
            Assert.Equal(0, clip.Total);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
        }

        [Fact]
        public async Task Boost_NoProbableUrl_DetectsButNeverReads()
        {
            var clip = new SpyClipboard { Probable = false, Text = Handoff };
            var engine = MatchMiss();
            var (s, _, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(1, clip.Detects);
            Assert.Equal(0, clip.Reads);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal("fingerprint", events.Single().Route);
        }

        [Fact]
        public async Task Boost_NotAHandoffLink_SendsNothingAboutTheClipboard()
        {
            var clip = new SpyClipboard { Text = $"https://evil.example/h/{Token}" };
            var engine = MatchMiss();
            var (s, _, _) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(1, clip.Reads);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.DoesNotContain(Token, engine.Calls[0].Body!.Value.GetRawText());
        }

        [Fact]
        public async Task Boost_ThrowingClipboard_FallsBackToMatch()
        {
            var clip = new SpyClipboard { Throws = true };
            var engine = MatchMiss();
            var (s, _, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal("no_match", events.Single().Reason);
        }

        [Fact]
        public async Task Boost_HandoffClaimed_ExactMatchOnRouteClipboard()
        {
            var clip = new SpyClipboard { Text = "  " + Handoff + "\n" };
            var engine = MatchMiss();
            engine.Routes["/v1/handoff/claim"] = (200,
                $"{{\"matched\":true,\"longUrl\":\"https://shop.example/p/42?color=red\",\"linkId\":\"lnk_42\",\"clickId\":\"{Click.ToUpperInvariant()}\",\"matchMethod\":\"clipboard\"}}");
            var (s, storage, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match", "/v1/handoff/claim" }, engine.Paths); // device matching first
            var body = engine.Calls[1].Body!.Value;
            Assert.Equal(engine.Calls[0].Body!.Value.GetProperty("openId").GetString(), body.GetProperty("openId").GetString());
            Assert.Equal(PK, body.GetProperty("publishableKey").GetString());
            Assert.Equal(Token, body.GetProperty("token").GetString());
            Assert.Equal("ios", body.GetProperty("platform").GetString());
            Assert.Equal(1_000_000, body.GetProperty("at").GetInt64());
            var e = events.Single();
            Assert.Equal(e.Id, body.GetProperty("openId").GetString());
            Assert.Equal("deferred", e.Kind);
            Assert.Equal("clipboard", e.Route);
            Assert.True(e.Matched);
            Assert.Equal("/p/42", e.Path);
            Assert.Equal("lnk_42", e.LinkId);
            Assert.False(body.TryGetProperty("screenWidth", out _)); // no device fields on a claim
            Assert.Equal("1", await storage.GetItemAsync(StraitClient.DeferredFlag));
            await s.TrackEvent("purchase");
            Assert.Equal(Click, engine.Calls.Last().Body!.Value.GetProperty("clickId").GetString()); // B16 tap remembered
        }

        [Fact]
        public async Task Boost_DeviceMatchWins_ClipboardNeverTouched()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = new Engine();
            engine.Routes["/v1/match"] = (200, "{\"matched\":true,\"longUrl\":\"https://shop.example/p/7\",\"linkId\":\"lnk_7\"}");
            engine.Routes["/v1/handoff/claim"] = (200, "{\"matched\":true,\"longUrl\":\"https://shop.example/x\",\"linkId\":\"lnk_1\"}");
            var (s, _, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal(0, clip.Detects + clip.Reads);
            var e = events.Single();
            Assert.Equal("fingerprint", e.Route);
            Assert.True(e.Matched);
        }

        [Fact]
        public async Task Boost_DeviceMatchUnanswered_StillTriesClipboard()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = new Engine();
            engine.Routes["/v1/match"] = (503, "");
            engine.Routes["/v1/handoff/claim"] = (200, "{\"matched\":true,\"longUrl\":\"https://shop.example/x\",\"linkId\":\"lnk_1\"}");
            var (s, storage, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match", "/v1/handoff/claim" }, engine.Paths);
            Assert.Equal("clipboard", events.Single().Route);
            Assert.True(events.Single().Matched);
            Assert.Equal("1", await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task Boost_ClaimRefused_KeepsSignalMatchResultWithSameOpenId()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            engine.Routes["/v1/handoff/claim"] = (200, "{\"matched\":false,\"matchMethod\":\"none\",\"reason\":\"handoff_used\"}");
            var (s, storage, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match", "/v1/handoff/claim" }, engine.Paths);
            Assert.Equal(engine.Calls[0].Body!.Value.GetProperty("openId").GetString(), engine.Calls[1].Body!.Value.GetProperty("openId").GetString());
            var e = events.Single();
            Assert.Equal("fingerprint", e.Route);
            Assert.Equal("no_match", e.Reason);
            Assert.Equal("1", await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Theory]
        [InlineData(429)]
        [InlineData(503)]
        public async Task Boost_ClaimUnanswered_IsNetworkAndRetriesNextLaunch(int status)
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            engine.Routes["/v1/handoff/claim"] = (status, "");
            var (s, storage, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match", "/v1/handoff/claim" }, engine.Paths);
            Assert.Equal("network", events.Single().Reason);
            Assert.Null(await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task Boost_Offline_IsNetwork()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            engine.Offline = true;
            var (s, storage, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal("network", events.Single().Reason);
            Assert.Null(await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task ClaimHandoff_PasteButton_ClaimsWithoutTouchingTheClipboard()
        {
            var clip = new SpyClipboard();
            var engine = new Engine();
            engine.Routes["/v1/handoff/claim"] = (200, "{\"matched\":true,\"longUrl\":\"https://shop.example/x\",\"linkId\":\"lnk_1\"}");
            var (s, _, events) = Make(engine, clip, boost: false);
            var e = await s.ClaimHandoff(Handoff);
            Assert.Equal(0, clip.Total);
            Assert.True(e.Matched);
            Assert.Equal("clipboard", e.Route);
            Assert.Equal("deferred", e.Kind);
            Assert.Equal(e.Id, engine.Calls.Single().Body!.Value.GetProperty("openId").GetString());
        }

        [Fact]
        public async Task ClaimHandoff_NotAHandoff_NoNetworkCall()
        {
            var engine = new Engine();
            var (s, _, _) = Make(engine, null, boost: false);
            var e = await s.ClaimHandoff("https://links.test/promo");
            Assert.False(e.Matched);
            Assert.Equal("not_handoff", e.Reason);
            Assert.Empty(engine.Calls);
            Assert.Equal("not_handoff", (await s.ClaimHandoff(null)).Reason);
        }

        [Fact]
        public async Task ClaimHandoff_Refused_ReportsEngineReason()
        {
            var engine = new Engine();
            engine.Routes["/v1/handoff/claim"] = (200, "{\"matched\":false,\"reason\":\"handoff_expired\"}");
            var (s, _, _) = Make(engine, null, boost: false);
            Assert.Equal("handoff_expired", (await s.ClaimHandoff(Handoff)).Reason);
            engine.Offline = true;
            Assert.Equal("network", (await s.ClaimHandoff(Handoff)).Reason);
        }
    }
}
