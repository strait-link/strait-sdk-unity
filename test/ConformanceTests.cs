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
            Assert.Equal(1, v.GetProperty("version").GetInt32());
            foreach (var section in new[] { "screenWidth", "splitUrl", "referrer", "classify", "linkHosts", "appState" })
                Assert.True(v.GetProperty(section).GetArrayLength() > 0, section);
        }

        [Fact]
        public void Constants()
        {
            var c = Vectors().GetProperty("constants");
            Assert.Equal(AppStateTracker.ResumeWindowMs, c.GetProperty("RESUME_WINDOW_MS").GetInt32());
            Assert.Equal(AppStateTracker.TransientPauseMs, c.GetProperty("TRANSIENT_PAUSE_MS").GetInt32());
            Assert.Equal(2000, AppStateTracker.ResumeWindowMs);
            Assert.Equal(1000, AppStateTracker.TransientPauseMs);
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
                        Assert.Null(got.Url);
                        Assert.Null(got.Path);
                        Assert.Null(got.Params);
                    }
                    else
                    {
                        Assert.Equal(exp.GetProperty("url").GetString(), got.Url);
                        Assert.Equal(exp.GetProperty("path").GetString(), got.Path);
                        Assert.Equal(Dict(exp.GetProperty("params")), Dict(got.Params));
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
