using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Strait;
using Xunit;
using Xunit.Abstractions;

namespace Strait.Tests
{
    /// <summary>Every row of shared-spec ADR 0002 (contract B23), word for word; port of sdk-react-native/test/doctor.test.ts.</summary>
    public class DoctorTests
    {
        private const string PK = "st_pub_live_abc123";
        private const string EP = "https://acme.strait.link";
        private const string FP = "FA:C6:17:45:DC:09:03:78:6F:B9:ED:E6:2A:96:2B:39:9F:73:48:F0:BB:6F:89:9B:83:32:66:75:91:03:3B:9C";
        private const string OTHER_FP = "6B:A3:E4:40:DF:12:16:87:B1:C6:98:7F:E1:9A:4E:28:CF:96:A6:61:38:40:28:00:03:90:56:EA:42:6F:7C:18";

        private static string Assetlinks(params (string Pkg, string[] Fps)[] apps) =>
            JsonSerializer.Serialize(apps.Select(a => new Dictionary<string, object>
            {
                ["relation"] = new[] { "delegate_permission/common.handle_all_urls" },
                ["target"] = new Dictionary<string, object> { ["namespace"] = "android_app", ["package_name"] = a.Pkg, ["sha256_cert_fingerprints"] = a.Fps },
            }));

        private static string Aasa(params string[] appIDs) =>
            JsonSerializer.Serialize(new { applinks = new { apps = new string[0], details = appIDs.Length > 0 ? new object[] { new { appIDs, components = new[] { new Dictionary<string, string> { ["/"] = "/*" } } } } : new object[0] } });

        private static DoctorFile Ok200(string body) => new DoctorFile(200, body);
        private static string[] Ids(DoctorReport r) => r.Findings.Select(f => f.Id).ToArray();
        private static DoctorFinding? ById(DoctorReport r, string id) => r.Findings.FirstOrDefault(f => f.Id == id);
        private static DoctorFinding F(string id, string severity, string title, string fix = "") => new DoctorFinding(id, severity, title, fix);

        private static readonly DoctorFinding KeyOk = F("key.ok", "ok", "Publishable key is set");

        // ------------------------------------------------------------------ key

        private static DoctorFinding Key(string k) => Doctor.Findings(k, "not a url", "android", null, null, null).Findings[0];

        [Fact]
        public void KeyOk_ForStPub() => Assert.Equal(KeyOk, Key(PK));

        [Theory]
        [InlineData("st_live_x")]
        [InlineData("st_test_x")]
        public void KeySecret(string k) => Assert.Equal(
            F("key.secret", "error", "This is a secret key, not the publishable key",
                "Use the publishable key (st_pub_…) from Dashboard → Get started, and revoke this secret key in Dashboard → Settings → Secret keys: it has been inside your app."),
            Key(k));

        [Theory]
        [InlineData("")]
        [InlineData("bk_pub_x")]
        [InlineData("pub_live_x")]
        public void KeyInvalid(string k) => Assert.Equal(
            F("key.invalid", "error", "Not a publishable key", "Copy the publishable key (st_pub_…) from Dashboard → Get started."),
            Key(k));

        [Fact]
        public void KeyInvalid_ForNull() => Assert.Equal("key.invalid", Doctor.Findings(null, EP, "ios", null, null, null).Findings[0].Id);

        // ------------------------------------------------------------------ endpoint

        [Theory]
        [InlineData("http://acme.strait.link")]
        [InlineData("acme.strait.link")]
        [InlineData("https://")]
        [InlineData("https:///x")]
        [InlineData("")]
        public void EndpointInvalid_NoHostChecks(string endpoint)
        {
            var r = Doctor.Findings(PK, endpoint, "ios", "com.acme.app", null, new DoctorFile(200, Aasa("T.com.acme.app")));
            Assert.Equal(new[]
            {
                KeyOk,
                F("endpoint.invalid", "error", "The endpoint isn't an https link domain",
                    "Set endpoint to your link domain from Dashboard → Get started, e.g. https://<handle>.strait.link."),
            }, r.Findings);
            Assert.False(r.Ok);
        }

        [Fact]
        public void NotFetched_KeyAndEndpointOnly()
        {
            var r = Doctor.Findings(PK, EP, "android", "com.acme.app", null, null);
            Assert.Equal(new[] { "key.ok" }, Ids(r));
            Assert.True(r.Ok);
        }

