using System;
using System.Threading;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>The Sub Calculator's working out (Levelling Plan, Compare Builds, Build Finder, Route Finder) on a thread of
    /// its own below normal priority, so the game keeps its framerate while it runs. A cancelled job ends its task as
    /// cancelled.</summary>
    internal static class LowPriority
    {
        public static Task<T> Run<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { done.TrySetResult(work()); }
                catch (OperationCanceledException) { done.TrySetCanceled(); }
                catch (Exception ex) { done.TrySetException(ex); }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "XIV Fleet Companion: Sub Calculator",
            };
            thread.Start();
            return done.Task;
        }
    }
}
