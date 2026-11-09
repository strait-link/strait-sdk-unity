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
    /// Contract B24 (engine ADR 0100): the first-launch check asked over IPv6 and found nothing; the engine names
    /// its IPv4-only URL and the SDK asks once more there, same openId, same fields. Only a match there counts.
    /// </summary>
    public class IpFamilyTests
    {
        private const string PK = "st_pub_test_appowner01";
        private const string Endpoint = "https://links.test";
        private const string V4 = "https://v4.straitlink.test/v1/match";
        private static string MissV6(string? ipv4Match = V4) =>
            "{\"matched\":false,\"matchMethod\":\"none\",\"reasons\":[\"no_candidate\"],\"ios\":{\"deviceMatching\":true,\"pasteHandoff\":false}"
            + (ipv4Match == null ? "" : ",\"ipv4Match\":" + JsonSerializer.Serialize(ipv4Match)) + "}";
        private const string HitV4 =
            "{\"matched\":true,\"longUrl\":\"https://shop.example/p/42\",\"linkId\":\"lnk_42\",\"clickId\":\"3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f\",\"matchMethod\":\"exact_ext\",\"reasons\":[\"exact_ext\",\"ip_family\"]}";

        /// <summary>Routes keyed by host + path; offline hosts throw.</summary>
        private sealed class Engine : IStraitTransport
        {
            public readonly Dictionary<string, string> Routes = new Dictionary<string, string>();
            public readonly HashSet<string> Offline = new HashSet<string>();
            public readonly List<(string Host, string Path, JsonElement? Body)> Calls = new List<(string, string, JsonElement?)>();
            public Action<string>? OnCall;
            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                var u = new Uri(url);
                Calls.Add((u.Host, u.AbsolutePath, jsonBody == null ? (JsonElement?)null : JsonDocument.Parse(jsonBody).RootElement.Clone()));
                OnCall?.Invoke(u.Host);
                if (Offline.Contains(u.Host)) throw new HttpRequestException("offline");
                return Task.FromResult(Routes.TryGetValue(u.Host + u.AbsolutePath, out var r) ? (200, r) : (404, ""));
            }
            public List<string> MatchHosts => Calls.Where(c => c.Path == "/v1/match").Select(c => c.Host).ToList();
        }

        private static (StraitClient, List<LinkEvent>) Make(Engine engine)
        {
            var c = new StraitClient(new StraitConfig
            {
                PublishableKey = PK, Endpoint = Endpoint, Platform = "ios", Storage = new MemoryKeyValueStore(), Transport = engine,
                DeviceFields = () => new DeviceFields { ScreenWidth = 390, PixelRatio = 3, Language = "en-IN", Timezone = "Asia/Kolkata" },
                Clock = () => 1_000_000,
            });
            var events = new List<LinkEvent>();
            c.OnLink += events.Add;
            return (c, events);
        }

        [Fact]
        public async Task SameBodyToTheNamedUrl_ItsMatchIsTheAnswer()
        {
            var e = new Engine();
            e.Routes["links.test/v1/match"] = MissV6();
            e.Routes["v4.straitlink.test/v1/match"] = HitV4;
            var (s, events) = Make(e);
            await s.Start(null);
            Assert.Equal(new[] { "links.test", "v4.straitlink.test" }, e.MatchHosts);
            var asks = e.Calls.Where(c => c.Path == "/v1/match").ToList();
            Assert.Equal(asks[0].Body!.Value.GetRawText(), asks[1].Body!.Value.GetRawText());
            Assert.True(asks[0].Body!.Value.TryGetProperty("openId", out _));
            var ev = Assert.Single(events);
            Assert.True(ev.Matched);
            Assert.Equal("https://shop.example/p/42", ev.Url);
            Assert.Equal("lnk_42", ev.LinkId);
        }

        [Fact]
        public async Task NoMatchThereOrUnreachable_TheFirstAnswerStands()
        {
            var a = new Engine();
            a.Routes["links.test/v1/match"] = MissV6();
            a.Routes["v4.straitlink.test/v1/match"] = "{\"matched\":false}";
            var (s1, ev1) = Make(a);
            await s1.Start(null);
            Assert.False(Assert.Single(ev1).Matched);
            Assert.Equal("no_match", ev1[0].Reason);

            var b = new Engine();
            b.Routes["links.test/v1/match"] = MissV6();
            b.Routes["v4.straitlink.test/v1/match"] = HitV4;
            b.Offline.Add("v4.straitlink.test");
            var (s2, ev2) = Make(b);
            await s2.Start(null);
            Assert.False(Assert.Single(ev2).Matched);
            Assert.Equal("no_match", ev2[0].Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("http://v4.straitlink.test/v1/match")]
        [InlineData("https://v4.straitlink.test/v1/open")]
        [InlineData("https://v4.straitlink.test/v1/match?x=1")]
        [InlineData("javascript:alert(1)")]
        [InlineData("/v1/match")]
        public async Task NoSecondAskWithoutTheFieldOrWithABadUrl(string? bad)
        {
            var e = new Engine();
            e.Routes["links.test/v1/match"] = MissV6(bad);
            e.Routes["v4.straitlink.test/v1/match"] = HitV4;
            var (s, _) = Make(e);
            await s.Start(null);
            Assert.Equal(new[] { "links.test" }, e.MatchHosts);
        }

        [Fact]
        public async Task ReadOnlyCheckDeferred_NeverAsksTwice()
        {
            var e = new Engine();
            e.Routes["links.test/v1/match"] = MissV6();
            e.Routes["v4.straitlink.test/v1/match"] = "{\"matched\":false}";
            var (s, _) = Make(e);
            await s.Start(null);
            var before = e.Calls.Count;
            await s.CheckDeferred();
            Assert.Equal(new[] { "links.test" }, e.Calls.Skip(before).Select(c => c.Host).ToArray());
        }

        [Fact]
        public async Task SwitchedOffDuringTheFirstAsk_NothingSentToTheIpv4Url()
        {
            var e = new Engine();
            e.Routes["links.test/v1/match"] = MissV6();
            e.Routes["v4.straitlink.test/v1/match"] = HitV4;
            var (s, _) = Make(e);
            e.OnCall = host => { if (host == "links.test") _ = s.SetEnabled(false); }; // the switch flips synchronously
            await s.Start(null);
            Assert.DoesNotContain("v4.straitlink.test", e.MatchHosts);
        }

        [Fact]
        public void Ipv4MatchUrl_OnlyAPlainHttpsMatchUrl()
        {
            Assert.Equal("https://v4.straitlink.in/v1/match", StraitCore.Ipv4MatchUrl("https://v4.straitlink.in/v1/match"));
            foreach (var bad in new object?[] { null, 1, "", "https://v4.straitlink.in/v1/match/", "https://u:p@v4.straitlink.in/v1/match", "https://v4.straitlink.in/v1/match#x", "ftp://x/v1/match" })
                Assert.Null(StraitCore.Ipv4MatchUrl(bad));
        }
    }
}
