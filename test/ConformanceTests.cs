using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>shared-spec/conformance-vectors.json — every case must pass (SDK-CONTRACT.md).</summary>
    public class ConformanceTests
    {
        private static JsonElement Vectors()
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance-vectors.json"));
            return JsonDocument.Parse(text).RootElement;
        }

        private static Dictionary<string, string> Dict(JsonElement obj) =>
            obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        private static Dictionary<string, string> Dict(IReadOnlyDictionary<string, string>? d) =>
            d == null ? new Dictionary<string, string>() : d.ToDictionary(kv => kv.Key, kv => kv.Value);

        [Fact]
        public void VectorFileHasEverySection()
        {
            var v = Vectors();
            Assert.Equal(5, v.GetProperty("version").GetInt32());
            foreach (var section in new[] { "screenWidth", "portraitScreenWidth", "splitUrl", "referrer", "referrerClick", "takeClickId", "classify",
                         "linkHosts", "appState", "openQueue", "retry", "eventClickId", "replyClickId" })
                Assert.True(v.GetProperty(section).GetArrayLength() > 0, section);
        }

        [Fact]
        public void Constants()
        {
            var c = Vectors().GetProperty("constants");
            Assert.Equal(AppStateTracker.ResumeWindowMs, c.GetProperty("RESUME_WINDOW_MS").GetInt32());
            Assert.Equal(AppStateTracker.TransientPauseMs, c.GetProperty("TRANSIENT_PAUSE_MS").GetInt32());
            Assert.Equal(StraitCore.OpenQueueMax, c.GetProperty("OPEN_QUEUE_MAX").GetInt32());
            Assert.Equal(StraitCore.OpenQueueMaxAgeMs, c.GetProperty("OPEN_QUEUE_MAX_AGE_MS").GetInt64());
            Assert.Equal(StraitCore.AttributionWindowMs, c.GetProperty("ATTRIBUTION_WINDOW_MS").GetInt64());
            Assert.Equal(5, c.EnumerateObject().Count());
            Assert.Equal(2000, AppStateTracker.ResumeWindowMs);
            Assert.Equal(1000, AppStateTracker.TransientPauseMs);
            Assert.Equal(100, StraitCore.OpenQueueMax);
            Assert.Equal(604_800_000L, StraitCore.OpenQueueMaxAgeMs);
            Assert.Equal(604_800_000L, StraitCore.AttributionWindowMs);
        }

        [Fact]
        public void EventClickIdVectors()
        {
            foreach (var c in Vectors().GetProperty("eventClickId").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString();
                var got = StraitCore.EventClickId(StrOrNull(c.GetProperty("stored")), c.GetProperty("now").GetInt64(), StrOrNull(c.GetProperty("explicit")));
                Assert.True(StrOrNull(c.GetProperty("expected")) == got, $"{name}: got '{got}'");
            }
        }

        [Fact]
        public void ReplyClickIdVectors()
        {
            foreach (var c in Vectors().GetProperty("replyClickId").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString();
                var r = c.GetProperty("reply");
                object? reply = r.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => r.GetString(),
                    JsonValueKind.Number => r.GetDouble(),
                    _ => r.ToString(),
                };
                var got = StraitCore.ReplyClickId(reply, StrOrNull(c.GetProperty("fallback")));
                Assert.True(StrOrNull(c.GetProperty("expected")) == got, $"{name}: got '{got}'");
            }
        }

        [Fact]
        public void RememberTap_RoundTrips()
        {
            Assert.Equal("{\"clickId\":\"3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f\",\"at\":1800000000000}",
                StraitCore.RememberTap("3F2A9C1E-7B4D-4E8A-9C0F-1A2B3C4D5E6F", 1_800_000_000_000));
            Assert.Equal("3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f",
                StraitCore.EventClickId(StraitCore.RememberTap("3f2a9c1e-7b4d-4e8a-9c0f-1a2b3c4d5e6f", 10), 10));
        }

        [Fact]
        public void BrowserScreenWidthVectors()
        {
            foreach (var c in Vectors().GetProperty("screenWidth").EnumerateArray())
            {
                double logical = c.GetProperty("logical").GetDouble();
                Assert.True(c.GetProperty("expected").GetInt32() == StraitCore.BrowserScreenWidth(logical), $"logical={logical:R}");
            }
        }

        [Fact]
        public void PortraitScreenWidthVectors()
        {
            foreach (var c in Vectors().GetProperty("portraitScreenWidth").EnumerateArray())
            {
                double w = c.GetProperty("width").GetDouble(), h = c.GetProperty("height").GetDouble();
                Assert.True(c.GetProperty("expected").GetInt32() == StraitCore.PortraitScreenWidth(w, h), $"{w:R}x{h:R}");
            }
        }

        [Fact]
        public void SplitUrlVectors()
        {
            foreach (var c in Vectors().GetProperty("splitUrl").EnumerateArray())
            {
                var input = c.GetProperty("input").GetString();
                var exp = c.GetProperty("expected");
                var got = StraitCore.SplitUrl(input);
                if (exp.ValueKind == JsonValueKind.Null)
                {
                    Assert.True(got == null, $"expected null for '{input}'");
                }
                else
                {
                    Assert.True(got != null, $"expected a result for '{input}'");
                    Assert.Equal(exp.GetProperty("scheme").GetString(), got!.Scheme);
                    Assert.Equal(exp.GetProperty("host").GetString(), got.Host);
                    Assert.Equal(exp.GetProperty("path").GetString(), got.Path);
                    Assert.Equal(Dict(exp.GetProperty("params")), Dict(got.Params));
                }
            }
        }

        [Fact]
        public void ParseStraitLinkVectors()
        {
            foreach (var c in Vectors().GetProperty("referrer").EnumerateArray())
            {
                var inp = c.GetProperty("input");
                string? input = inp.ValueKind == JsonValueKind.Null ? null : inp.GetString();
                var exp = c.GetProperty("expected");
                string? expected = exp.ValueKind == JsonValueKind.Null ? null : exp.GetString();
                Assert.True(expected == StraitCore.ParseStraitLink(input), $"input='{input}'");
            }
        }

        private static string? StrOrNull(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

        [Fact]
        public void ParseStraitClickVectors()
        {
            foreach (var c in Vectors().GetProperty("referrerClick").EnumerateArray())
            {
                string? input = StrOrNull(c.GetProperty("input"));
                string? expected = StrOrNull(c.GetProperty("expected"));
                Assert.True(expected == StraitCore.ParseStraitClick(input), $"input='{input}'");
            }
        }

        [Fact]
        public void TakeClickIdVectors()
        {
            foreach (var c in Vectors().GetProperty("takeClickId").EnumerateArray())
            {
                var input = c.GetProperty("input").GetString()!;
                var exp = c.GetProperty("expected");
                Assert.Equal(2, exp.EnumerateObject().Count());
                var (url, clickId) = StraitCore.TakeClickId(input);
                Assert.True(exp.GetProperty("url").GetString() == url, $"url for '{input}': got '{url}'");
                Assert.True(StrOrNull(exp.GetProperty("clickId")) == clickId, $"clickId for '{input}': got '{clickId}'");
            }
        }

        [Fact]
        public void PruneOpenQueueVectors()
        {
            foreach (var c in Vectors().GetProperty("openQueue").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString();
                var queue = c.GetProperty("queue").EnumerateArray()
                    .Select(r => (OpenId: r.GetProperty("openId").GetString()!, At: r.GetProperty("at").GetInt64())).ToList();
                var got = StraitCore.PruneOpenQueue(queue, c.GetProperty("now").GetInt64(), r => r.At).Select(r => r.OpenId).ToList();
                var expected = c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToList();
                Assert.True(expected.SequenceEqual(got), $"{name}: expected [{string.Join(",", expected)}] got [{string.Join(",", got)}]");
            }
        }

        [Fact]
        public void ShouldRetryReportVectors()
        {
            foreach (var c in Vectors().GetProperty("retry").EnumerateArray())
            {
                var st = c.GetProperty("status");
                int? status = st.ValueKind == JsonValueKind.Null ? (int?)null : st.GetInt32();
                Assert.True(c.GetProperty("expected").GetBoolean() == StraitCore.ShouldRetryReport(status), $"status={status}");
            }
        }

        [Fact]
        public void ClassifyUrlVectors()
        {
            foreach (var c in Vectors().GetProperty("classify").EnumerateArray())
            {
                var raw = c.GetProperty("raw").GetString();
                var hosts = c.GetProperty("linkHosts").EnumerateArray().Select(h => h.GetString()!).ToList();
                var exp = c.GetProperty("expected");
                var got = StraitCore.ClassifyUrl(raw, hosts);
                if (exp.ValueKind == JsonValueKind.Null)
                {
                    Assert.True(got == null, $"expected null for '{raw}'");
                }
                else
                {
                    Assert.True(got != null, $"expected a result for '{raw}'");
                    Assert.Equal(exp.GetProperty("route").GetString(), got!.Route);
                    bool needsResolve = exp.GetProperty("needsResolve").GetBoolean();
                    Assert.Equal(needsResolve, got.NeedsResolve);
                    if (needsResolve)
                    {
                        Assert.Equal(2, exp.EnumerateObject().Count());
                        Assert.Null(got.Url);
                        Assert.Null(got.Path);
                        Assert.Null(got.Params);
                        Assert.Null(got.ClickId);
                    }
                    else
                    {
                        Assert.Equal(exp.GetProperty("url").GetString(), got.Url);
                        Assert.Equal(exp.GetProperty("path").GetString(), got.Path);
                        Assert.Equal(Dict(exp.GetProperty("params")), Dict(got.Params));
                        Assert.True(StrOrNull(exp.GetProperty("clickId")) == got.ClickId, $"clickId for '{raw}'"); // present, possibly null
                    }
                }
            }
        }

        [Fact]
        public void NormalizeLinkHostsVectors()
        {
            foreach (var c in Vectors().GetProperty("linkHosts").EnumerateArray())
            {
                var endpoint = c.GetProperty("endpoint").GetString();
                var hosts = c.GetProperty("linkHosts").EnumerateArray().Select(h => h.GetString()).ToList();
                var expected = c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToList();
                var got = StraitCore.NormalizeLinkHosts(endpoint, hosts);
                Assert.True(expected.SequenceEqual(got), $"{endpoint}: expected [{string.Join(",", expected)}] got [{string.Join(",", got)}]");
            }
        }

        [Fact]
        public void AppStateTrackerVectors()
        {
            foreach (var c in Vectors().GetProperty("appState").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString();
                var tracker = new AppStateTracker();
                var got = new List<string>();
                foreach (var step in c.GetProperty("steps").EnumerateArray())
                {
                    var kind = step[0].GetString();
                    if (kind == "url") got.Add(tracker.Classify(step[1].GetInt64()));
                    else if (kind == "state") tracker.OnState(step[1].GetString()!, step[2].GetInt64());
                    else throw new InvalidOperationException($"unknown step '{kind}' in '{name}'");
                }
                var expected = c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()!).ToList();
                Assert.True(expected.SequenceEqual(got), $"{name}: expected [{string.Join(",", expected)}] got [{string.Join(",", got)}]");
            }
        }

        // ---- extra edge cases of the JS semantics (beyond the shared vectors)

        [Theory]
        [InlineData("a%ZZ+b", "a%ZZ+b")]        // malformed escape → original string, '+' intact
        [InlineData("a%C3+b", "a%C3+b")]        // truncated UTF-8 → original
        [InlineData("%C0%AF", "%C0%AF")]        // overlong → original (JS URIError)
        [InlineData("%ED%A0%80", "%ED%A0%80")]  // surrogate → original
        [InlineData("%F0%9F%98%80", "\U0001F600")]
        [InlineData("%", "%")]
        [InlineData("100%25", "100%")]
        [InlineData("a+b%2Bc", "a b+c")]
        public void DecodeMatchesJs(string input, string expected)
        {
            Assert.Equal(expected, StraitCore.Decode(input));
        }

        [Fact]
        public void NewOpenId_Format()
        {
            var re = new System.Text.RegularExpressions.Regex("^o_[a-z0-9]+_[a-z0-9]{12}$");
            var ids = Enumerable.Range(0, 50).Select(_ => StraitCore.NewOpenId(1_800_000_000_000)).ToList();
            Assert.All(ids, id => Assert.Matches(re, id));
            Assert.Equal(50, ids.Distinct().Count());
            Assert.StartsWith("o_mywpiww0_", StraitCore.NewOpenId(1_800_000_000_000)); // (1.8e12).toString(36)
            Assert.Equal("o_0_aaaaaaaaaaaa", StraitCore.NewOpenId(0, () => 0));
            Assert.Equal("o_z_999999999999", StraitCore.NewOpenId(35, () => 0.9999));
        }

        [Fact]
        public void SplitUrlEdgeCases()
        {
            var p = StraitCore.SplitUrl("  \uFEFFHTTPS://A.B/x?k=1&k=2#f  ")!;
            Assert.Equal("https", p.Scheme);
            Assert.Equal("a.b", p.Host);
            Assert.Equal("/x", p.Path);
            Assert.Equal("2", p.Params["k"]); // later duplicate wins
            Assert.Null(StraitCore.SplitUrl(null));
            Assert.Null(StraitCore.SplitUrl("1http://x"));
        }
    }
}
