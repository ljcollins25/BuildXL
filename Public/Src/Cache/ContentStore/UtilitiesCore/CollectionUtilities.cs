// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BuildXL.Cache.ContentStore.UtilitiesCore.Internal
{
    /// <summary>
    /// Custom implementation of Xunit collection runner.
    /// </summary>
    public static class CollectionUtilities
    {
        /// <summary>
        /// Returns an empty instance of <see cref="List{T}"/>.
        /// </summary>
        public static List<T> EmptyList<T>() => Empty<T>.EmptyList;

        /// <summary>
        /// Returns an empty instance of <typeparamref name="T"/>[].
        /// </summary>
        public static T[] EmptyArray<T>() => Empty<T>.EmptyArray;

        private static class Empty<T>
        {
            public static readonly List<T> EmptyList = new List<T>();

            public static readonly T[] EmptyArray = new T[] { };
        }

        private static class Empty<TKey, TValue>
        {
            public static readonly Dictionary<TKey, TValue> EmptyDictionary = new Dictionary<TKey, TValue>();
        }

        /// <summary>
        /// Returns an empty instance of <see cref="IReadOnlyDictionary{TKey,TValue}"/>
        /// </summary>
        public static IReadOnlyDictionary<TKey, TValue> EmptyDictionary<TKey, TValue>() => Empty<TKey, TValue>.EmptyDictionary;

        /// <summary>
        /// Allows deconstructing a key value pair to a tuple
        /// </summary>
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> source, out TKey key, out TValue value)
        {
            key = source.Key;
            value = source.Value;
        }

        /// <summary>
        /// Allows adding enumerable to collection using collection initializer syntax
        /// </summary>
        public static void Add<T>(this ICollection<T> collection, IEnumerable<T> values)
        {
            foreach (var value in values)
            {
                collection.Add(value);
            }
        }

        /// <summary>
        /// Allows adding enumerable to collection using collection initializer syntax
        /// </summary>
        public static void Add<TKey, TValue>(this IDictionary<TKey, TValue> map, AddOrSetEntries<TKey, TValue> values)
        {
            foreach (var entry in values.Entries)
            {
                map[entry.Key] = entry.Value;
            }
        }

        /// <summary>
        /// Clears the dictionary
        /// </summary>
        public static void Add<T>(this ICollection<T> collection, ClearEntries clear)
        {
            if (clear.ShouldClear)
            {
                collection.Clear();
            }
        }

        /// <summary>
        /// Wraps key value pairs in an <see cref="AddOrSetEntries{TKey, TValue}"/> for use with collection initializer.
        /// </summary>
        public static AddOrSetEntries<TKey, TValue> ToAddOrSetEntries<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> entries)
        {
            return new(entries);
        }

        /// <summary>
        /// For use with <see cref="Add{TKey, TValue}(IDictionary{TKey, TValue}, AddOrSetEntries{TKey, TValue})"/> in collection initializer
        /// to allow setting items from a collection instead of add with throws if there is a duplicate.
        /// </summary>
        public record struct AddOrSetEntries<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> Entries);

        /// <summary>
        /// Compare two operands and returns true if two instances are equivalent.
        /// </summary>
        public static bool IsCompareEquals<T>(this T x1, T x2, out int compareResult, bool greatestFirst = false)
            where T : IComparable<T>
        {
            compareResult = (int)Order(x1, x2, greatestFirst: greatestFirst);
            return compareResult == 0;
        }

        /// <summary>
        /// Compare two operands and returns true if first instance is greater.
        /// </summary>
        public static bool IsGreaterThan<T>(this T x1, T x2)
            where T : IComparable<T>
        {
            return Order(x1, x2, greatestFirst: true) == OrderResult.PreferFirst;
        }

        /// <summary>
        /// Compare two operands and returns true if first instance is less.
        /// </summary>
        public static bool IsLessThan<T>(this T x1, T x2)
            where T : IComparable<T>
        {
            return Order(x1, x2, greatestFirst: false) == OrderResult.PreferFirst;
        }

        /// <summary>
        /// The result of comparing two values
        /// </summary>
        public enum OrderResult
        {
            /// <summary>
            /// The values are equal
            /// </summary>
            Equal = 0,

            /// <summary>
            /// Prefer the first value
            /// </summary>
            PreferFirst = -1,

            /// <summary>
            /// Prefer the second value
            /// </summary>
            PreferSecond = 1,
        }

        /// <summary>
        /// Comverts a integer comparison result (from <see cref="IComparer{T}.Compare(T, T)"/> or <see cref="IComparer{T}.Compare(T, T)"/>) to
        /// an <see cref="OrderResult"/> value
        /// </summary>
        public static OrderResult ToOrderResult(int compareResult)
        {
            if (compareResult == 0)
            {
                return OrderResult.Equal;
            }
            else
            {
                return compareResult < 0 ? OrderResult.PreferFirst : OrderResult.PreferSecond;
            }
        }

        /// <summary>
        /// Comverts a integer comparison result (from <see cref="IComparer{T}.Compare(T, T)"/> or <see cref="IComparer{T}.Compare(T, T)"/>) to
        /// an <see cref="OrderResult"/> value or null if equivalent
        /// </summary>
        public static OrderResult? ToChainOrderResult(int compareResult)
        {
            if (compareResult == 0)
            {
                return null;
            }
            else
            {
                return compareResult < 0 ? OrderResult.PreferFirst : OrderResult.PreferSecond;
            }
        }

        /// <nodoc />
        public static int ToCompareResult(this OrderResult orderResult) => (int)orderResult;

        /// <nodoc />
        public static int ToCompareResult(this OrderResult? orderResult) => (int)(orderResult ?? OrderResult.Equal);

        /// <summary>
        /// Compare two operands and returns order.
        /// </summary>
        public static OrderResult Order<T>(this T x1, T x2, bool greatestFirst = false)
            where T : IComparable<T>
        {
            return ToOrderResult(greatestFirst
                ? x2.CompareTo(x1)
                : x1.CompareTo(x2));
        }

        /// <summary>
        /// Compare two operands and returns order or null if equivalent.
        /// </summary>
        public static int? ChainCompareTo<T>(this T x1, T x2, bool greatestFirst = false)
            where T : IComparable<T>
        {
            var comparisonResult = x1.CompareTo(x2);
            return comparisonResult == 0 ? null : comparisonResult;
        }
    }

    /// <summary>
    /// Sentinel for clearing entries in a collection
    /// </summary>
    public record struct ClearEntries(bool ShouldClear = true);
}
