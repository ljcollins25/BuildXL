// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Pips;
using Test.BuildXL.Scheduler;
using Test.BuildXL.TestUtilities.Xunit;

namespace IntegrationTest.BuildXL.Scheduler
{
    /// <summary>
    /// Extension for <see cref="ScheduleRunResult"/> used for incremental scheduling tests.
    /// </summary>
    public static class ScheduleRunResultExtensions
    {
        /// <summary>
        /// Validates that pips are not scheduled.
        /// </summary>
        public static ScheduleRunResult AssertNotScheduled(this ScheduleRunResult @this, params PipId[] pipIds)
        {
            XAssert.IsTrue(pipIds.Length > 0, "Scheduling assertions should specify the pip ids in question");

            @this.AssertSuccess();
            PipResultStatus status;
            for (int i = 0; i < pipIds.Length; i++)
            {
                PipId pipId = pipIds[i];
                bool exists = @this.PipResults.TryGetValue(pipId, out status);
                XAssert.IsFalse(exists, "A pip is scheduled, but it should have not been scheduled. Pip at 0-based parameter index: " + i + " (status: " + status + ")");
            }

            return @this;
        }

        /// <summary>
        /// Validates that pips are scheduled.
        /// </summary>
        public static ScheduleRunResult AssertScheduled(this ScheduleRunResult @this, params PipId[] pipIds)
        {
            XAssert.IsTrue(pipIds.Length > 0, "Scheduling assertions should specify the pip ids in question");

            @this.AssertSuccess();
            PipResultStatus status;
            for (int i = 0; i < pipIds.Length; i++)
            {
                PipId pipId = pipIds[i];
                bool exists = @this.PipResults.TryGetValue(pipId, out status);
                XAssert.IsTrue(exists, "A pip is not scheduled, but it should have been scheduled. Pip at 0-based parameter index: " + i);
            }

            return @this;
        }
    }
}