        // ------------------------------------------------------------------ host checks, both platforms

        private static string FileName(string platform) => platform == "android" ? "assetlinks.json" : "apple-app-site-association";
        private static DoctorReport Host(string platform, DoctorFile f) => Doctor.Findings(PK, EP + "/", platform, null, null, f);

        [Theory]
        [InlineData("android")]
        [InlineData("ios")]
        public void HostUnreachable(string platform)
        {
            var r = Host(platform, new DoctorFile(null, null));
            Assert.Equal(new[] { "key.ok", "host.unreachable" }, Ids(r));
            Assert.Equal(F("host.unreachable", "error", "Can't reach acme.strait.link",
                "Check that endpoint is your link domain (Dashboard → Get started) and that this device is online."), r.Findings[1]);
            Assert.False(r.Ok);
        }

        [Theory]
        [InlineData("android", 404, "")]
        [InlineData("android", 200, "<html>hi</html>")]
        [InlineData("android", 200, "{}")]
        [InlineData("android", 200, null)]
        [InlineData("ios", 404, "")]
        [InlineData("ios", 200, "<html>hi</html>")]
        [InlineData("ios", 200, "[]")]
        [InlineData("ios", 200, "{\"applinks\":{}}")]
        public void HostFileMissing(string platform, int status, string? body)
        {
            var r = Host(platform, new DoctorFile(status, body));
            Assert.Equal(new[] { "key.ok", "host.file_missing" }, Ids(r));
            Assert.Equal(F("host.file_missing", "error", $"acme.strait.link doesn't serve {FileName(platform)}",
                "Use your Strait link domain as endpoint (Dashboard → Get started); Strait serves this file there."), r.Findings[1]);
        }

        [Theory]
        [InlineData("android")]
        [InlineData("ios")]
        public void AppNone(string platform)
        {
            var r = Host(platform, Ok200(platform == "android" ? "[]" : Aasa()));
            Assert.Equal(F("app.none", "error", $"No {(platform == "android" ? "Android" : "iOS")} app is set up for acme.strait.link",
                "Add your app in Dashboard → Apps & stores."), r.Findings[1]);
            Assert.False(r.Ok);
        }

        [Fact]
        public void AppNone_AndroidIgnoresOtherNamespaces()
        {
            var web = "[{\"relation\":[\"x\"],\"target\":{\"namespace\":\"web\",\"site\":\"https://acme.example\"}}]";
            Assert.Equal("app.none", Host("android", Ok200(web)).Findings[1].Id);
        }

        // ------------------------------------------------------------------ Android app and certificate

        private static readonly string AndroidBody = Assetlinks(("com.acme.app", new[] { FP }), ("com.acme.other", new[] { OTHER_FP }));
        private static DoctorReport Android(string? appId, string? cert = null) => Doctor.Findings(PK, EP, "android", appId, cert, Ok200(AndroidBody));

        [Fact]
        public void Android_AppListed_CertListed_AnyFingerprintFormat()
        {
            var r = Android("com.acme.app", FP.ToLowerInvariant().Replace(":", ""));
            Assert.Equal(new[]
            {
                KeyOk,
                F("app.listed", "ok", "acme.strait.link lists com.acme.app"),
                F("cert.listed", "ok", "This build's signing certificate is listed"),
            }, r.Findings);
            Assert.True(r.Ok);
        }

