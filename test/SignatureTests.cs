using System.IO;
using System.Text.Json;
using Strait;
using Xunit;

namespace Strait.Tests
{
    public class SignatureTests
    {
        private static JsonElement Vectors()
        {
            var text = File.ReadAllText("test-vectors.json");
            return JsonDocument.Parse(text).RootElement;
        }

        [Fact]
        public void H32GoldenVectors()
        {
            foreach (var v in Vectors().GetProperty("h32").EnumerateArray())
            {
                Assert.Equal(v.GetProperty("expected").GetString(),
                    StraitSignature.H32(v.GetProperty("input").GetString()!));
            }
        }

        [Fact]
        public void SignatureGoldenVectors()
        {
            foreach (var v in Vectors().GetProperty("signatures").EnumerateArray())
            {
                var inp = v.GetProperty("input");
                var exp = v.GetProperty("expected");
                var sig = StraitSignature.Compute(
                    inp.GetProperty("screenWidth").GetDouble(),
                    inp.GetProperty("pixelRatio").GetDouble(),
                    inp.GetProperty("language").GetString()!,
                    inp.GetProperty("ip").GetString()!,
                    inp.GetProperty("timezone").GetString()!);
                Assert.Equal(exp.GetProperty("coreRaw").GetString(), sig.CoreRaw);
                Assert.Equal(exp.GetProperty("extRaw").GetString(), sig.ExtRaw);
                Assert.Equal(exp.GetProperty("coreHash").GetString(), sig.CoreHash);
                Assert.Equal(exp.GetProperty("extHash").GetString(), sig.ExtHash);
            }
        }

        [Theory]
        [InlineData(3.0, "3")]
        [InlineData(2.625, "2.625")]
        [InlineData(1176.0, "1176")]
        public void NumStrParity(double n, string expected)
        {
            Assert.Equal(expected, StraitSignature.NumStr(n));
        }
    }
}
