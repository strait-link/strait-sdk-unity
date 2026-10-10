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
    /// Contract B19: the iPhone paste handoff is the workspace's choice (Dashboard Settings → iPhone installs), read
    /// live from the /v1/match reply (`ios.pasteHandoff`), iOS-only, and claims only Strait handoff links.
    /// </summary>
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

        /// <summary>The engine's /v1/match reply for a workspace with these two Dashboard switches.</summary>
        private static string MatchReply(bool matched, bool deviceMatching, bool pasteHandoff) =>
            "{\"matched\":" + (matched ? "true,\"longUrl\":\"https://shop.example/p/7\",\"linkId\":\"lnk_7\",\"matchMethod\":\"exact_ext\"" : "false,\"matchMethod\":\"none\"")
            + (deviceMatching ? "" : ",\"reasons\":[\"device_matching_off\"]")
            + ",\"ios\":{\"deviceMatching\":" + (deviceMatching ? "true" : "false") + ",\"pasteHandoff\":" + (pasteHandoff ? "true" : "false") + "}}";

        /// <summary>No device match; the workspace has paste handoff on (the default for these tests) or off.</summary>
        private static Engine MatchMiss(bool pasteHandoff = true, bool deviceMatching = true)
        {
            var e = new Engine();
            e.Routes["/v1/match"] = (200, MatchReply(false, deviceMatching, pasteHandoff));
            return e;
        }

        private const string ClaimOk = "{\"matched\":true,\"longUrl\":\"https://shop.example/x\",\"linkId\":\"lnk_1\",\"matchMethod\":\"clipboard\"}";

        /// <summary><paramref name="boost"/> sets the obsolete ClipboardBoost, which must have no effect.</summary>
        private static (StraitClient, MemoryKeyValueStore, List<LinkEvent>) Make(Engine engine, SpyClipboard? clip, bool boost = false, string platform = "ios")
        {
            var storage = new MemoryKeyValueStore();
            var c = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Platform = platform, Storage = storage, Transport = engine,
                DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en-IN", Timezone = "Asia/Kolkata" },
#pragma warning disable CS0618 // ClipboardBoost is obsolete; the tests prove it is ignored
                ClipboardBoost = boost, Clipboard = clip, Clock = () => 1_000_000,
#pragma warning restore CS0618
            });
            var events = new List<LinkEvent>();
            c.OnLink += events.Add;
            return (c, storage, events);
        }

        [Fact]
        public void DefaultConfig_NoClipboard_BoostObsolete()
        {
            Assert.Null(new StraitConfig().Clipboard);
            Assert.Null(IosStraitClipboard.Default); // not an iOS player build: no clipboard at all
            var prop = typeof(StraitConfig).GetProperty(nameof(StraitConfig.Clipboard))!;
            Assert.NotNull(prop);
            var boost = typeof(StraitConfig).GetProperty("ClipboardBoost")!;
            Assert.NotNull(boost.GetCustomAttributes(typeof(ObsoleteAttribute), false).SingleOrDefault());
        }

        // ---------------------------------------------------------------- the four Dashboard combinations

        [Fact]
        public async Task OffOff_NoMatch_ClipboardNeverTouched()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss(pasteHandoff: false, deviceMatching: false);
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (s, storage, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths); // the install is still counted
            Assert.Equal(0, clip.Total);
            var e = events.Single();
            Assert.False(e.Matched);
            Assert.Equal("no_match", e.Reason);
            Assert.Equal("1", await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task DeviceOnly_Matched_ClipboardNeverTouched()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = new Engine();
            engine.Routes["/v1/match"] = (200, MatchReply(true, deviceMatching: true, pasteHandoff: false));
            var (s, _, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal(0, clip.Total);
            Assert.Equal("fingerprint", events.Single().Route);
            Assert.True(events.Single().Matched);
            Assert.Equal("/p/7", events.Single().Path);
        }

        [Fact]
        public async Task DeviceOnly_NoMatch_ClipboardNeverTouched()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss(pasteHandoff: false, deviceMatching: true);
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (s, _, events) = Make(engine, clip, boost: true); // the obsolete app flag changes nothing
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal(0, clip.Total);
            Assert.Equal("no_match", events.Single().Reason);
        }

        [Fact]
        public async Task PasteOnly_ClaimsTheHandoffWithTheSameOpenId()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss(pasteHandoff: true, deviceMatching: false);
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (s, storage, events) = Make(engine, clip); // the app set nothing
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match", "/v1/handoff/claim" }, engine.Paths);
            Assert.Equal(engine.Calls[0].Body!.Value.GetProperty("openId").GetString(), engine.Calls[1].Body!.Value.GetProperty("openId").GetString());
            Assert.Equal(1, clip.Detects);
            Assert.Equal(1, clip.Reads);
            var e = events.Single();
            Assert.Equal("clipboard", e.Route);
            Assert.True(e.Matched);
            Assert.Equal("1", await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task Both_DeviceMatchWins_ClipboardNeverTouched()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = new Engine();
            engine.Routes["/v1/match"] = (200, MatchReply(true, deviceMatching: true, pasteHandoff: true));
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (s, _, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal(0, clip.Total);
            var e = events.Single();
            Assert.Equal("fingerprint", e.Route);
            Assert.True(e.Matched);
        }

        [Fact]
        public async Task Both_NoDeviceMatch_FallsBackToPaste()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss(pasteHandoff: true, deviceMatching: true);
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (s, _, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match", "/v1/handoff/claim" }, engine.Paths); // device matching first
            Assert.Equal("clipboard", events.Single().Route);
            Assert.True(events.Single().Matched);
        }

        [Fact]
        public async Task OlderEngine_NoIosField_IsOff_EvenWithTheObsoleteFlag()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = new Engine();
            engine.Routes["/v1/match"] = (200, "{\"matched\":false,\"matchMethod\":\"none\"}");
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (s, _, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal(0, clip.Total);
            Assert.Equal("no_match", events.Single().Reason);
        }

        [Theory]
        [InlineData(429)]
        [InlineData(503)]
        public async Task MatchUnanswered_ChoiceUnknown_ClipboardUntouched_RetriesNextLaunch(int status)
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = new Engine();
            engine.Routes["/v1/match"] = (status, "");
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (s, storage, events) = Make(engine, clip, boost: true);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal(0, clip.Total);
            Assert.Equal("network", events.Single().Reason);
            Assert.Null(await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task Offline_IsNetwork_ClipboardUntouched()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            engine.Offline = true;
            var (s, storage, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(0, clip.Total);
            Assert.Equal("network", events.Single().Reason);
            Assert.Null(await storage.GetItemAsync(StraitClient.DeferredFlag));
        }

        [Fact]
        public async Task ChoiceIsReadLive_NotStored_TwoInstallsFollowTheirOwnReply()
        {
            // Same game build, the Dashboard switch flipped between two installs.
            var clipA = new SpyClipboard { Text = Handoff };
            var engineA = MatchMiss(pasteHandoff: false);
            engineA.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (a, storageA, eventsA) = Make(engineA, clipA);
            await a.Start(null);
            Assert.Equal(0, clipA.Total);
            Assert.Equal("no_match", eventsA.Single().Reason);

            var clipB = new SpyClipboard { Text = Handoff };
            var engineB = MatchMiss(pasteHandoff: true);
            engineB.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            var (b, storageB, eventsB) = Make(engineB, clipB);
            await b.Start(null);
            Assert.Equal(1, clipB.Reads);
            Assert.Equal("clipboard", eventsB.Single().Route);

            // Nothing about the choice is kept on the device.
            foreach (var st in new[] { storageA, storageB })
                foreach (var k in new[] { "strait.pasteHandoff", "strait.ios", "strait.settings" })
                    Assert.Null(await st.GetItemAsync(k));
        }

        [Fact]
        public async Task FailedCheck_RetriedNextLaunch_UsesThatLaunchesReply()
        {
            var storage = new MemoryKeyValueStore();
            var clip = new SpyClipboard { Text = Handoff };
            var engine = new Engine();
            engine.Routes["/v1/match"] = (503, "");
            engine.Routes["/v1/handoff/claim"] = (200, ClaimOk);
            StraitClient Client() => new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Platform = "ios", Storage = storage, Transport = engine,
                DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en-IN", Timezone = "Asia/Kolkata" },
                Clipboard = clip, Clock = () => 1_000_000,
            });
            var first = new List<LinkEvent>();
            var c1 = Client(); c1.OnLink += first.Add;
            await c1.Start(null);
            Assert.Equal("network", first.Single().Reason);
            Assert.Equal(0, clip.Total);
            // Next launch: the engine answers, and the customer has paste handoff on by now.
            engine.Routes["/v1/match"] = (200, MatchReply(false, deviceMatching: false, pasteHandoff: true));
            var second = new List<LinkEvent>();
            var c2 = Client(); c2.OnLink += second.Add;
            await c2.Start(null);
            Assert.Equal("clipboard", second.Single().Route);
            Assert.True(second.Single().Matched);
        }

        // ---------------------------------------------------------------- the clipboard step itself (paste handoff on)

        [Fact]
        public async Task Default_AppSetsNothing_PasteOff_NeverTouchesTheClipboard()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss(pasteHandoff: false);
            var (s, _, _) = Make(engine, clip);
            await s.Start(null);
            await s.CheckDeferred();
            Assert.Equal(0, clip.Total);
            Assert.DoesNotContain("/v1/handoff/claim", engine.Paths);
            Assert.Equal("/v1/match", engine.Paths[0]);
        }

        [Fact]
        public async Task PasteOn_Android_NeverTouchesTheClipboard()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            var (s, _, _) = Make(engine, clip, platform: "android");
            await s.Start(null);
            Assert.Equal(0, clip.Total);
        }

        [Fact]
        public async Task PasteOn_DebugCheckDeferred_NeverTouchesTheClipboard()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            var (s, _, _) = Make(engine, clip);
            await s.CheckDeferred();
            Assert.Equal(0, clip.Total);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
        }

        [Fact]
        public async Task PasteOn_NoProbableUrl_DetectsButNeverReads()
        {
            var clip = new SpyClipboard { Probable = false, Text = Handoff };
            var engine = MatchMiss();
            var (s, _, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(1, clip.Detects);
            Assert.Equal(0, clip.Reads);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal("fingerprint", events.Single().Route);
        }

        [Fact]
        public async Task PasteOn_NotAHandoffLink_SendsNothingAboutTheClipboard()
        {
            var clip = new SpyClipboard { Text = $"https://evil.example/h/{Token}" };
            var engine = MatchMiss();
            var (s, _, _) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(1, clip.Reads);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.DoesNotContain(Token, engine.Calls[0].Body!.Value.GetRawText());
        }

        [Fact]
        public async Task PasteOn_ThrowingClipboard_FallsBackToMatch()
        {
            var clip = new SpyClipboard { Throws = true };
            var engine = MatchMiss();
            var (s, _, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match" }, engine.Paths);
            Assert.Equal("no_match", events.Single().Reason);
        }

        [Fact]
        public async Task PasteOn_HandoffClaimed_ExactMatchOnRouteClipboard()
        {
            var clip = new SpyClipboard { Text = "  " + Handoff + "\n" };
            var engine = MatchMiss();
            engine.Routes["/v1/handoff/claim"] = (200,
                $"{{\"matched\":true,\"longUrl\":\"https://shop.example/p/42?color=red\",\"linkId\":\"lnk_42\",\"clickId\":\"{Click.ToUpperInvariant()}\",\"matchMethod\":\"clipboard\"}}");
            var (s, storage, events) = Make(engine, clip);
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
        public async Task PasteOn_ClaimRefused_KeepsSignalMatchResultWithSameOpenId()
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            engine.Routes["/v1/handoff/claim"] = (200, "{\"matched\":false,\"matchMethod\":\"none\",\"reason\":\"handoff_used\"}");
            var (s, storage, events) = Make(engine, clip);
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
        public async Task PasteOn_ClaimUnanswered_IsNetworkAndRetriesNextLaunch(int status)
        {
            var clip = new SpyClipboard { Text = Handoff };
            var engine = MatchMiss();
            engine.Routes["/v1/handoff/claim"] = (status, "");
            var (s, storage, events) = Make(engine, clip);
            await s.Start(null);
            Assert.Equal(new[] { "/v1/match", "/v1/handoff/claim" }, engine.Paths);
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
