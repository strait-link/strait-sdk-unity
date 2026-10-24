using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Strait
{
    /// <summary>One setup check (contract B23). <see cref="Severity"/> is "ok", "warn" or "error"; <see cref="Fix"/> is empty for "ok".</summary>
    public sealed class DoctorFinding : IEquatable<DoctorFinding>
    {
        public string Id { get; }
        public string Severity { get; }
        public string Title { get; }
        public string Fix { get; }

        public DoctorFinding(string id, string severity, string title, string fix)
        {
            Id = id; Severity = severity; Title = title; Fix = fix;
        }

        public bool Equals(DoctorFinding? o) => o != null && o.Id == Id && o.Severity == Severity && o.Title == Title && o.Fix == Fix;
        public override bool Equals(object? o) => Equals(o as DoctorFinding);
        public override int GetHashCode() => (Id + "\n" + Title).GetHashCode();
        /// <summary>The line <c>Doctor()</c> logs: "severity id: title" plus ". Fix: …" when there is one.</summary>
        public override string ToString() => $"{Severity} {Id}: {Title}" + (Fix.Length > 0 ? ". Fix: " + Fix : "");
    }

    /// <summary>The findings in order; <see cref="Ok"/> = no "error" finding.</summary>
    public sealed class DoctorReport
    {
        public bool Ok { get; }
        public IReadOnlyList<DoctorFinding> Findings { get; }

        public DoctorReport(IReadOnlyList<DoctorFinding> findings)
        {
            Findings = findings;
            Ok = !findings.Any(f => f.Severity == "error");
        }
    }

    /// <summary>What the link host answered for the platform's file. <see cref="Status"/> null = the request failed (network).</summary>
    public sealed class DoctorFile
    {
        public int? Status { get; }
        public string? Body { get; }
        public DoctorFile(int? status, string? body) { Status = status; Body = body; }
    }

    /// <summary>
    /// The checks behind <see cref="StraitClient.Doctor"/> (contract B23, shared-spec ADR 0002): the same ids,
    /// severities and wording in every Strait SDK. Pure and engine-free.
    /// </summary>
    public static class Doctor
    {
        private static readonly Regex HttpsHost = new Regex(@"^https://([^/?#\s@]+)(?:[/?#]|$)", RegexOptions.IgnoreCase);

        /// <summary>The endpoint's host when it is an https:// URL with a host, else null.</summary>
        public static string? Host(string? endpoint)
        {
            var m = HttpsHost.Match((endpoint ?? "").Trim());
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>Path of the file fetched from the link host: assetlinks.json on "android", apple-app-site-association on "ios".</summary>
        public static string FilePath(string platform) =>
            platform == "ios" ? "/.well-known/apple-app-site-association" : "/.well-known/assetlinks.json";

        /// <summary>
        /// The B23 findings. <paramref name="platform"/> is "android" or "ios"; <paramref name="appId"/> = Android
        /// package / iOS bundle ID, null when unknown; <paramref name="certSha256"/> = this build's signing certificate
        /// (Android, any format), null when unknown; <paramref name="file"/> = what the host answered, null when
        /// nothing was fetched (only the key and endpoint are checked then).
        /// </summary>
        public static DoctorReport Findings(string? publishableKey, string? endpoint, string platform, string? appId, string? certSha256, DoctorFile? file)
        {
            var out_ = new List<DoctorFinding>();
            var key = publishableKey ?? "";
            if (key.StartsWith("st_pub_", StringComparison.Ordinal)) out_.Add(Ok("key.ok", "Publishable key is set"));
            else if (key.StartsWith("st_live_", StringComparison.Ordinal) || key.StartsWith("st_test_", StringComparison.Ordinal))
                out_.Add(Err("key.secret", "This is a secret key, not the publishable key",
                    "Use the publishable key (st_pub_…) from Dashboard → Get started, and revoke this secret key in Dashboard → Settings → Secret keys: it has been inside your app."));
            else out_.Add(Err("key.invalid", "Not a publishable key", "Copy the publishable key (st_pub_…) from Dashboard → Get started."));

            var host = Host(endpoint);
            if (host == null)
                out_.Add(Err("endpoint.invalid", "The endpoint isn't an https link domain",
                    "Set endpoint to your link domain from Dashboard → Get started, e.g. https://<handle>.strait.link."));
            else if (file != null) out_.AddRange(HostFindings(platform, appId, certSha256, host, file));
            return new DoctorReport(out_);
        }

        private static IEnumerable<DoctorFinding> HostFindings(string platform, string? appId, string? certSha256, string host, DoctorFile file)
        {
            bool android = platform != "ios";
            if (file.Status == null)
                return new[] { Err("host.unreachable", $"Can't reach {host}", "Check that endpoint is your link domain (Dashboard → Get started) and that this device is online.") };
            var apps = file.Status == 200 ? ListedApps(android, file.Body) : null;
            if (apps == null)
                return new[] { Err("host.file_missing", $"{host} doesn't serve {(android ? "assetlinks.json" : "apple-app-site-association")}",
                    "Use your Strait link domain as endpoint (Dashboard → Get started); Strait serves this file there.") };
            if (apps.Count == 0)
                return new[] { Err("app.none", $"No {(android ? "Android" : "iOS")} app is set up for {host}", "Add your app in Dashboard → Apps & stores.") };

            var listed = string.Join(", ", apps.Select(a => a.Id));
            var app = appId?.Trim();
            if (string.IsNullOrEmpty(app))
                return new[] { new DoctorFinding("app.unknown", "warn", $"{host} lists {listed}", "Make sure your app is one of these.") };
            // iOS can't read its Team ID at runtime: match the bundle ID suffix, show the full appID.
            var match = apps.FirstOrDefault(a => android ? a.Id == app : a.Id.EndsWith("." + app, StringComparison.Ordinal));
            if (match.Id == null)
                return new[] { Err("app.missing", $"{host} doesn't list {app}", $"Add {app} in Dashboard → Apps & stores (listed now: {listed}).") };

            var found = new List<DoctorFinding> { Ok("app.listed", $"{host} lists {match.Id}") };
            var cert = android ? NormalizeFingerprint(certSha256 ?? "") : "";
            if (cert.Length > 0)
                found.Add(match.Fps.Contains(cert)
                    ? Ok("cert.listed", "This build's signing certificate is listed")
                    : Err("cert.missing", $"This build's signing certificate isn't listed for {app}",
                        $"Add {cert} in Dashboard → Apps & stores. Debug, release and Play App Signing keys each have their own fingerprint."));
            return found;
        }

        /// <summary>Upper-case, colon-separated, as assetlinks.json writes it (accepts any case, with or without colons).</summary>
        public static string NormalizeFingerprint(string s)
        {
            var hex = new string(s.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < hex.Length; i += 2)
            {
                if (sb.Length > 0) sb.Append(':');
                sb.Append(hex, i, 2);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Listed apps in file order, once each: Android package → its fingerprints; iOS full appID (TEAM.bundle).
        /// Null when the body isn't the JSON expected.
        /// </summary>
        private static List<(string Id, List<string> Fps)>? ListedApps(bool android, string? body)
        {
            object? json;
            try { json = StraitJson.Parse(body ?? ""); }
            catch (Exception) { return null; }
            var apps = new List<(string Id, List<string> Fps)>();
            void Add(string id, IEnumerable<string> fps)
            {
                var i = apps.FindIndex(a => a.Id == id);
                if (i < 0) apps.Add((id, fps.ToList()));
                else apps[i].Fps.AddRange(fps);
            }
            if (android)
            {
                if (!(json is List<object?> statements)) return null;
                foreach (var s in statements)
                {
                    if (!(s is Dictionary<string, object?> st) || !(Get(st, "target") is Dictionary<string, object?> t)) continue;
                    if (!(Get(t, "namespace") is string ns) || ns != "android_app" || !(Get(t, "package_name") is string pkg)) continue;
                    var fps = Get(t, "sha256_cert_fingerprints") as List<object?> ?? new List<object?>();
                    Add(pkg, fps.OfType<string>().Select(NormalizeFingerprint));
                }
                return apps;
            }
            if (!(json is Dictionary<string, object?> root) || !(Get(root, "applinks") is Dictionary<string, object?> applinks)
                || !(Get(applinks, "details") is List<object?> details)) return null;
            foreach (var d in details.OfType<Dictionary<string, object?>>())
            {
                foreach (var id in (Get(d, "appIDs") as List<object?> ?? new List<object?>()).OfType<string>()) Add(id, new string[0]);
                if (Get(d, "appID") is string legacy) Add(legacy, new string[0]);
            }
            return apps;
        }

        private static object? Get(Dictionary<string, object?> j, string key) => j.TryGetValue(key, out var v) ? v : null;
        private static DoctorFinding Ok(string id, string title) => new DoctorFinding(id, "ok", title, "");
        private static DoctorFinding Err(string id, string title, string fix) => new DoctorFinding(id, "error", title, fix);
    }
}
