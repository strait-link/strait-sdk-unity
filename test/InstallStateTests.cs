using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>
    /// Port of sdk-react-native/test/install-state.test.ts. Contract B6a (shared-spec ADR 0003): storage restored
    /// from a backup of another install (Android Auto Backup, a restored iPhone) must not skip the once-per-install
    /// deferred check.
    /// </summary>
    public class InstallStateTests
    {
        private const long TOld = 1_780_000_000_000;
        private const long TNew = 1_790_000_000_000;

        /// <summary>Memory store that records every write, in order.</summary>
        private sealed class Store : IKeyValueStore
        {
            public readonly Dictionary<string, string> Data;
            public readonly List<string> Writes = new List<string>();
            public Store(params (string k, string v)[] init) { Data = init.ToDictionary(x => x.k, x => x.v); }
            public string? this[string k] { get { lock (Data) return Data.TryGetValue(k, out var v) ? v : null; } }
            public Task<string?> GetItemAsync(string key) => Task.FromResult(this[key]);
            public Task SetItemAsync(string key, string value)
            {
                lock (Data) { Data[key] = value; Writes.Add(key + "=" + value); }
                return Task.CompletedTask;
            }
        }

        private static Store Restored() => new Store((StraitClient.DeferredFlag, "1"), (StraitClient.InstallTimeKey, TOld.ToString()));

        private static async Task<List<LinkEvent>> Launch(Store s, RouteEngine e, Func<long?>? installTime, string? initialUrl = null)
        {
            if (!e.Routes.ContainsKey("/v1/open")) e.Routes["/v1/open"] = 202;
            if (!e.Routes.ContainsKey("/v1/match")) e.Routes["/v1/match"] = 200;
            var strait = new StraitClient(new StraitConfig
            {
                PublishableKey = "st_pub_test_x", Endpoint = "https://links.test", Storage = s, Platform = "ios", Transport = e,
                Clock = () => 1_800_000_000_000, InstallTime = installTime,
            });
            var events = new List<LinkEvent>();
            strait.OnLink += ev => events.Add(ev);
            await strait.Start(initialUrl);
            await RouteEngine.Settle();
            return events;
        }

        [Fact]
        public void FirstLaunchState_NormalLaunch_SdkUpgrade_RestoredStorage_FirstLaunch()
        {
            void Is(FirstLaunchStateResult r, bool first, bool clear, string? store)
            {
                Assert.Equal(first, r.FirstLaunch);
                Assert.Equal(clear, r.ClearFlag);
                Assert.Equal(store, r.StoreInstallTime);
            }
            Is(StraitCore.FirstLaunchState("1", TNew.ToString(), TNew), false, false, null);
            Is(StraitCore.FirstLaunchState("1", null, TNew), false, false, TNew.ToString());
            Is(StraitCore.FirstLaunchState("1", TOld.ToString(), TNew), true, true, TNew.ToString());
            Is(StraitCore.FirstLaunchState(null, null, TNew), true, false, TNew.ToString());
            Is(StraitCore.FirstLaunchState("1", TOld.ToString(), null), false, false, null);
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.5, 1e300 })
                Is(StraitCore.FirstLaunchState("1", TOld.ToString(), bad), false, false, null);
        }

        [Fact]
        public async Task RestoredStorage_TheDeferredCheckRunsAgainAndCountsTheInstall_Once()
        {
            var s = Restored();
            var e = new RouteEngine();
            var events = await Launch(s, e, () => TNew);
            var match = Assert.Single(e.Of("/v1/match"));
            Assert.Matches("^o_", match.GetProperty("openId").GetString());
            Assert.Equal(new[] { "deferred" }, events.Select(x => x.Kind).ToArray());
            Assert.Equal("1", s[StraitClient.DeferredFlag]);
            Assert.Equal(TNew.ToString(), s[StraitClient.InstallTimeKey]);
            // The flag is cleared first, then the new time written.
            Assert.Equal(new[] { StraitClient.DeferredFlag + "=", StraitClient.InstallTimeKey + "=" + TNew }, s.Writes.Take(2).ToArray());
            // and only once: the next launch is a normal one
            await Launch(s, e, () => TNew);
            Assert.Single(e.Of("/v1/match"));
        }

        [Fact]
        public async Task RestoredStorage_LaunchedByALink_TheOpenReportSaysFirstLaunch()
        {
            var s = Restored();
            var e = new RouteEngine();
            await Launch(s, e, () => TNew, "straitlink://shop.example/p/1");
            Assert.Empty(e.Of("/v1/match"));
            Assert.True(Assert.Single(e.Of("/v1/open")).GetProperty("firstLaunch").GetBoolean());
            Assert.Equal("1", s[StraitClient.DeferredFlag]);
        }

        [Fact]
        public async Task AppUpdatedFromAnSdkWithoutInstallTime_AdoptsTheTime_NoNewInstall()
        {
            var s = new Store((StraitClient.DeferredFlag, "1"));
            var e = new RouteEngine();
            Assert.Empty(await Launch(s, e, () => TNew));
            Assert.Empty(e.Of("/v1/match"));
            Assert.Equal(TNew.ToString(), s[StraitClient.InstallTimeKey]);
        }

        [Fact]
        public async Task RestoredStorage_OfflineFirstLaunch_TheFlagStaysCleared_SoTheNextLaunchRetries()
        {
            var s = Restored();
            var offline = new RouteEngine();
            offline.Routes["/v1/match"] = RouteEngine.Offline;
            await Launch(s, offline, () => TNew);
            Assert.Equal("", s[StraitClient.DeferredFlag]);
            var e = new RouteEngine();
            await Launch(s, e, () => TNew);
            Assert.Single(e.Of("/v1/match"));
            Assert.Equal("1", s[StraitClient.DeferredFlag]);
        }

        [Fact]
        public async Task InstallTimeMissingThrowingOrInvalid_BehavesAsBefore_NoReset()
        {
            var cases = new Func<long?>?[] { null, () => throw new InvalidOperationException("no package info"), () => null, () => 0, () => -5 };
            foreach (var installTime in cases)
            {
                var s = Restored();
                var e = new RouteEngine();
                await Launch(s, e, installTime);
                Assert.Empty(e.Of("/v1/match"));
                Assert.Equal(TOld.ToString(), s[StraitClient.InstallTimeKey]);
                Assert.Equal("1", s[StraitClient.DeferredFlag]);
            }
        }

        [Fact]
        public async Task AFreshInstallStoresItsInstallTimeWithTheFlag()
        {
            var s = new Store();
            var e = new RouteEngine();
            await Launch(s, e, () => TNew);
            Assert.Single(e.Of("/v1/match"));
            Assert.Equal(TNew.ToString(), s[StraitClient.InstallTimeKey]);
            Assert.Equal("1", s[StraitClient.DeferredFlag]);
        }

        [Fact]
        public async Task UnreadableStorage_NeverAJumpOnEveryLaunch()
        {
            var e = new RouteEngine();
            var strait = new StraitClient(new StraitConfig
            {
                PublishableKey = "st_pub_test_x", Endpoint = "https://links.test", Storage = new Broken(), Platform = "ios", Transport = e,
                InstallTime = () => TNew,
            });
            await strait.Start(null);
            await RouteEngine.Settle();
            Assert.Empty(e.Of("/v1/match"));
        }

        private sealed class Broken : IKeyValueStore
        {
            public Task<string?> GetItemAsync(string key) => throw new InvalidOperationException("locked");
            public Task SetItemAsync(string key, string value) => throw new InvalidOperationException("locked");
        }
    }
}
