using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("Strait.Signature.Tests")]

namespace Strait
{
    /// <summary>How the app received a link (wire values, identical across SDKs).</summary>
    public static class LinkRoutes
    {
        public const string AppLink = "app_link";
        public const string CustomScheme = "custom_scheme";
        public const string InstallReferrer = "install_referrer";
        public const string Fingerprint = "fingerprint";
    }

    /// <summary>What the app was doing when a link arrived.</summary>
    public static class LinkAppStates
    {
        public const string Closed = "closed";
        public const string Background = "background";
        public const string Foreground = "foreground";
    }

    /// <summary>App lifecycle states fed to <see cref="AppStateTracker"/> / StraitClient.OnAppState.</summary>
    public static class AppLifecycle
    {
        public const string Active = "active";
        public const string Background = "background";
        public const string Inactive = "inactive";
    }

    /// <summary>Result of <see cref="StraitCore.SplitUrl"/>.</summary>
    public sealed class SplitUrlResult
    {
        public SplitUrlResult(string scheme, string host, string path, IReadOnlyDictionary<string, string> @params)
        {
            Scheme = scheme; Host = host; Path = path; Params = @params;
        }
        public string Scheme { get; }
        public string Host { get; }
        public string Path { get; }
        public IReadOnlyDictionary<string, string> Params { get; }
    }

    /// <summary>
    /// Result of <see cref="StraitCore.ClassifyUrl"/>. When <see cref="NeedsResolve"/> is true the
    /// URL is a Strait short link (ask /v1/resolve) and Url/Path/Params/ClickId are null.
    /// </summary>
    public sealed class ClassifiedUrl
    {
        public ClassifiedUrl(string route, bool needsResolve, string? url, string? path, IReadOnlyDictionary<string, string>? @params,
            string? clickId = null)
        {
            Route = route; NeedsResolve = needsResolve; Url = url; Path = path; Params = @params; ClickId = clickId;
        }
        public string Route { get; }
        public bool NeedsResolve { get; }
        public string? Url { get; }
        public string? Path { get; }
        public IReadOnlyDictionary<string, string>? Params { get; }
        /// <summary>Tap id from a Strait hand-off (removed from Url/Params), else null.</summary>
        public string? ClickId { get; }
    }

    /// <summary>
    /// Pure, platform-free link logic: a 1:1 port of sdk-react-native/src/core.ts.
    /// conformance-vectors.json is the cross-language contract (shared-spec/SDK-CONTRACT.md).
    /// Deliberately avoids System.Uri: its semantics differ from the reference (B12).
    /// </summary>
    public static class StraitCore
    {
        /// <summary>
        /// Screen width as a browser reports it (<c>screen.width</c>). Chrome rounds fractional
        /// logical widths UP (1080 px at 2.625 = 411.43 → 412). JS: <c>Math.ceil(w - 0.001)</c>.
        /// </summary>
        public static int BrowserScreenWidth(double logicalWidth)
        {
            return (int)Math.Ceiling(logicalWidth - 0.001);
        }

        // JS: /^([a-z][a-z0-9+.-]*):\/\/([^/?#]*)([^?#]*)(?:\?([^#]*))?/i
        // Explicit A-Z instead of IgnoreCase so .NET's Unicode case folding (e.g. KELVIN SIGN ~ k)
        // can't widen the match beyond what JS's /i accepts for ASCII.
        private static readonly Regex UrlRe = new Regex(
            @"^([A-Za-z][A-Za-z0-9+.\-]*)://([^/?#]*)([^?#]*)(?:\?([^#]*))?",
            RegexOptions.CultureInvariant);

        private static readonly Regex SchemePrefixRe = new Regex(
            @"^[A-Za-z][A-Za-z0-9+.\-]*://", RegexOptions.CultureInvariant);

