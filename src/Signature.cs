using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Strait
{
    /// <summary>
    /// Strait deferred-match signature — C# port of shared-spec/RECIPE.md.
    /// MUST be byte-identical to the JS reference + every other SDK (golden vectors).
    /// C# int overflow is wrapped with `unchecked` to match JS's 32-bit `h |= 0`.
    /// </summary>
    public readonly struct Signature
    {
        public readonly string CoreRaw, ExtRaw, CoreHash, ExtHash;
        public Signature(string coreRaw, string extRaw, string coreHash, string extHash)
        {
            CoreRaw = coreRaw; ExtRaw = extRaw; CoreHash = coreHash; ExtHash = extHash;
        }
    }

    public static class StraitSignature
    {
        private static readonly Dictionary<string, string> RegionMap = new()
        {
            ["Asia/Kolkata"] = "IN", ["Asia/Karachi"] = "PK", ["Asia/Dhaka"] = "BD",
            ["America/New_York"] = "US", ["America/Chicago"] = "US", ["America/Denver"] = "US",
            ["America/Los_Angeles"] = "US", ["Europe/London"] = "GB", ["Europe/Paris"] = "EU",
            ["Europe/Berlin"] = "EU", ["Asia/Singapore"] = "SG", ["Asia/Dubai"] = "AE",
            ["Australia/Sydney"] = "AU",
        };

        /// <summary>Deterministic 32-bit string hash (Java hashCode → abs → hex), matching JS.</summary>
        public static string H32(string s)
        {
            int h = 0;
            foreach (char c in s) // C# char iterates UTF-16 code units like JS
            {
                unchecked { h = (h << 5) - h + c; } // wraps at 32 bits like JS
            }
            long magnitude = h < 0 ? -(long)h : h; // abs(Int.MinValue) needs long, matches JS
            return magnitude.ToString("x", CultureInfo.InvariantCulture);
        }

        /// <summary>Mirror JS String(Number): whole numbers print with no decimal point.</summary>
        public static string NumStr(double n)
        {
            if (n == Math.Floor(n) && !double.IsInfinity(n))
                return ((long)n).ToString(CultureInfo.InvariantCulture);
            return n.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string RegionFromTimezone(string timezone)
        {
            var tz = timezone == "Asia/Calcutta" ? "Asia/Kolkata" : timezone;
            return RegionMap.TryGetValue(tz, out var r) ? r : "XX";
        }

        public static Signature Compute(double screenWidth, double pixelRatio, string language, string ip, string timezone)
        {
            int sw = (int)Math.Round(screenWidth, MidpointRounding.AwayFromZero);
            string rawLang = string.IsNullOrEmpty(language) ? "en" : language;
            string lang = (rawLang.Length <= 2 ? rawLang : rawLang.Substring(0, 2)).ToLowerInvariant();

            var coreFields = new List<string> { "universal", NumStr(sw), NumStr(pixelRatio), lang, ip };
            string coreRaw = string.Join("|", coreFields);

            int physWidth = (int)Math.Round(sw * pixelRatio / 8.0, MidpointRounding.AwayFromZero) * 8;
            string region = RegionFromTimezone(timezone);
            var extFields = new List<string>(coreFields) { NumStr(physWidth), region };
            string extRaw = string.Join("|", extFields);

            return new Signature(coreRaw, extRaw, H32(coreRaw), H32(extRaw));
        }
    }
}
