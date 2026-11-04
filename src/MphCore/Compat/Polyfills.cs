// netstandard2.1 polyfills for the .NET 8/9 APIs MphRead uses (Unity's Mono has none of these).
using System.Collections.Generic;
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute { public CompilerFeatureRequiredAttribute(string f) { } }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    internal sealed class CollectionBuilderAttribute : Attribute
    {
        public CollectionBuilderAttribute(Type builderType, string methodName) { }
    }
}
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
namespace System.Collections.Frozen
{
    public class FrozenDictionary<TKey, TValue> : Dictionary<TKey, TValue> where TKey : notnull
    {
        public FrozenDictionary(IDictionary<TKey, TValue> d) : base(d) { }
        public FrozenDictionary(IEqualityComparer<TKey>? c) : base(c) { }
        public FrozenDictionary() { }
        public static FrozenDictionary<TKey, TValue> Empty => new FrozenDictionary<TKey, TValue>();
    }
    public class FrozenSet<T> : HashSet<T>
    {
        public FrozenSet(IEnumerable<T> e) : base(e) { }
    }
    public static class FrozenExtensions
    {
        public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> e) where TKey : notnull
        {
            var d = new Dictionary<TKey, TValue>();
            foreach (KeyValuePair<TKey, TValue> kv in e) { d[kv.Key] = kv.Value; }
            return new FrozenDictionary<TKey, TValue>(d);
        }
        public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TSource, TKey, TValue>(this IEnumerable<TSource> s, Func<TSource, TKey> k, Func<TSource, TValue> v) where TKey : notnull
        {
            var d = new Dictionary<TKey, TValue>();
            foreach (TSource x in s) { d[k(x)] = v(x); }
            return new FrozenDictionary<TKey, TValue>(d);
        }
        public static FrozenDictionary<TKey, TSource> ToFrozenDictionary<TSource, TKey>(this IEnumerable<TSource> s, Func<TSource, TKey> k) where TKey : notnull
        {
            var d = new Dictionary<TKey, TSource>();
            foreach (TSource x in s) { d[k(x)] = x; }
            return new FrozenDictionary<TKey, TSource>(d);
        }
        public static FrozenSet<T> ToFrozenSet<T>(this IEnumerable<T> e) => new FrozenSet<T>(e);
    }
}

namespace System.IO
{
    public static class StreamPolyfills
    {
        public static void ReadExactly(this Stream s, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = s.Read(buffer.Slice(total));
                if (n <= 0) { throw new EndOfStreamException(); }
                total += n;
            }
        }
        public static void ReadExactly(this Stream s, byte[] buffer, int offset, int count) => ReadExactly(s, buffer.AsSpan(offset, count));
    }
}