        /// <summary>
        /// Split a URL without platform URL classes. Scheme and host are lower-cased; '+' and
        /// %-escapes in the query are decoded; the fragment is dropped. Null if not a URL.
        /// </summary>
        public static SplitUrlResult? SplitUrl(string? u)
        {
            if (u == null) return null;
            var m = UrlRe.Match(JsTrim(u));
            if (!m.Success) return null;
            var @params = new Dictionary<string, string>();
            var query = m.Groups[4].Success ? m.Groups[4].Value : "";
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int i = pair.IndexOf('=');
                var k = i < 0 ? pair : pair.Substring(0, i);
                var v = i < 0 ? "" : pair.Substring(i + 1);
                @params[Decode(k)] = Decode(v); // later duplicates win, like the JS object
            }
            var path = m.Groups[3].Value;
            return new SplitUrlResult(
                m.Groups[1].Value.ToLowerInvariant(),
                m.Groups[2].Value.ToLowerInvariant(),
                path.Length == 0 ? "/" : path,
                @params);
        }

        // JS: /^[a-z0-9.-]+(:\d+)?$/i — ASCII-only classes and \z (.NET's \d and $ are wider).
        private static readonly Regex BareHostRe = new Regex(
            @"^[A-Za-z0-9.\-]+(:[0-9]+)?\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// The Strait short-link hosts: the endpoint's host plus each configured link host, given
        /// as a URL or a bare host ("go.brand.com", "localhost:3000"). Lower-cased, de-duplicated
        /// in order; blanks and anything with a path or spaces are ignored; ports are kept.
        /// </summary>
        public static IReadOnlyList<string> NormalizeLinkHosts(string? endpoint, IEnumerable<string?>? linkHosts = null)
        {
            var out_ = new List<string>();
            var all = new List<string?> { endpoint };
            if (linkHosts != null) all.AddRange(linkHosts);
            foreach (var h in all)
            {
                if (h == null) continue;
                var p = SplitUrl(h);
                string? host;
                if (p != null) host = p.Host;
                else
                {
                    var t = JsTrim(h);
                    host = BareHostRe.IsMatch(t) ? t.ToLowerInvariant() : null;
                }
                if (!string.IsNullOrEmpty(host) && !out_.Contains(host!)) out_.Add(host!);
            }
            return out_;
        }

        /// <summary>The strait_link id inside a Play Install Referrer string, or null.</summary>
        public static string? ParseStraitLink(string? referrer) => ReferrerParam(referrer, "strait_link");

        /// <summary>
        /// The tap id (strait_click) inside a Play Install Referrer string, or null.
        /// Joins the install to the exact tap that sent the user to the store.
        /// </summary>
        public static string? ParseStraitClick(string? referrer)
        {
            var v = ReferrerParam(referrer, "strait_click");
            return v != null && ClickIdRe.IsMatch(v) ? v : null;
        }

        private static string? ReferrerParam(string? referrer, string key)
        {
            if (string.IsNullOrEmpty(referrer)) return null;
            foreach (var pair in referrer!.Split('&'))
            {
                int i = pair.IndexOf('=');
                if (i < 0 || pair.Substring(0, i) != key) continue;
                var v = Decode(pair.Substring(i + 1));
                return v.Length == 0 ? null : v; // first match wins, even if empty
            }
            return null;
        }

        // JS: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i — a tap id as Strait issues it (uuid).
        private static readonly Regex ClickIdRe = new Regex(
            @"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Remove every <c>strait_click</c> parameter from a URL's query, keeping the rest of the URL
        /// byte-for-byte (fragment included). Returns the cleaned URL and the tap id (lower-cased;
        /// null when absent or malformed). The app never sees the tap id.
        /// </summary>
        public static (string Url, string? ClickId) TakeClickId(string raw)
        {
            var s = JsTrim(raw);
            int hash = s.IndexOf('#');
            var beforeHash = hash < 0 ? s : s.Substring(0, hash);
            var frag = hash < 0 ? "" : s.Substring(hash);
            int q = beforeHash.IndexOf('?');
            if (q < 0) return (s, null);
            string? clickId = null;
            var kept = new List<string>();
            foreach (var pair in beforeHash.Substring(q + 1).Split('&'))
            {
                int i = pair.IndexOf('=');
                if (Decode(i < 0 ? pair : pair.Substring(0, i)) != "strait_click") { kept.Add(pair); continue; }
                var v = Decode(i < 0 ? "" : pair.Substring(i + 1));
                if (ClickIdRe.IsMatch(v)) clickId = v.ToLowerInvariant();
            }
            var query = string.Join("&", kept);
            return (beforeHash.Substring(0, q) + (query.Length > 0 ? "?" + query : "") + frag, clickId);
        }

        /// <summary>
        /// What a URL handed to the app means:
        /// https on a Strait link host → short link (needs /v1/resolve);
        /// other https → it IS the destination;
        /// yourapp://host/path (browser hand-off) → destination https://host/path.
        /// A <c>strait_click</c> tap id is removed from the destination and returned apart.
        /// Null for anything that isn't a URL.
        /// </summary>
        public static ClassifiedUrl? ClassifyUrl(string? raw, IEnumerable<string> linkHosts)
        {
            var p0 = SplitUrl(raw);
            if (p0 == null) return null;
            bool isWeb = p0.Scheme == "https" || p0.Scheme == "http";
            if (isWeb)
            {
                foreach (var h in linkHosts)
                {
                    if (h != null && h.ToLowerInvariant() == p0.Host)
                        return new ClassifiedUrl(LinkRoutes.AppLink, true, null, null, null);
                }
            }
            var (clean, clickId) = TakeClickId(raw!);
            var p = SplitUrl(clean)!;
            var url = isWeb ? clean : SchemePrefixRe.Replace(clean, "https://", 1);
            return new ClassifiedUrl(isWeb ? LinkRoutes.AppLink : LinkRoutes.CustomScheme, false, url, p.Path, p.Params, clickId);
        }

        /// <summary>Open reports waiting to be sent are kept at most this long (7 days)…</summary>
        public const long OpenQueueMaxAgeMs = 7L * 24 * 60 * 60 * 1000;
        /// <summary>…and at most this many (oldest dropped first).</summary>
        public const int OpenQueueMax = 100;

        /// <summary>
        /// Prune a pending-report queue: drop reports older than <see cref="OpenQueueMaxAgeMs"/> (by
        /// their <paramref name="at"/>), then keep the newest <see cref="OpenQueueMax"/>. Order is kept.
        /// </summary>
        public static List<T> PruneOpenQueue<T>(IEnumerable<T> queue, long now, Func<T, long> at)
        {
            var recent = new List<T>();
            foreach (var r in queue) if (now - at(r) <= OpenQueueMaxAgeMs) recent.Add(r);
            return recent.Count > OpenQueueMax ? recent.GetRange(recent.Count - OpenQueueMax, OpenQueueMax) : recent;
        }

        /// <summary>Whether a failed report should be kept for retry: no answer (null), 429 or 5xx.</summary>
        public static bool ShouldRetryReport(int? status) => status == null || status == 429 || status >= 500;

        private const string OpenIdChars = "abcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>
        /// A unique id for one link open (the engine de-duplicates retries by it):
        /// <c>o_&lt;base36 ms&gt;_&lt;12 × [a-z0-9]&gt;</c>. <paramref name="random"/> returns [0, 1).
        /// </summary>
        public static string NewOpenId(long now, Func<double>? random = null)
        {
            random ??= DefaultRandom;
            var sb = new StringBuilder("o_").Append(ToBase36(now)).Append('_');
            for (int i = 0; i < 12; i++) sb.Append(OpenIdChars[(int)Math.Floor(random() * 36)]);
            return sb.ToString();
        }

        private static readonly Random Rng = new Random();
        private static double DefaultRandom() { lock (Rng) return Rng.NextDouble(); }

        /// <summary>JS <c>n.toString(36)</c> for an integer.</summary>
        internal static string ToBase36(long n)
        {
            if (n == 0) return "0";
            var sb = new StringBuilder();
            ulong u = n < 0 ? (ulong)(-(n + 1)) + 1 : (ulong)n;
            while (u > 0) { sb.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(u % 36)]); u /= 36; }
            if (n < 0) sb.Insert(0, '-');
            return sb.ToString();
        }

