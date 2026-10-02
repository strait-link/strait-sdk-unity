using System;
using System.Collections.Generic;
using System.Text.Json;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>The dependency-free JSON writer/reader, cross-checked against System.Text.Json.</summary>
    public class JsonTests
    {
        private static KeyValuePair<string, object?> Kv(string k, object? v) => new KeyValuePair<string, object?>(k, v);

        [Theory]
        [InlineData("plain")]
        [InlineData("quote \" backslash \\ slash /")]
        [InlineData("ctrl \b\f\n\r\t \u0001 \u001f end")]
        [InlineData("line sep \u2028 para \u2029")]
        [InlineData("José 😀 मराठी")]
        [InlineData("\",\"publishableKey\":\"evil")]
        [InlineData("")]
        public void WriterEscapesEveryString_RoundTripsThroughSystemTextJson(string s)
        {
            var json = StraitJson.Serialize(new[] { Kv("k", s), Kv(s, "v") });
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(s, doc.RootElement.GetProperty("k").GetString());
            Assert.Equal("v", doc.RootElement.GetProperty(s).GetString());
            Assert.Equal(s, ((Dictionary<string, object?>)StraitJson.Parse(json)!)["k"]);
        }

        [Fact]
        public void LoneSurrogatesAreEscaped()
        {
            var json = StraitJson.Serialize(new[] { Kv("k", "a\uD800b\uDC00c") });
            Assert.Equal("{\"k\":\"a\\ud800b\\udc00c\"}", json);
        }

        [Fact]
        public void NumbersBoolsNullsAndOmission()
        {
            var json = StraitJson.Serialize(new[]
            {
                Kv("i", 411), Kv("l", 1234567890123L), Kv("d", 2.625), Kv("w", 3.0), Kv("neg", -0.5),
                Kv("nan", double.NaN), Kv("t", true), Kv("f", false), Kv("skip", null),
                Kv("nested", new Dictionary<string, object?> { ["a"] = 1, ["s"] = "x" }),
                Kv("arr", new List<object?> { 1, "two", null }),
                Kv("strDict", new Dictionary<string, string> { ["k"] = "v" }),
            });
            Assert.Equal("{\"i\":411,\"l\":1234567890123,\"d\":2.625,\"w\":3,\"neg\":-0.5,\"nan\":null,\"t\":true,\"f\":false,"
                + "\"nested\":{\"a\":1,\"s\":\"x\"},\"arr\":[1,\"two\",null],\"strDict\":{\"k\":\"v\"}}", json);
            using var doc = JsonDocument.Parse(json); // valid JSON
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        [Fact]
        public void ParsesEngineResponses()
        {
            var o = StraitJson.ParseObjectOrEmpty(
                " {\"matched\" : true, \"longUrl\":\"https:\\/\\/a.b\\/p?x=1\", \"n\": -1.5e2, \"z\":null,"
                + " \"arr\":[1,{\"a\":[]}], \"u\":\"\\u00e9\\ud83d\\ude00\", \"matched\":false } ");
            Assert.Equal(false, o["matched"]); // duplicate keys: last wins
            Assert.Equal("https://a.b/p?x=1", o["longUrl"]);
            Assert.Equal(-150.0, o["n"]);
            Assert.Null(o["z"]);
            Assert.Equal("é😀", o["u"]);
            Assert.IsType<List<object?>>(o["arr"]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("<html>502 Bad Gateway</html>")]
        [InlineData("[1,2]")]
        [InlineData("\"str\"")]
        [InlineData("{\"a\":1,}")]
        [InlineData("{\"a\":01}")]
        [InlineData("{\"a\":tru}")]
        [InlineData("{\"a\":\"unterminated}")]
        [InlineData("{\"a\":1} x")]
        public void BadOrNonObjectBodies_BecomeEmpty(string text)
        {
            Assert.Empty(StraitJson.ParseObjectOrEmpty(text));
        }

        [Fact]
        public void DeepNestingIsRejectedNotStackOverflow()
        {
            var deep = new string('[', 10_000) + new string(']', 10_000);
            Assert.Throws<FormatException>(() => StraitJson.Parse(deep));
            Assert.Empty(StraitJson.ParseObjectOrEmpty("{\"a\":" + deep + "}"));
        }
    }
}
