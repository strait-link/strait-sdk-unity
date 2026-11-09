using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Strait;
using Xunit;

namespace Strait.Tests
{
    /// <summary>
    /// Port of sdk-react-native/test/user-id.test.ts. Contract B27 (shared-spec ADR 0009, engine ADR 0110): SetUserId(id)
    /// after the game's own sign-in; later events carry it on the app.event webhook. Emails and phone numbers are hashed
    /// on the device.
    /// </summary>
    public class UserIdTests
    {
        private const long T = 1_800_000_000_000;

        private static string Sha(string s) =>
            string.Concat(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));

        private static async Task<(StraitClient Strait, RouteEngine Engine, IKeyValueStore Store)> Make(int eventStatus = 202,
            IKeyValueStore? store = null, bool start = true)
        {
            var e = new RouteEngine();
            e.Routes["/v1/event"] = eventStatus;
            e.Routes["/v1/open"] = 202;
            var s = store ?? new MemoryKeyValueStore();
            await s.SetItemAsync(StraitClient.DeferredFlag, "1");
            var strait = new StraitClient(new StraitConfig
            {
                PublishableKey = "st_pub_test_x", Endpoint = "https://links.test", Storage = s, Platform = "android", Transport = e, Clock = () => T,
            });
            if (start)
            {
                await strait.Start(null);
                await RouteEngine.Settle();
            }
            return (strait, e, s);
        }

        private static bool Has(JsonElement o, string name) => o.TryGetProperty(name, out _);

        [Fact]
        public void Field_KeepsAnOpaqueId_HashesEmailAndPhone_DropsBadValues()
        {
            var opaque = StraitCore.UserIdField("  user_4821 ")!;
            Assert.Equal("user_4821", opaque.UserId);
            Assert.False(opaque.UserIdHashed);
            var email = StraitCore.UserIdField(" Priya@Example.COM ")!;
            Assert.Equal(Sha("priya@example.com"), email.UserId);
            Assert.True(email.UserIdHashed);
            var phone = StraitCore.UserIdField("+91 98765-43210")!;
            Assert.Equal(Sha("919876543210"), phone.UserId);
            Assert.True(phone.UserIdHashed);
            foreach (var bad in new[] { null, "", "   ", new string('x', StraitCore.UserIdMax + 1), "a\nb" })
                Assert.Null(StraitCore.UserIdField(bad));
            Assert.Equal(new string('x', StraitCore.UserIdMax), StraitCore.UserIdField(new string('x', StraitCore.UserIdMax))!.UserId);
        }

        [Fact]
        public async Task EventsAfterSetUserIdCarryIt_NullStopsIt()
        {
            var (strait, e, _) = await Make();
            await strait.TrackEvent("open_cart");
            strait.SetUserId("user_4821");
            await strait.TrackEvent("purchase");
            strait.SetUserId(null);
            await strait.TrackEvent("after_logout");
            var ev = e.Of("/v1/event");
            Assert.False(Has(ev[0], "userId"));
            Assert.Equal("user_4821", ev[1].GetProperty("userId").GetString());
            Assert.False(ev[1].GetProperty("userIdHashed").GetBoolean());
            Assert.False(Has(ev[2], "userId"));
            Assert.False(Has(ev[2], "userIdHashed"));
            Assert.All(e.Calls.Where(c => c.Path != "/v1/event"), c => Assert.False(Has(c.Body, "userId")));
        }

