// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;

namespace BuildXL.Scheduler
{
    /// <summary>
    /// Filters the directory members by using the regex
    /// </summary>
    public class RegexDirectoryMembershipFilter : DirectoryMembershipFilter
    {
        /// <nodoc/>
        public static readonly ObjectCache<string, RegexDirectoryMembershipFilter> RegexCache = new(1000);

        /// <summary>
        /// Regex that allows all.
        /// </summary>
        public const string AllowAllRegex = "^.*$";

        /// <summary>
        /// Win32 legacy regex that allows all, i.e., '*.*';
        /// </summary>
        public const string Win32LegacyAllowAllRegex = @"^(.*\..*)$";

        private readonly Regex m_regex;

        /// <nodoc/>
        private RegexDirectoryMembershipFilter(string pattern)
        {
            m_regex = new Regex(pattern, RegexOptions.IgnoreCase);
        }

        /// <inheritdoc/>
        public override bool Include(PathAtom fileName, string fileNameStr)
        {
            return m_regex.IsMatch(fileNameStr);
        }

        /// <summary>
        /// Convert the wildcard search pattern to regex
        /// </summary>
        public static string ConvertWildcardsToRegex(params string[] patterns)
        {
            var sb = new StringBuilder();
            bool isAdded = false;
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern) || string.Equals(pattern, "*"))
                {
                    // Then, there is no need to use search pattern regex. We can just use AllowAllFilter
                    return AllowAllRegex;
                }

                if (OperatingSystemHelper.IsWindowsOS && string.Equals(pattern, "*.*"))
                {
                    // On Windows, the pattern "*.*" matches all (legacy behavior), so we simply returns the regex that matches all.
                    return AllowAllRegex;
                }

                sb.Append(isAdded ? "|" : string.Empty);
                var regexStr = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
                sb.Append("(" + regexStr + ")");
                isAdded = true;
            }

            if (!isAdded)
            {
                return null;
            }

            if (patterns.Length > 1)
            {
                return "^(" + sb.ToString() + ")$";
            }
            else
            {
                return "^" + sb.ToString() + "$";
            }
        }

        /// <summary>
        /// Create a directory membership filter based on the regex filter given
        /// </summary>
        public static DirectoryMembershipFilter Create(string enumeratePatternRegex)
        {
            if (enumeratePatternRegex == AllowAllRegex
                || (OperatingSystemHelper.IsWindowsOS && enumeratePatternRegex == Win32LegacyAllowAllRegex))
            {
                // If the regex allows all, then returns an efficient AllowAllFilter.
                return AllowAllFilter;
            }

            RegexDirectoryMembershipFilter result;
            if (!RegexCache.TryGetValue(enumeratePatternRegex, out result))
            {
                result = new RegexDirectoryMembershipFilter(enumeratePatternRegex);
                RegexCache.AddItem(enumeratePatternRegex, result);
            }

            return result;
        }
    }
}
