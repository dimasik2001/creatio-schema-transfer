// Polyfills so cst's source (written for net10) compiles and runs on .NET Framework 4.8.
// Only compiled into the net48 library build (guarded by the NETFRAMEWORK_POLYFILL constant).
#if NETFRAMEWORK_POLYFILL
using System.Collections.Generic;

namespace System.Runtime.CompilerServices
{
    /// <summary>Required by the compiler for `init` setters and records on net48.</summary>
    internal static class IsExternalInit { }

    /// <summary>Required by the compiler for `required` members on net48.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Lets a constructor declare it sets all required members (net48 polyfill).</summary>
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}

namespace System.Collections.Generic
{
    internal static class DictionaryPolyfill
    {
        // Concrete overload is most specific, so a Dictionary receiver is unambiguous.
        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key)
            => d.TryGetValue(key, out var v) ? v : default;
        public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> d, TKey key)
            => d.TryGetValue(key, out var v) ? v : default;
        public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> d, TKey key)
            => d.TryGetValue(key, out var v) ? v : default;
    }

    /// <summary>KeyValuePair deconstruction (foreach (var (k,v) in dict)) on net48.</summary>
    internal static class KeyValuePairPolyfill
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kvp, out TKey key, out TValue value)
        {
            key = kvp.Key;
            value = kvp.Value;
        }
    }

    /// <summary>Dictionary.TryAdd on net48.</summary>
    internal static class TryAddPolyfill
    {
        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, TValue value)
        {
            if (d.ContainsKey(key)) return false;
            d.Add(key, value);
            return true;
        }
    }
}

namespace System
{
    /// <summary>Minimal System.Index polyfill so range expressions (s[2..]) compile on net48.</summary>
    internal readonly struct Index
    {
        private readonly int _value;
        public Index(int value, bool fromEnd = false) => _value = fromEnd ? ~value : value;
        public static implicit operator Index(int value) => new Index(value);
        public int GetOffset(int length) => _value < 0 ? length + _value + 1 : _value;
    }

    /// <summary>Minimal System.Range polyfill so range expressions compile on net48.</summary>
    internal readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new Range(start, new Index(0, true));
        public static Range EndAt(Index end) => new Range(new Index(0, false), end);
        public static Range All => new Range(new Index(0, false), new Index(0, true));
    }
}
#endif
