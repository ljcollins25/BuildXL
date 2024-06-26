// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.Tracing;
using BuildXL.Cache.Interfaces;
using BuildXL.Utilities.Core;

namespace BuildXL.Cache.ImplementationSupport
{
    /// <summary>
    /// ICache Session API specific activity
    /// </summary>
    public sealed class IncorporateRecordsActivity : CacheActivity
    {
        /// <summary>
        /// Activity Constructor
        /// </summary>
        /// <param name="eventSource">The cache's event source</param>
        /// <param name="relatedActivityId">The related activity ID as passed to the API</param>
        /// <param name="cache">The ICache session instance (this)</param>
        public IncorporateRecordsActivity(EventSource eventSource, Guid relatedActivityId, ICacheReadOnlySession cache)
            : base(
                eventSource,
                new EventSourceOptions
                {
                    Level = EventLevel.Informational,
                    Keywords = Keywords.TelemetryKeyword | Keywords.IncorporateRecords,
                },
                relatedActivityId,
                InterfaceNames.IncorporateRecords,
                cache.CacheId)
        {
        }

        /// <summary>
        /// Return result for the activity
        /// </summary>
        /// <param name="result">The return result</param>
        /// <returns>
        /// Returns the same value as given
        /// </returns>
        /// <notes>
        /// Returns the same value as given such that code can be
        /// written that looks like:
        /// <code>return activity.Returns(result);</code>
        /// If we had a formal way to do tail-calls in C# we would
        /// have done that.  This is as close as it gets.
        /// </notes>
        public Possible<int, Failure> Returns(Possible<int, Failure> result)
        {
            if (result.Succeeded)
            {
                if (TraceReturnValuesEnabled())
                {
                    Write(
                        ReturnOptions,
                        new
                        {
                            Count = result.Result,
                        });
                }

                Stop();
            }
            else
            {
                StopFailure(result.Failure);
            }

            return result;
        }
    }
}
