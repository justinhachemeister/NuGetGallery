﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using NuGet.Services.Validation;
using NuGet.Services.Validation.Issues;
using NuGetGallery.Configuration;

namespace NuGetGallery
{
    public class ValidationService : IValidationService
    {
        private readonly IAppConfiguration _appConfiguration;
        private readonly IPackageService _packageService;
        private readonly IPackageValidationInitiator _initiator;
        private readonly IEntityRepository<PackageValidationSet> _validationSets;
        private readonly ITelemetryService _telemetryService;

        public ValidationService(
            IAppConfiguration appConfiguration,
            IPackageService packageService,
            IPackageValidationInitiator initiator,
            IEntityRepository<PackageValidationSet> validationSets,
            ITelemetryService telemetryService)
        {
            _appConfiguration = appConfiguration ?? throw new ArgumentNullException(nameof(appConfiguration));
            _packageService = packageService ?? throw new ArgumentNullException(nameof(packageService));
            _initiator = initiator ?? throw new ArgumentNullException(nameof(initiator));
            _validationSets = validationSets ?? throw new ArgumentNullException(nameof(validationSets));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        }

        public async Task StartValidationAsync(Package package)
        {
            var packageStatus = await _initiator.StartValidationAsync(package);

            await _packageService.UpdatePackageStatusAsync(
                package,
                packageStatus,
                commitChanges: false);
        }

        public async Task RevalidateAsync(Package package)
        {
            await _initiator.StartValidationAsync(package);

            _telemetryService.TrackPackageRevalidate(package);
        }

        public bool IsValidatingTooLong(Package package)
        {
            if (package.PackageStatusKey == PackageStatus.Validating)
            {
                return ((DateTime.UtcNow - package.Created) >= _appConfiguration.ValidationExpectedTime);
            }

            return false;
        }

        public IReadOnlyList<ValidationIssue> GetLatestValidationIssues(Package package)
        {
            var issues = Enumerable.Empty<ValidationIssue>();

            // Only query the database for validation issues if the package has failed validation.
            if (package.PackageStatusKey == PackageStatus.FailedValidation)
            {
                // Grab the most recently completed validation set for this package. Note that the orchestrator will stop
                // processing a validation set if all validation succeed, OR, one or more validation fails.
                var validationSet = _validationSets
                                        .GetAll()
                                        .Where(s => s.PackageKey == package.Key)
                                        .Where(s => s.PackageValidations.All(v => v.ValidationStatus == ValidationStatus.Succeeded) ||
                                                    s.PackageValidations.Any(v => v.ValidationStatus == ValidationStatus.Failed))
                                        .Include(s => s.PackageValidations.Select(v => v.PackageValidationIssues))
                                        .OrderByDescending(s => s.Updated)
                                        .FirstOrDefault();

                if (validationSet != null)
                {
                    // Get the failed validation set's validation issues. The issues are ordered by their
                    // key so that it appears that issues are appended as more validations fail.
                    issues = validationSet
                        .PackageValidations
                        .SelectMany(v => v.PackageValidationIssues)
                        .OrderBy(i => i.Key)
                        .Select(i => ValidationIssue.Deserialize(i.IssueCode, i.Data))
                        .ToList();
                }

                // If the package failed validation, use the generic error message.
                if (issues == null || !issues.Any())
                {
                    issues = new[] { ValidationIssue.Unknown };
                }
            }

            // Filter out unknown issues and deduplicate the issues by code and data. This also deduplicates cases
            // where there is extraneous data in the serialized data field or if the issue code is unknown to the
            // gallery.
            return issues
                .GroupBy(x => new { x.IssueCode, Data = x.Serialize() })
                .Select(x => x.First())
                .ToList();
        }
    }
}