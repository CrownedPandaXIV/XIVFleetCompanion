using System;
using System.Collections.Generic;

namespace XIVFleetCompanion
{
    /// <summary>
    /// One value worked out from some inputs, kept until an input changes: for lists a page would otherwise filter,
    /// look up and sort on every frame. Inputs are compared as they are: a list read from the database by reference (a
    /// new read is a new list), text, numbers and flags by value.
    /// </summary>
    public sealed class Memo<T>
    {
        private object?[]? inputs;
        private T value = default!;

        /// <summary>The value for these inputs: kept from before when they're the same, else made again.</summary>
        public T Get(Func<T> make, params object?[] now)
        {
            if (inputs != null && Same(inputs, now)) return value;
            value = make();
            inputs = now;
            return value;
        }

        /// <summary>Made again at the next Get.</summary>
        public void Forget() => inputs = null;

        private static bool Same(object?[] before, object?[] now)
        {
            if (before.Length != now.Length) return false;
            for (var i = 0; i < now.Length; i++)
            {
                var (a, b) = (before[i], now[i]);
                if (a is string or ValueType || b is string or ValueType) { if (!Equals(a, b)) return false; }
                else if (!ReferenceEquals(a, b)) return false;
            }
            return true;
        }
    }

    /// <summary>A value worked out at most every so often (things read from Windows: saved logins, network adapters, files).</summary>
    public sealed class Every<T>
    {
        private readonly TimeSpan every;
        private DateTime at = DateTime.MinValue;
        private T value = default!;
        public Every(TimeSpan every) => this.every = every;

        public T Get(Func<T> make)
        {
            var now = DateTime.UtcNow;
            if (now - at < every && now >= at) return value;
            value = make();
            at = now;
            return value;
        }

        /// <summary>Worked out again at the next Get (after something that changes it).</summary>
        public void Forget() => at = DateTime.MinValue;
    }
}
