// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Utilities.Collections;

namespace BuildXL.Cache.ContentStore.Distributed.Utilities
{
    public static class MultiLevelUtilities
    {
        /// <summary>
        /// Processes given inputs with on first level then optionally calls subset for second level
        /// </summary>
        /// <remarks>
        /// * Call first function in given inputs
        /// * Call specified inputs (based on first function results) with second function
        /// * Fix indices for results of second function
        /// * Merge the results
        /// </remarks>
        public static Task<IEnumerable<Task<Indexed<TResult>>>> RunMultiLevelAsync<TSource, TResult>(
            IReadOnlyList<TSource> inputs,
            GetIndexedResults<TSource, TResult> runFirstLevelAsync,
            GetIndexedResults<TSource, TResult> runSecondLevelAsync,
            Func<TResult, bool> useFirstLevelResult,
            Func<IReadOnlyList<Indexed<TResult>>, Task> handleFirstLevelOnlyResultsAsync = null
            )
        {
            // Get results from first method
            return runFirstLevelAsync(inputs)
                .FallbackAsync<TSource, TResult>(useFirstLevelResult, inputs, runSecondLevelAsync, handleFirstLevelOnlyResultsAsync);
        }

        /// <summary>
        /// Processes given inputs with on first level then optionally calls subset for second level
        /// </summary>
        /// <remarks>
        /// * Call first function in given inputs
        /// * Call specified inputs (based on first function results) with second function
        /// * Fix indices for results of second function
        /// * Merge the results
        /// </remarks>
        public static async Task<IEnumerable<Task<Indexed<TResult>>>> FallbackAsync<TSource, TResult>(
            this Task<IEnumerable<Task<Indexed<TResult>>>> initialResultsTask,
            Func<TResult, bool> useFirstLevelResult,
            IReadOnlyList<TSource> inputs,
            GetIndexedResults<TSource, TResult> runSecondLevelAsync,
            Func<IReadOnlyList<Indexed<TResult>>, Task> handleFirstLevelOnlyResultsAsync = null
            )
        {
            var initialResults = await initialResultsTask;

            // Determine which inputs can use the first level results based on useFirstLevelResult()
            List<Indexed<TResult>> indexedFirstLevelOnlyResults = new List<Indexed<TResult>>();
            List<Indexed<TResult>> nextLevelResults = null;

            foreach (var resultTask in initialResults)
            {
                var result = await resultTask;
                if (useFirstLevelResult(result.Item))
                {
                    indexedFirstLevelOnlyResults.Add(result);
                }
                else
                {
                    nextLevelResults = nextLevelResults ?? new List<Indexed<TResult>>();
                    nextLevelResults.Add(result);
                }
            }

            // Optional action to process hits from first attempt
            if (handleFirstLevelOnlyResultsAsync != null)
            {
                await handleFirstLevelOnlyResultsAsync(indexedFirstLevelOnlyResults);
            }

            // Return early if no misses
            if (nextLevelResults == null)
            {
                return initialResults;
            }

            // Try fallback for items that failed in first attempt
            IReadOnlyList<TSource> missedInputs = nextLevelResults.SelectList(r => inputs[r.Index]);
            IEnumerable<Task<Indexed<TResult>>> fallbackResults = await runSecondLevelAsync(missedInputs);

            foreach (var resultTask in fallbackResults)
            {
                Indexed<TResult> result = await resultTask;
                int originalIndex = nextLevelResults[result.Index].Index;
                nextLevelResults[result.Index] = result.Item.WithIndex(originalIndex);
            }

            // Merge original successful results with fallback results
            return indexedFirstLevelOnlyResults
                    .Concat(nextLevelResults)
                    .AsTasks();
        }
    }
}