        /// <summary>
        /// JS <c>decodeURIComponent(s.replace(/\+/g, ' '))</c>, returning the ORIGINAL string
        /// (pluses intact) when the escapes are malformed, exactly as the reference's catch does.
        /// </summary>
        internal static string Decode(string s)
        {
            var replaced = s.Replace('+', ' ');
            return TryDecodeUriComponent(replaced, out var decoded) ? decoded : s;
        }

        /// <summary>ECMAScript decodeURIComponent: strict UTF-8, any malformed escape fails.</summary>
        internal static bool TryDecodeUriComponent(string s, out string result)
        {
            result = s;
            if (s.IndexOf('%') < 0) return true;
            var sb = new StringBuilder(s.Length);
            int k = 0, len = s.Length;
            while (k < len)
            {
                char c = s[k];
                if (c != '%') { sb.Append(c); k++; continue; }
                if (!TryHexByte(s, k, out int b)) return false;
                k += 3;
                if ((b & 0x80) == 0) { sb.Append((char)b); continue; }
                int n;
                if ((b & 0xE0) == 0xC0) n = 2;
                else if ((b & 0xF0) == 0xE0) n = 3;
                else if ((b & 0xF8) == 0xF0) n = 4;
                else return false; // continuation byte or invalid lead
                int cp = b & (0xFF >> (n + 1));
                for (int j = 1; j < n; j++)
                {
                    if (k >= len || s[k] != '%' || !TryHexByte(s, k, out int cb)) return false;
                    if ((cb & 0xC0) != 0x80) return false;
                    cp = (cp << 6) | (cb & 0x3F);
                    k += 3;
                }
                int min = n == 2 ? 0x80 : n == 3 ? 0x800 : 0x10000;
                if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
                sb.Append(char.ConvertFromUtf32(cp));
            }
            result = sb.ToString();
            return true;
        }