        [Fact]
        public void Android_CertMissing_WhenSignedWithAnotherAppsFingerprint()
        {
            var r = Android("com.acme.app", OTHER_FP);
            Assert.Equal(F("cert.missing", "error", "This build's signing certificate isn't listed for com.acme.app",
                $"Add {OTHER_FP} in Dashboard → Apps & stores. Debug, release and Play App Signing keys each have their own fingerprint."),
                ById(r, "cert.missing"));
            Assert.False(r.Ok);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Android_NoCertCheck_WhenCertUnknown(string? cert) =>
            Assert.Equal(new[] { "key.ok", "app.listed" }, Ids(Android("com.acme.app", cert)));

        [Fact]
        public void Android_AppMissing_ListsWhatIsThere_NoCertCheck() => Assert.Equal(new[]
        {
            F("app.missing", "error", "acme.strait.link doesn't list com.acme.nope",
                "Add com.acme.nope in Dashboard → Apps & stores (listed now: com.acme.app, com.acme.other)."),
        }, Android("com.acme.nope", FP).Findings.Skip(1));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Android_AppUnknown_WarnKeepsOk(string? appId)
        {
            var r = Android(appId, FP);
            Assert.Equal(new[] { F("app.unknown", "warn", "acme.strait.link lists com.acme.app, com.acme.other", "Make sure your app is one of these.") }, r.Findings.Skip(1));
            Assert.True(r.Ok);
        }

        // ------------------------------------------------------------------ iOS app

        private static readonly string IosBody = Aasa("TEAM123456.com.acme.app", "TEAM123456.com.acme.clip");
        private static DoctorReport Ios(string? appId, string? cert = null) => Doctor.Findings(PK, EP, "ios", appId, cert, Ok200(IosBody));

        [Fact]
        public void Ios_AppListed_BundleSuffix_FullAppId_NoCertCheck()
        {
            var r = Ios("com.acme.app", FP);
            Assert.Equal(new[] { F("app.listed", "ok", "acme.strait.link lists TEAM123456.com.acme.app") }, r.Findings.Skip(1));
            Assert.True(r.Ok);
        }

        [Fact]
        public void Ios_PartialSuffixIsNotListed()
        {
            Assert.Null(ById(Ios("acme.app"), "app.missing")); // ".acme.app" is a real dot suffix
            Assert.Equal("acme.strait.link doesn't list me.app", ById(Ios("me.app"), "app.missing")?.Title);
        }

        [Fact]
        public void Ios_AppMissing() => Assert.Equal(
            F("app.missing", "error", "acme.strait.link doesn't list in.other.app",
                "Add in.other.app in Dashboard → Apps & stores (listed now: TEAM123456.com.acme.app, TEAM123456.com.acme.clip)."),
            ById(Ios("in.other.app"), "app.missing"));

        [Fact]
        public void Ios_AppUnknown() => Assert.Equal(
            new[] { F("app.unknown", "warn", "acme.strait.link lists TEAM123456.com.acme.app, TEAM123456.com.acme.clip", "Make sure your app is one of these.") },
            Ios(null).Findings.Skip(1));

        [Fact]
        public void Ios_ReadsOlderPathsStyle()
        {
            var old = "{\"applinks\":{\"apps\":[],\"details\":[{\"appID\":\"TEAM123456.com.acme.app\",\"paths\":[\"*\"]}]}}";
            Assert.Equal(new[] { "key.ok", "app.listed" }, Ids(Doctor.Findings(PK, EP, "ios", "com.acme.app", null, Ok200(old))));
        }

        // ------------------------------------------------------------------ client.Doctor()

        private sealed class FakeHost : IStraitTransport
        {
            public readonly List<(string Method, string Url)> Calls = new List<(string, string)>();
            public int Status = 200;
            public string Body = "";
            public bool Offline;
            public Task<(int Status, string Body)> SendAsync(string method, string url, string? jsonBody)
            {
                Calls.Add((method, url));
                if (Offline) throw new HttpRequestException("Network request failed");
                return Task.FromResult((Status, Body));
            }
        }

        private static StraitClient Make(string platform, IStraitTransport transport, string endpoint = EP) =>
            new StraitClient(new StraitConfig { PublishableKey = PK, Endpoint = endpoint, Platform = platform, Transport = transport });

        [Fact]
        public async Task Client_Android_OneGetOfAssetlinks()
        {
            var host = new FakeHost { Body = Assetlinks(("com.acme.app", new[] { FP })) };
            var r = await Make("android", host).Doctor("com.acme.app", FP, _ => { });
            Assert.Equal(new[] { ("GET", "https://acme.strait.link/.well-known/assetlinks.json") }, host.Calls);
            Assert.True(r.Ok);
            Assert.Equal(new[] { "key.ok", "app.listed", "cert.listed" }, Ids(r));
        }

        [Fact]
        public async Task Client_Ios_GetsAasa()
        {
            var host = new FakeHost { Body = Aasa("TEAM123456.com.acme.app") };
            var r = await Make("ios", host, EP + "/").Doctor("com.acme.app", log: _ => { });
            Assert.Equal(new[] { ("GET", "https://acme.strait.link/.well-known/apple-app-site-association") }, host.Calls);
            Assert.Equal(new[] { "key.ok", "app.listed" }, Ids(r));
        }

        [Fact]
        public async Task Client_NoAppId_AppUnknown()
        {
            var r = await Make("ios", new FakeHost { Body = Aasa("TEAM123456.com.acme.app") }).Doctor(log: _ => { });
            Assert.Equal(new[] { "key.ok", "app.unknown" }, Ids(r));
        }

        [Fact]
        public async Task Client_NeverThrows_FailingTransportIsUnreachable()
        {
            var r = await Make("android", new FakeHost { Offline = true }).Doctor("com.acme.app", log: _ => { });
            Assert.Equal(new[] { "key.ok", "host.unreachable" }, Ids(r));
            Assert.False(r.Ok);
        }

        [Fact]
        public async Task Client_NeverThrows_ThrowingLogger()
        {
            var r = await Make("android", new FakeHost { Status = 404 }).Doctor(log: _ => throw new InvalidOperationException("bad logger"));
            Assert.Equal(new[] { "key.ok", "host.file_missing" }, Ids(r));
        }

        [Fact]
        public async Task Client_InvalidEndpoint_NoRequest()
        {
            var host = new FakeHost();
            var r = await Make("android", host, "http://acme.strait.link").Doctor(log: _ => { });
            Assert.Empty(host.Calls);
            Assert.Equal(new[] { "key.ok", "endpoint.invalid" }, Ids(r));
        }

        [Fact]
        public async Task Client_OtherPlatform_NoRequest_KeyAndEndpointOnly()
        {
            var host = new FakeHost();
            var r = await Make("other", host).Doctor("com.acme.app", log: _ => { });
            Assert.Empty(host.Calls);
            Assert.Equal(new[] { "key.ok" }, Ids(r));
        }

        [Fact]
        public async Task Client_LogsEachFinding_WithPrefix()
        {
            var lines = new List<string>();
            await Make("android", new FakeHost { Status = 404 }).Doctor(log: lines.Add);
            Assert.Equal(new[]
            {
                "[Strait doctor] ok key.ok: Publishable key is set",
                "[Strait doctor] error host.file_missing: acme.strait.link doesn't serve assetlinks.json. Fix: Use your Strait link domain as endpoint (Dashboard → Get started); Strait serves this file there.",
            }, lines);
        }
    }

