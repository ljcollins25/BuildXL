// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Text;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;

namespace BuildXL.Cache.ContentStore.Extensions
{
    /// <summary>
    ///     Useful extensions to standard string class.
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        ///     Create a stream for the given string.
        /// </summary>
        public static Stream ToUTF8Stream(this string value)
        {
            Contract.Requires(value != null);
            return new MemoryStream(Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        ///     Serialize an object instance to JSON.
        /// </summary>
        public static string SerializeToJSON<T>(this T obj)
        {
            using (var stream = new MemoryStream())
            {
                obj.SerializeToJSON(stream);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// Returns null if the string is empty otherwise returns the original string.
        /// </summary>
        public static string? NullIfEmpty(this string s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>
        /// Replaces the substring ignoring case
        /// </summary>
        public static string ReplaceIgnoreCase(this string str, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(str) || string.IsNullOrEmpty(oldValue))
            {
                return str;
            }

            int startIndex = 0;
            int replaceIndex;

            while ((replaceIndex = str.IndexOf(oldValue, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                str = str.Remove(replaceIndex, oldValue.Length);
                str = str.Insert(replaceIndex, newValue);
                startIndex = replaceIndex += newValue.Length;
            }

            return str;
        }
    }
}