        private static bool TryHexByte(string s, int pct, out int value)
        {
            value = 0;
            if (pct + 2 >= s.Length) return false; // need two chars after '%'
            int hi = HexVal(s[pct + 1]), lo = HexVal(s[pct + 2]);
            if (hi < 0 || lo < 0) return false;
            value = (hi << 4) | lo;
            return true;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>String.prototype.trim: ECMAScript WhiteSpace + LineTerminator (not .NET's set).</summary>
        internal static string JsTrim(string s)
        {
            int start = 0, end = s.Length - 1;
            while (start <= end && IsJsSpace(s[start])) start++;
            while (end >= start && IsJsSpace(s[end])) end--;
            return s.Substring(start, end - start + 1);
        }

        private static bool IsJsSpace(char c)
        {
            switch (c)
            {
                case '\t': case '\n': case '\v': case '\f': case '\r': case ' ':
                case '\u00A0': case '\u1680': case '\u2028': case '\u2029':
                case '\u202F': case '\u205F': case '\u3000': case '\uFEFF':
                    return true;
                default:
                    return c >= '\u2000' && c <= '\u200A';
            }
        }
    }

    /// <summary>
    /// Tracks app lifecycle to label a link delivered while the app is running. Android wraps
    /// link delivery in a brief pause/resume and the link can arrive before or after the resume:
    /// a pause under <see cref="TransientPauseMs"/> is that delivery (app was on screen); a
    /// longer one means the user had left.
    /// </summary>
    public sealed class AppStateTracker
    {
        /// <summary>A link arriving this soon after the app came back to the front came "from background".</summary>
        public const int ResumeWindowMs = 2000;
        /// <summary>Pauses shorter than this are Android delivering the link, not the user leaving.</summary>
        public const int TransientPauseMs = 1000;

        private readonly object _gate = new object();
        private string _state = AppLifecycle.Active;
        private double _backgroundAt = double.NegativeInfinity;
        private double _resumeAt = double.NegativeInfinity;
        private double _backgroundFor;

        /// <param name="state">"active", "background" or "inactive".</param>
        /// <param name="now">Clock in ms.</param>
        public void OnState(string state, long now)
        {
            lock (_gate)
            {
                bool wasActive = _state == AppLifecycle.Active;
                bool isActive = state == AppLifecycle.Active;
                if (!isActive && wasActive) _backgroundAt = now;
                if (isActive && !wasActive)
                {
                    _resumeAt = now;
                    _backgroundFor = now - _backgroundAt;
                }
                _state = state;
            }
        }

        /// <summary>Label ("background" or "foreground") for a link delivered while running at <paramref name="now"/>.</summary>
        public string Classify(long now)
        {
            lock (_gate)
            {
                double? away = null;
                if (_state != AppLifecycle.Active) away = now - _backgroundAt;
                else if (now - _resumeAt <= ResumeWindowMs) away = _backgroundFor;
                return away.HasValue && away.Value >= TransientPauseMs ? LinkAppStates.Background : LinkAppStates.Foreground;
            }
        }
    }
}
