// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Runtime.Serialization;

namespace BuildXL.Cache.ContentStore.FileSystem
{
    /// <summary>
    ///     An exception that is thrown when a native API call fails.
    /// </summary>
    [SuppressMessage(
        "Microsoft.Design",
        "CA1032:ImplementStandardExceptionConstructors",
        Justification = "The exception must be constructed with a status code and status name.")]
    [Serializable]
    public class NTStatusException : Exception
    {
        /// <summary>
        ///     NT Status Code
        /// </summary>
        private readonly uint _statusCode;

        /// <summary>
        ///     NT Status Name
        /// </summary>
        private readonly string? _statusName;

        /// <summary>
        ///     Initializes a new instance of the <see cref="NTStatusException" /> class.
        /// </summary>
        /// <param name="statusCode">Status code from a native API call</param>
        /// <param name="statusName">Status name from a native API call</param>
        /// <param name="message">Exception message</param>
        public NTStatusException(uint statusCode, string statusName, string message)
            : base(message)
        {
            Contract.Requires(!string.IsNullOrEmpty(statusName));
            Contract.Requires(message != null);

            _statusCode = statusCode;
            _statusName = statusName;
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="NTStatusException" /> class.
        /// </summary>
#if NET8_0_OR_GREATER
        [Obsolete(DiagnosticId = "SYSLIB0051")]
#endif
        protected NTStatusException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            Contract.Requires(info != null);

            _statusCode = info.GetUInt32("StatusCode");
            _statusName = info.GetString("StatusName");
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "NTStatusException thrown with code [0x{0:X}] = [{1}] : {2}",
                _statusCode,
                _statusName,
                Message);
        }

        /// <inheritdoc />
#if NET8_0_OR_GREATER
        [Obsolete(DiagnosticId = "SYSLIB0051")]
#endif
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("StatusCode", _statusCode);
            info.AddValue("StatusName", _statusName);

            base.GetObjectData(info, context);
        }
    }
}