    /// <summary>
    /// Live proof against a real Strait link host, only when STRAIT_DOCTOR_LIVE is set (CI never calls Strait):
    /// STRAIT_DOCTOR_LIVE=https://strait-dev.strait.link dotnet test --filter DoctorLiveTests --logger "console;verbosity=detailed"
    /// </summary>
    public class DoctorLiveTests
    {
        private readonly ITestOutputHelper _out;
        public DoctorLiveTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public async Task LiveHost()
        {
            var endpoint = Environment.GetEnvironmentVariable("STRAIT_DOCTOR_LIVE");
            if (string.IsNullOrEmpty(endpoint)) return;
            async Task<DoctorReport> Run(string platform, string appId, string? cert = null)
            {
                var client = new StraitClient(new StraitConfig { PublishableKey = "st_pub_live_placeholder", Endpoint = endpoint, Platform = platform });
                _out.WriteLine($"== {platform} Doctor(\"{appId}\"{(cert == null ? "" : $", \"{cert}\"")})");
                var r = await client.Doctor(appId, cert, _out.WriteLine);
                _out.WriteLine($"ok={r.Ok}");
                return r;
            }
            Assert.Equal("app.listed", (await Run("android", "com.straitlink.app")).Findings[1].Id);
            Assert.Equal("cert.listed", (await Run("android", "com.straitlink.app", "FA:C6:17:45:DC:09:03:78:6F:B9:ED:E6:2A:96:2B:39:9F:73:48:F0:BB:6F:89:9B:83:32:66:75:91:03:3B:9C")).Findings[2].Id);
            Assert.Equal("app.listed", (await Run("ios", "in.straitlink.reference")).Findings[1].Id);
            Assert.Equal("app.missing", (await Run("android", "com.example.notlisted")).Findings[1].Id);
            Assert.Equal("app.missing", (await Run("ios", "com.example.notlisted")).Findings[1].Id);
        }
    }
}
