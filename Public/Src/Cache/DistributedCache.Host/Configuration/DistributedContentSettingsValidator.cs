﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

#nullable enable

namespace BuildXL.Cache.Host.Configuration
{
    /// <summary>
    /// This class helps clients validate our configuration before actually using it. It's basically used as a PR gate.
    /// </summary>
    public static class DistributedContentSettingsValidator
    {
        /// <summary>
        /// Entry point to <see cref="DistributedContentSettings"/> validation.
        /// </summary>
        /// <remarks>
        /// This method is called by ConfigCop to ensure that DistributedContentSettings isn't obviously broken.
        /// </remarks>
        public static IReadOnlyList<string> Validate(this DistributedContentSettings settings)
        {
            var errorList = new List<string>();

            ValidateProactiveCopies(settings, errorList);
            ValidateDataAnnotations(settings, errorList);

            return errorList;
        }

        private static void ValidateDataAnnotations(DistributedContentSettings settings, ICollection<string> errorList)
        {
            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(settings, new ValidationContext(settings), validationResults, validateAllProperties: true))
            {
                foreach (var result in validationResults)
                {
                    if (result != ValidationResult.Success)
                    {
                        errorList.Add(result.ErrorMessage!);
                    }
                }
            }
        }

        private static void ValidateProactiveCopies(DistributedContentSettings settings, ICollection<string> errors)
        {
            if (settings.ProactiveCopyUsePreferredLocations && !settings.UseBinManager)
            {
                errors.Add($"For {nameof(settings.ProactiveCopyUsePreferredLocations)} to be true, {nameof(settings.UseBinManager)} should also be true.");
            }
        }
    }
}
