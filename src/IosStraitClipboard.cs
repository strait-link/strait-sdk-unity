using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Strait
{
    /// <summary>
    /// The iPhone clipboard for the paste handoff (contract B19), backed by Plugins/iOS/StraitClipboard.mm:
    /// <see cref="HasProbableWebUrlAsync"/> uses UIPasteboard detectPatterns (probableWebURL), which shows no prompt
    /// (iOS 15+; false on older iOS); <see cref="ReadTextAsync"/> reads UIPasteboard.general.string, which shows iOS's
    /// "Allow Paste" prompt. Outside an iOS player build (Editor, Android, tests) it never touches any clipboard:
    /// detection answers false and reading answers null. The SDK only calls it on the once-per-install check when
    /// device matching found nothing and the engine's /v1/match reply said the workspace turned on Paste handoff
    /// (Dashboard Settings → iPhone installs), or from <see cref="StraitClient.ClaimHandoff"/>.
    /// </summary>
    public sealed class IosStraitClipboard : IStraitClipboard
    {
        /// <summary>The default clipboard: this class in an iOS player build, else null (no clipboard).</summary>
        public static IStraitClipboard? Default
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return Instance;
#else
                return null;
#endif
            }
        }

#if UNITY_IOS && !UNITY_EDITOR
        private static readonly IosStraitClipboard Instance = new IosStraitClipboard();
        private delegate void DetectCallback(int requestId, int probable);
        private static readonly DetectCallback OnDetectDelegate = OnDetect;
        private static readonly Dictionary<int, TaskCompletionSource<bool>> Pending = new Dictionary<int, TaskCompletionSource<bool>>();
        private static int _nextId;

        [DllImport("__Internal")] private static extern void StraitClipboard_DetectProbableWebURL(int requestId, DetectCallback callback);
        [DllImport("__Internal")] private static extern string? StraitClipboard_ReadText();

        // IL2CPP needs reverse-P/Invoke callbacks to be static and marked with an attribute of this name.
        [Strait.Native.MonoPInvokeCallback(typeof(DetectCallback))]
        private static void OnDetect(int requestId, int probable)
        {
            TaskCompletionSource<bool>? tcs;
            lock (Pending)
            {
                if (!Pending.TryGetValue(requestId, out tcs)) return;
                Pending.Remove(requestId);
            }
            tcs.TrySetResult(probable != 0);
        }

        public Task<bool> HasProbableWebUrlAsync()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (Pending) { id = ++_nextId; Pending[id] = tcs; }
            try { StraitClipboard_DetectProbableWebURL(id, OnDetectDelegate); }
            catch
            {
                lock (Pending) Pending.Remove(id);
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        public Task<string?> ReadTextAsync()
        {
            try { return Task.FromResult(StraitClipboard_ReadText()); }
            catch { return Task.FromResult<string?>(null); }
        }
#else
        public Task<bool> HasProbableWebUrlAsync() => Task.FromResult(false);
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
#endif
    }
}

#if UNITY_IOS && !UNITY_EDITOR
namespace Strait.Native
{
    /// <summary>IL2CPP recognises reverse-P/Invoke callbacks by this attribute's name (the asmdef has no UnityEngine reference).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type type) { Type = type; }
        public Type Type { get; }
    }
}
#endif