        [Fact]
        public async Task AnEmailNeverLeavesTheDevice_OnlyItsHashIsSentAndQueued()
        {
            var (strait, e, s) = await Make(RouteEngine.Offline);
            strait.SetUserId("Priya@Example.com");
            Assert.False(await strait.TrackEvent("purchase"));
            var sent = e.Of("/v1/event")[0];
            Assert.Equal(Sha("priya@example.com"), sent.GetProperty("userId").GetString());
            Assert.True(sent.GetProperty("userIdHashed").GetBoolean());
            Assert.DoesNotContain("Priya", sent.GetRawText());
            await RouteEngine.Settle();
            var queued = await s.GetItemAsync(StraitClient.QueueKey) ?? "";
            Assert.Contains(Sha("priya@example.com"), queued);
            Assert.DoesNotContain("priya", queued, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TheIdAtTrackTimeIsTheOneSent_EvenWhenSentLater()
        {
            var (strait, e, s) = await Make(RouteEngine.Offline);
            strait.SetUserId("user_1");
            await strait.TrackEvent("purchase");
            strait.SetUserId("user_2");
            await RouteEngine.Settle();
            var queued = JsonDocument.Parse(await s.GetItemAsync(StraitClient.QueueKey) ?? "[]").RootElement[0];
            Assert.Equal("user_1", queued.GetProperty("userId").GetString());
            e.Routes["/v1/event"] = 202;
            await strait.FlushOpenReports();
            Assert.Equal("user_1", e.Of("/v1/event").Last().GetProperty("userId").GetString());
        }

        [Fact]
        public async Task TheIdIsReadWhenTrackEventIsCalled_NotAfterItsFirstAwait()
        {
            // A slow storage read (the remembered tap) must not let a sign-out right after TrackEvent change the event's user.
            var store = new SlowStore();
            var (strait, e, _) = await Make(store: store);
            strait.SetUserId("user_1");
            store.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = strait.TrackEvent("purchase");
            strait.SetUserId(null);
            var b = strait.TrackEvent("after_logout");
            strait.SetUserId("other");
            store.Hold.SetResult(true);
            await Task.WhenAll(a, b);
            var ev = e.Of("/v1/event");
            Assert.Equal("user_1", ev.Single(x => x.GetProperty("event").GetString() == "purchase").GetProperty("userId").GetString());
            Assert.False(Has(ev.Single(x => x.GetProperty("event").GetString() == "after_logout"), "userId"));
        }

        /// <summary>Storage whose reads wait on <see cref="Hold"/> when it is set (a slow PlayerPrefs / disk).</summary>
        private sealed class SlowStore : IKeyValueStore
        {
            private readonly MemoryKeyValueStore _inner = new MemoryKeyValueStore();
            public TaskCompletionSource<bool>? Hold;
            public async Task<string?> GetItemAsync(string key)
            {
                var h = Hold;
                if (h != null) await h.Task;
                return await _inner.GetItemAsync(key);
            }
            public Task SetItemAsync(string key, string value) => _inner.SetItemAsync(key, value);
        }

        [Fact]
        public async Task OptingOutClearsIt_SetWhileOffIsIgnored_BackOnSendsNothingUntilSetAgain()
        {
            var (strait, e, _) = await Make();
            strait.SetUserId("user_4821");
            await strait.SetEnabled(false);
            strait.SetUserId("user_9");
            await strait.SetEnabled(true);
            await strait.TrackEvent("purchase");
            var ev = Assert.Single(e.Of("/v1/event"));
            Assert.False(Has(ev, "userId"));
        }

        [Fact]
        public async Task ABadValueClearsThePreviousId()
        {
            var (strait, e, _) = await Make();
            strait.SetUserId("user_1");
            strait.SetUserId(new string('x', StraitCore.UserIdMax + 1));
            await strait.TrackEvent("purchase");
            Assert.False(Has(e.Of("/v1/event")[0], "userId"));
        }

        [Fact]
        public async Task BeforeStart_AStoredOptOutDropsAnIdSetBeforeTheSwitchWasRead()
        {
            var store = new MemoryKeyValueStore();
            await store.SetItemAsync(StraitClient.EnabledKey, "0");
            var (strait, e, _) = await Make(store: store, start: false);
            strait.SetUserId("user_4821");
            await RouteEngine.Settle();
            await strait.SetEnabled(true);
            await strait.TrackEvent("purchase");
            var ev = Assert.Single(e.Of("/v1/event"));
            Assert.False(Has(ev, "userId"));
        }
    }
}
