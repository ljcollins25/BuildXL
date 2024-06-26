// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.Text.RegularExpressions;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// All values necessary to construct a regular expression
    /// </summary>
    public readonly struct RegexDescriptor : IEquatable<RegexDescriptor>
    {
        /// <summary>
        /// Adapted from Microsoft.BUild.Utilities.Core / CanonicalError.cs
        /// </summary>
        public const string DefaultWarningPattern = Warning.DefaultWarningPattern;

        private const RegexOptions DefaultOptions = RegexOptions.IgnoreCase;

        /// <summary>
        /// An invalid descriptor.
        /// </summary>
        public static readonly RegexDescriptor Invalid = default(RegexDescriptor);

        /// <summary>
        /// The regular expression pattern to match.
        /// </summary>
        public readonly StringId Pattern;

        /// <summary>
        /// A bitwise combination of the options that modify the regular expression;
        /// </summary>
        public readonly RegexOptions Options;

        /// <summary>
        /// Creates a new instance of this class
        /// </summary>
        /// <remarks>
        /// The <code>options</code> must not include <code>RegexOptions.Compiled</code>.
        /// The <code>PipRunner</code> will decide where the compile the regular expression.
        /// </remarks>
        public RegexDescriptor(StringId pattern, RegexOptions options)
        {
            Contract.Requires(pattern.IsValid);
            Contract.Requires((options & RegexOptions.Compiled) == 0);

            Pattern = pattern;
            Options = options;
        }

        /// <summary>
        /// Checks if this instance is valid.
        /// </summary>
        public bool IsValid => Pattern.IsValid;

        /// <summary>
        /// Creates the default regular expression descriptor (for warnings).
        /// </summary>
        public static RegexDescriptor CreateDefaultForWarnings(StringTable stringTable)
        {
            Contract.Requires(stringTable != null, "stringTable can't be null.");
            return new RegexDescriptor(
                StringId.Create(stringTable, DefaultWarningPattern),
                DefaultOptions);
        }

        /// <summary>
        /// Creates the default regular expression descriptor (for errors).
        /// Currently, it matches everything.
        /// </summary>
        public static RegexDescriptor CreateDefaultForErrors(StringTable stringTable)
        {
            Contract.Requires(stringTable != null, "stringTable can't be null.");
            return new RegexDescriptor(StringId.Create(stringTable, ".*"), DefaultOptions);
        }

        /// <summary>
        /// Checks if the given pattern and option match the default regex descriptor.
        /// </summary>
        public static bool IsDefault(string pattern, RegexOptions options)
        {
            return
                pattern == DefaultWarningPattern &&
                options == DefaultOptions;
        }

        #region Serialization
        internal void Serialize(BuildXLWriter writer)
        {
            Contract.Requires(writer != null);
            writer.Write(Pattern);
            if (Pattern.IsValid)
            {
                writer.WriteCompact(unchecked((int)Options));
            }
        }

        internal static RegexDescriptor Deserialize(BuildXLReader reader)
        {
            Contract.Requires(reader != null);
            StringId pattern = reader.ReadStringId();
            if (pattern.IsValid)
            {
                var options = (RegexOptions)reader.ReadInt32Compact();
                Contract.Assume((options & RegexOptions.Compiled) == 0);
                return new RegexDescriptor(pattern, options);
            }
            else
            {
                return default(RegexDescriptor);
            }
        }
        #endregion

        #region IEquatable<RegexDescriptor> implementation

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is RegexDescriptor && Equals((RegexDescriptor)obj);
        }

        /// <inheritdoc />
        public bool Equals(RegexDescriptor other)
        {
            return
                Pattern == other.Pattern &&
                Options == other.Options;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCodeHelper.Combine(Pattern.GetHashCode(), (int)Options);
        }

        /// <summary>
        /// Checks if two values are equal.
        /// </summary>
        public static bool operator ==(RegexDescriptor left, RegexDescriptor right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Checks if two values are different.
        /// </summary>
        public static bool operator !=(RegexDescriptor left, RegexDescriptor right)
        {
            return !left.Equals(right);
        }
        #endregion
    }
}
