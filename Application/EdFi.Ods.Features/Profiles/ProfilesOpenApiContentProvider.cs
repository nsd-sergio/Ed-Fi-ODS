﻿// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using EdFi.Common;
using EdFi.Ods.Api.Constants;
using EdFi.Ods.Api.Models;
using EdFi.Ods.Api.Providers;
using EdFi.Ods.Common;
using EdFi.Ods.Common.Metadata;
using EdFi.Ods.Common.Metadata.Profiles;
using EdFi.Ods.Common.Models;
using EdFi.Ods.Features.OpenApiMetadata.Dtos;
using EdFi.Ods.Features.OpenApiMetadata.Factories;
using EdFi.Ods.Features.OpenApiMetadata.Models;
using EdFi.Ods.Features.OpenApiMetadata.Strategies.ResourceStrategies;

namespace EdFi.Ods.Features.Profiles
{
    public class ProfilesOpenApiContentProvider : IOpenApiContentProvider
    {
        private readonly IProfileResourceModelProvider _profileResourceModelProvider;
        private readonly IProfileResourceNamesProvider _profileResourceNamesProvider;
        private readonly IResourceModelProvider _resourceModelProvider;
        private readonly IOpenApiMetadataDocumentFactory _openApiMetadataDocumentFactory;

        public ProfilesOpenApiContentProvider(IProfileResourceModelProvider profileResourceModelProvider,
            IProfileResourceNamesProvider profileResourceNamesProvider,
            IResourceModelProvider resourceModelProvider,
            IOpenApiMetadataDocumentFactory documentFactory)
        {
            _profileResourceModelProvider = Preconditions.ThrowIfNull(profileResourceModelProvider, nameof(profileResourceModelProvider));
            _profileResourceNamesProvider = Preconditions.ThrowIfNull(profileResourceNamesProvider, nameof(profileResourceNamesProvider));
            _resourceModelProvider = Preconditions.ThrowIfNull(resourceModelProvider, nameof(resourceModelProvider));
            _openApiMetadataDocumentFactory = Preconditions.ThrowIfNull(documentFactory, nameof(documentFactory));
        }

        public string RouteName
        {
            get => MetadataRouteConstants.Profiles;
        }

        public IEnumerable<OpenApiContent> GetOpenApiContent()
        {
            var openApiStrategy = new OpenApiProfileStrategy();

            return _profileResourceNamesProvider
                .GetProfileResourceNames()
                .Select(x => x.ProfileName)
                .Select(
                    x => new OpenApiMetadataProfileContext
                    {
                        ProfileName = x,
                        ProfileResourceModel = _profileResourceModelProvider.GetProfileResourceModel(x)
                    })
                .Select(
                    x => new OpenApiMetadataDocumentContext(_resourceModelProvider.GetResourceModel())
                    {
                        ProfileContext = x,
                        IsIncludedExtension = r => true
                    })
                .Select(
                    c =>
                        new OpenApiContent(
                            OpenApiMetadataSections.Profiles,
                            c.ProfileContext.ProfileName,
                            new Lazy<string>(() => _openApiMetadataDocumentFactory.Create(openApiStrategy, c)),
                            RouteConstants.DataManagementRoutePrefix,
                            $"{OpenApiMetadataSections.Profiles}/{c.ProfileContext.ProfileName}"));
        }
    }
}
