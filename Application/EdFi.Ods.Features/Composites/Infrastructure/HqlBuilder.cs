// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Common;
using EdFi.Common.Extensions;
using EdFi.Common.Utils.Extensions;
using EdFi.Ods.Api.Extensions;
using EdFi.Ods.Common;
using EdFi.Ods.Common.Caching;
using EdFi.Ods.Common.Descriptors;
using EdFi.Ods.Common.Exceptions;
using EdFi.Ods.Common.Infrastructure.Activities;
using EdFi.Ods.Common.Models.Domain;
using EdFi.Ods.Common.Models.Resource;
using EdFi.Ods.Common.Specifications;
using log4net;
using NHibernate;
using NHibernate.Exceptions;
using NHibernate.Transform;

namespace EdFi.Ods.Features.Composites.Infrastructure
{
    public class HqlBuilder : ICompositeItemBuilder<HqlBuilderContext, CompositeQuery>
    {
        private const string BaseEntityIdName = "__BaseEntityId__";

        private static readonly Dictionary<string, string> RangeOperatorBySymbol = new Dictionary<string, string>
        {
            {"[", ">="},
            {"{", ">"},
            {"]", "<="},
            {"}", "<"}
        };

        // Support date and numeric ranges (e.g. [2016-05-23..2016-06-30])
        private static readonly Regex _rangeRegex = new Regex(
            @"(?<PropertyName>\w+):(?<BeginRangeSymbol>[\[\{])((?<BeginValue>[0-9]{4}-[0-9]{1,2}-[0-9]{1,2})(\.\.\.|\.\.|…)(?<EndValue>[0-9]{4}-[0-9]{1,2}-[0-9]{1,2})|(?<BeginValue>[0-9\.]+?)(\.\.\.|\.\.|…)(?<EndValue>[0-9\.]+?))(?<EndRangeSymbol>[\}\]])",
            RegexOptions.Compiled);
        private readonly IDescriptorResolver _descriptorResolver;

        private readonly ILog _logger = LogManager.GetLogger(typeof(HqlBuilder));
        private readonly IResourceJoinPathExpressionProcessor _resourceJoinPathExpressionProcessor;
        private readonly IParameterListSetter _parameterListSetter;
        private readonly IPersonEntitySpecification _personEntitySpecification;
        private readonly IPersonUsiResolver _personUsiResolver;

        private readonly ISessionFactory _sessionFactory;

        public HqlBuilder(
            ISessionFactory sessionFactory,
            IDescriptorResolver descriptorResolver,
            IResourceJoinPathExpressionProcessor resourceJoinPathExpressionProcessor,
            IParameterListSetter parameterListSetter,
            IPersonEntitySpecification personEntitySpecification,
            IPersonUsiResolver personUsiResolver)
        {
            _personEntitySpecification = personEntitySpecification;
            _personUsiResolver = personUsiResolver;
            _sessionFactory = Preconditions.ThrowIfNull(sessionFactory, nameof(sessionFactory));
            _descriptorResolver = Preconditions.ThrowIfNull(descriptorResolver, nameof(descriptorResolver));

            _resourceJoinPathExpressionProcessor = Preconditions.ThrowIfNull(
                resourceJoinPathExpressionProcessor, nameof(resourceJoinPathExpressionProcessor));

            _parameterListSetter = Preconditions.ThrowIfNull(parameterListSetter, nameof(parameterListSetter));
        }

        /// <summary>
        /// Applies the composite resource's root resource to the build result using the supplied builder context.
        /// </summary>
        /// <param name="processorContext"></param>
        /// <param name="builderContext">The builder context.</param>
        public void ApplyRootResource(CompositeDefinitionProcessorContext processorContext, HqlBuilderContext builderContext)
        {
            var resource = (Resource) processorContext.CurrentResourceClass;

            builderContext.CurrentAlias = builderContext.AliasGenerator.GetNextAlias();

            builderContext.SpecificationFrom = new StringBuilder();
            builderContext.SpecificationWhere = new StringBuilder();

            // Fully qualified entity name is required to perform hql queries for an entity
            // This requirement was added in phase 3 when multiple entity extensions were added to the same entity
            var properCaseName =
                resource.Entity.DomainModel.SchemaNameMapProvider.GetSchemaMapByPhysicalName(
                        resource.Entity.Schema)
                    .ProperCaseName;

            // Root level queries start with the "Q" version of the model
            var aggregateNamespace = Namespaces.Entities.NHibernate.QueryModels
                .GetAggregateNamespace(resource.Entity.Name, properCaseName);

            builderContext.From
                .AppendFormat(
                    "{0}\t{1}Q {2}",
                    Environment.NewLine,
                    $@"{aggregateNamespace}.{resource.Entity.Name}",
                    builderContext.CurrentAlias);

            // Add the selection of the main query Id
            if (resource.IdentifyingProperties.Count > 1)
            {
                builderContext.Select.AppendFormat("{0}.Id As {1}", builderContext.CurrentAlias, BaseEntityIdName);
            }
            else
            {
                builderContext.Select.AppendFormat(
                    "{0}.{1} As {2}",
                    builderContext.CurrentAlias,
                    resource.Entity.Identifier.Properties.Single(),
                    BaseEntityIdName);
            }

            if (builderContext.FilterCriteria.Count > 0)
            {
                // Process specification parameters
                foreach (var kvp in builderContext.FilterCriteria)
                {
                    string key = kvp.Key;
                    object value = kvp.Value.Value;
                    string filterJoinPath = kvp.Value.FilterPath;

                    string thisFilterJoinAlias = builderContext.CurrentAlias;

                    string parentFilterJoinAlias = thisFilterJoinAlias;

                    // Assumption: "id" in the filter criteria represents a GetById pattern in the route.
                    if (key.EqualsIgnoreCase("id"))
                    {
                        builderContext.IsSingleItemResult = true;
                    }

                    _resourceJoinPathExpressionProcessor.ProcessPath(
                        resource,
                        kvp.Key,
                        filterJoinPath,
                        (prop, pathPart) =>
                        {
                            parentFilterJoinAlias = thisFilterJoinAlias;

                            // Add property to where clause
                            string parameterName = key.Replace(".", "_");

                            builderContext.SpecificationWhere.AppendFormat(
                                "{0}{1}.{2} = :{3}",
                                AndIfNeeded(builderContext.SpecificationWhere),
                                parentFilterJoinAlias,
                                pathPart,
                                parameterName);

                            builderContext.ParameterValueByName.Add(parameterName, value);
                        },
                        (reference, pathPart) =>
                        {
                            parentFilterJoinAlias = thisFilterJoinAlias;

                            // Add a join
                            thisFilterJoinAlias = builderContext.AliasGenerator.GetNextAlias();

                            builderContext.SpecificationFrom.AppendFormat(
                                "{0}\tjoin {1}.{2} {3}",
                                Environment.NewLine,
                                parentFilterJoinAlias,
                                reference.Association.Name,
                                thisFilterJoinAlias);
                        },
                        (collection, pathPart) =>
                        {
                            parentFilterJoinAlias = thisFilterJoinAlias;
                            builderContext.NeedDistinct = true;

                            // Add a join
                            thisFilterJoinAlias = builderContext.AliasGenerator.GetNextAlias();

                            builderContext.SpecificationFrom.AppendFormat(
                                "{0}\tjoin {1}.{2} {3}",
                                Environment.NewLine,
                                parentFilterJoinAlias,
                                collection.PropertyName,
                                thisFilterJoinAlias);
                        },
                        (linkedCollection, pathPart) =>
                        {
                            parentFilterJoinAlias = thisFilterJoinAlias;
                            builderContext.NeedDistinct = true;

                            // Add a join
                            thisFilterJoinAlias = builderContext.AliasGenerator.GetNextAlias();

                            builderContext.SpecificationFrom.AppendFormat(
                                "{0}\tjoin {1}.{2} {3}",
                                Environment.NewLine,
                                parentFilterJoinAlias,
                                linkedCollection.PropertyName,
                                thisFilterJoinAlias);
                        },
                        (embeddedObject, pathPart) =>
                        {
                            parentFilterJoinAlias = thisFilterJoinAlias;

                            // Add a join
                            thisFilterJoinAlias = builderContext.AliasGenerator.GetNextAlias();

                            builderContext.SpecificationFrom.AppendFormat(
                                "{0}\tjoin {1}.{2} {3}",
                                Environment.NewLine,
                                parentFilterJoinAlias,
                                embeddedObject.PropertyName,
                                thisFilterJoinAlias);
                        });
                }
            }

            if (builderContext.QueryStringParameters.Any())
            {
                object queryExpressionObject;

                if (builderContext.QueryStringParameters.TryGetValue(SpecialQueryStringParameters.Q, out queryExpressionObject))
                {
                    ProcessQueryExpressions(builderContext, processorContext, queryExpressionObject.ToString());
                }

                ProcessQueryStringParameters(builderContext, processorContext);

                // Perform root level processing related to GetById and GetByKey request patterns
                builderContext.IsSingleItemResult =
                    processorContext.CurrentResourceClass.IsSingleItemRequest(GetCriteriaQueryStringParameters(builderContext));
            }
        }

        public void ApplyChildResource(
            HqlBuilderContext builderContext,
            CompositeDefinitionProcessorContext processorContext)
        {
            builderContext.CurrentAlias = builderContext.AliasGenerator.GetNextAlias();

            builderContext.From.AppendFormat(
                "{0}\tjoin {1}.{2} {3}",
                Environment.NewLine,
                builderContext.ParentAlias,
                processorContext.EntityMemberName,
                builderContext.CurrentAlias);

            var collection = processorContext.CurrentResourceMember as Collection;

            if (collection != null && collection.ValueFilters.Length > 0)
            {
                StringBuilder filterWhere = new StringBuilder();

                foreach (var valueFilter in collection.ValueFilters)
                {
                    // Get the actual filter to which the property applies
                    var filterProperty = collection.ItemType.AllPropertyByName[valueFilter.PropertyName];

                    string parameterName =
                        builderContext.CurrentAlias
                        + "_" + valueFilter.PropertyName
                        + "_" + (valueFilter.FilterMode == ItemFilterMode.ExcludeOnly
                            ? "0"
                            : "1");

                    // Set the filter values
                    object parametersAsObject;

                    // Is this a first time parameter value assignment?
                    if (!builderContext.CurrentQueryFilterParameterValueByName.TryGetValue(parameterName, out parametersAsObject))
                    {
                        // Process filters into the query
                        filterWhere.AppendFormat(
                            "{0}{1}.{2}Id {3} (:{4})",
                            OrIfNeeded(filterWhere),
                            builderContext.CurrentAlias,
                            valueFilter.PropertyName,
                            valueFilter.FilterMode == ItemFilterMode.ExcludeOnly
                                ? "NOT IN"
                                : "IN",
                            parameterName);

                        // Set the parameter values
                        builderContext.CurrentQueryFilterParameterValueByName[parameterName]
                            = valueFilter.Values
                                .Select(x => _descriptorResolver.GetDescriptorId(filterProperty.DescriptorName, x))
                                .ToArray();
                    }
                    else
                    {
                        // Concatenate the current filter's values to the existing parameter list
                        builderContext.CurrentQueryFilterParameterValueByName[parameterName]
                            = (parametersAsObject as int[])
                            .Concat(
                                valueFilter.Values
                                    .Select(x => _descriptorResolver.GetDescriptorId(filterProperty.DescriptorName, x))
                            )
                            .ToArray();
                    }
                }

                // Apply all the filters using an AND clause
                builderContext.Where.AppendFormat(
                    "{0}({1})",
                    AndIfNeeded(builderContext.Where),
                    filterWhere);
            }
        }

        /// <summary>
        /// Builds a new context from the current builder context for use in processing a flattened reference.
        /// </summary>
        /// <param name="builderContext">The builder context.</param>
        /// <returns>The new builder context for use in processing a flattened reference.</returns>
        public HqlBuilderContext CreateFlattenedMemberContext(HqlBuilderContext builderContext)
        {
            var flattenedBuilderContext = new HqlBuilderContext(
                builderContext.Select,
                builderContext.From,
                null,
                null,
                null,
                builderContext.CurrentAlias,
                int.MinValue,
                null,
                null,
                null,
                builderContext.AliasGenerator
            );

            flattenedBuilderContext.PropertyProjections = builderContext.PropertyProjections;

            return flattenedBuilderContext;
        }

        /// <summary>
        /// Applies the provided flattened resource reference to the build result using the suplied builder context.
        /// </summary>
        /// <param name="member">The flattened ReferencedResource or EmbeddedObject to be applied to the build result.</param>
        /// <param name="builderContext">The builder context.</param>
        public void ApplyFlattenedMember(
            ResourceMemberBase member,
            HqlBuilderContext builderContext)
        {
            var downCastedReference = member as Reference;
            var downCastedEmbeddedObject = member as EmbeddedObject;
            var downCastedResourceProperty = member as ResourceProperty;

            string associationName =
                downCastedReference?.Association?.Name
                ?? downCastedEmbeddedObject?.Association?.Name
                ?? downCastedResourceProperty?.PropertyName;

            // Create a new alias
            builderContext.CurrentAlias = builderContext.AliasGenerator.GetNextAlias();

            // Add the connective HQL join for processing the flattened reference
            builderContext.From.AppendFormat(
                "{0}\tjoin {1}.{2} {3}",
                Environment.NewLine,
                builderContext.ParentAlias,
                associationName,
                builderContext.CurrentAlias);
        }

        /// <summary>
        /// Applies the provided flattened resource reference to the build result using the suplied builder context.
        /// </summary>
        /// <param name="locallyDefinedIdentifyingProperties">The list of local identifying properties to be applied to the build result.</param>
        /// <param name="builderContext">The builder context.</param>
        /// <param name="processorContext">The composite definition processor context.</param>
        public void ApplyLocalIdentifyingProperties(
            IReadOnlyList<EntityProperty> locallyDefinedIdentifyingProperties,
            HqlBuilderContext builderContext,
            CompositeDefinitionProcessorContext processorContext)
        {
            // Add selects for local PK fields, (Note: May be able to optimize this by only doing this if it has children in composite definition.)
            locallyDefinedIdentifyingProperties.ForEach(
                pk =>
                {
                    builderContext.Select.AppendFormat(
                        "{0}{1}.{2} as PK{3}{4}_{2}",
                        CommaIfNeeded(builderContext.Select),
                        builderContext.CurrentAlias,
                        pk.PropertyName,
                        builderContext.Depth,
                        (char) (processorContext.ChildIndex + 'a'));
                });

            // Add ORDER BY for the primary keys
            locallyDefinedIdentifyingProperties.ForEach(
                pk =>
                    builderContext.OrderBy.AppendFormat(
                        "{0}{1}.{2}",
                        CommaIfNeeded(builderContext.OrderBy),
                        builderContext.CurrentAlias,
                        pk.PropertyName));
        }

        /// <summary>
        /// Captures context from the current builder context to be used as the baseline for processing children
        /// while allowing additional changes to be made to the current context.
        /// </summary>
        /// <seealso cref="CreateParentingContext"/>
        /// <param name="builderContext">The current build context.</param>
        /// <remarks>Implementations should use this as a means for preserving part of the current
        /// context for future use by storing the snapshotted context within the current context.</remarks>
        public void SnapshotParentingContext(HqlBuilderContext builderContext)
        {
            // Capture the base SELECT, FROM, ORDER BY for subsequent child queries into the current context
            var baseSelect = new StringBuilder(builderContext.Select.ToString());
            var baseFrom = new StringBuilder(builderContext.From.ToString());
            var baseWhere = new StringBuilder(builderContext.Where.ToString());
            var baseOrderBy = new StringBuilder(builderContext.OrderBy.ToString());

            var snapshotContext = new HqlBuilderContext(baseSelect, baseFrom, baseWhere, baseOrderBy);

            builderContext.ParentingContext = snapshotContext;
        }

        /// <summary>
        /// Creates a new builder context by applying previously snapshotted parental context.
        /// </summary>
        /// <param name="builderContext">The current builder context.</param>
        /// <returns>The new builder context to be used for child processing.</returns>
        public HqlBuilderContext CreateParentingContext(HqlBuilderContext builderContext)
        {
            return new HqlBuilderContext(
                builderContext.ParentingContext.Select,
                builderContext.ParentingContext.From,
                builderContext.ParentingContext.Where,
                builderContext.ParentingContext.OrderBy,
                builderContext.ParameterValueByName,
                builderContext.CurrentAlias,
                builderContext.Depth,
                builderContext.FilterCriteria,
                builderContext.QueryStringParameters,
                builderContext.QueryRangeParameters,
                builderContext.AliasGenerator);
        }

        /// <summary>
        /// Creates a new builder context to be used for processing a child element.
        /// </summary>
        /// <param name="parentingBuilderContext">The parent context to be used to derive the new child context.</param>
        /// <param name="childProcessorContext"></param>
        /// <returns>The new builder context.</returns>
        public HqlBuilderContext CreateChildContext(
            HqlBuilderContext parentingBuilderContext,
            CompositeDefinitionProcessorContext childProcessorContext)
        {
            return new HqlBuilderContext(
                new StringBuilder(parentingBuilderContext.Select.ToString()),
                new StringBuilder(parentingBuilderContext.From.ToString()),
                new StringBuilder(parentingBuilderContext.Where.ToString()),
                new StringBuilder(parentingBuilderContext.OrderBy.ToString()),
                parentingBuilderContext.ParameterValueByName,
                parentingBuilderContext.ParentAlias,
                parentingBuilderContext.Depth + 1,
                parentingBuilderContext.FilterCriteria,
                parentingBuilderContext.QueryStringParameters,
                parentingBuilderContext.QueryRangeParameters,
                parentingBuilderContext.AliasGenerator);
        }

        /// <summary>
        /// Creates a new builder context to be used for processing a flattened reference.
        /// </summary>
        /// <param name="parentingBuilderContext">The parent builder context.</param>
        /// <returns>The new builder context.</returns>
        public HqlBuilderContext CreateFlattenedReferenceChildContext(HqlBuilderContext parentingBuilderContext)
        {
            return new HqlBuilderContext(
                parentingBuilderContext.Select,
                parentingBuilderContext.From,
                parentingBuilderContext.Where,
                parentingBuilderContext.OrderBy,
                parentingBuilderContext.ParameterValueByName,
                parentingBuilderContext.CurrentAlias,
                parentingBuilderContext.Depth,
                parentingBuilderContext.FilterCriteria,
                parentingBuilderContext.QueryStringParameters,
                parentingBuilderContext.QueryRangeParameters,
                parentingBuilderContext.AliasGenerator);
        }

        /// <summary>
        /// Applies processing related to the usage/entry to another top-level resource (e.g. applying authorization concerns).
        /// </summary>
        /// <param name="processorContext">The composite definition processor context.</param>
        /// <param name="builderContext">The current builder context.</param>
        /// <returns><b>true</b> if the resource can be processed; otherwise <b>false</b>.</returns>
        public bool TryIncludeResource(CompositeDefinitionProcessorContext processorContext, HqlBuilderContext builderContext)
        {
            return true;
        }

        /// <summary>
        /// Apply the provided property projections onto the build result with the provided builder and composite
        /// definition processor contexts.
        /// </summary>
        /// <param name="propertyProjections">A list of property projections to be applied to the build result.</param>
        /// <param name="builderContext">The builder context.</param>
        /// <param name="processorContext">The composite definition processor context.</param>
        public void ProjectProperties(
            IReadOnlyList<CompositePropertyProjection> propertyProjections,
            HqlBuilderContext builderContext,
            CompositeDefinitionProcessorContext processorContext)
        {
            if (builderContext.PropertyProjections == null)
            {
                builderContext.PropertyProjections = new List<CompositePropertyProjection>();
            }

            // in the case where we have an abstract reference, we add the discriminator to the query
            if (processorContext.ShouldIncludeResourceSubtype())
            {
                string discriminatorDisplayName = processorContext.CurrentResourceClass.Name.ToCamelCase() + "Type";

                builderContext.Select.AppendFormat(
                    "{0}{1}.{2} as {3}__PassThrough",
                    CommaIfNeeded(builderContext.Select),
                    builderContext.CurrentAlias,
                    "Discriminator",
                    discriminatorDisplayName);
            }

            propertyProjections
               .ForEach(
                    p =>
                    {
                        builderContext.PropertyProjections.Add(p);

                        if (p.ResourceProperty.EntityProperty.IsDescriptorUsage)
                        {
                            string lookupAlias = builderContext.AliasGenerator.GetNextAlias();

                            builderContext.Select.AppendFormat(
                                "{0}{1}.Namespace as {2}__Namespace",
                                CommaIfNeeded(builderContext.Select),
                                lookupAlias,
                                p.DisplayName.ToCamelCase() ?? p.ResourceProperty.PropertyName.ToCamelCase());

                            builderContext.Select.AppendFormat(
                                "{0}{1}.{2} as {3}",
                                CommaIfNeeded(builderContext.Select),
                                lookupAlias,
                                "CodeValue",
                                p.DisplayName.ToCamelCase() ?? p.ResourceProperty.PropertyName.ToCamelCase());

                            builderContext.From.AppendFormat(
                                "{0}\t\tleft join {1}.{2} {3} ",
                                Environment.NewLine,
                                builderContext.CurrentAlias,
                                p.ResourceProperty.PropertyName,
                                lookupAlias);
                        }
                        else
                        {
                            builderContext.Select.AppendFormat(
                                "{0}{1}.{2} as {3}",
                                CommaIfNeeded(builderContext.Select),
                                builderContext.CurrentAlias,
                                p.ResourceProperty.EntityProperty.PropertyName,
                                p.DisplayName.ToCamelCase() ?? p.ResourceProperty.EntityProperty.PropertyName.ToCamelCase());
                        }
                    });
        }

        /// <summary>
        /// Builds the artifact for the root resource of the composite definition.
        /// </summary>
        /// <param name="builderContext">The builder context.</param>
        /// <param name="processorContext">The composite definition processor context.</param>
        /// <param name="rootCompositeQuery">The root composite query, if any records could be found.</param>
        /// <returns><b>true</b> if the root/base query returned records; otherwise <b>false</b>.</returns>
        public bool TryBuildForRootResource(
            HqlBuilderContext builderContext,
            CompositeDefinitionProcessorContext processorContext,
            out CompositeQuery rootCompositeQuery)
        {
            rootCompositeQuery = null;

            // If this is the main query, execute the query and get the Ids and use as criteria for child queries.
            string hql =
                "select " + (builderContext.NeedDistinct
                              ? "distinct "
                              : string.Empty)
                          + $"{Environment.NewLine}\t" + builderContext.Select
                          + $"{Environment.NewLine}from " + builderContext.From + builderContext.SpecificationFrom
                          + (builderContext.SpecificationWhere.Length > 0 || builderContext.Where.Length > 0
                              ? $"{Environment.NewLine}where " + builderContext.SpecificationWhere
                                                               + ConnectingAndIfNeeded(builderContext.SpecificationWhere, builderContext.Where) +
                                                               builderContext.Where
                              : string.Empty)
                          + (builderContext.OrderBy.Length > 0
                              ? $"{Environment.NewLine}order by " + builderContext.OrderBy
                              : string.Empty);

            if (_logger.IsDebugEnabled)
            {
                object correlationId;

                if (builderContext.QueryStringParameters.TryGetValue(
                    SpecialQueryStringParameters.CorrelationId, out correlationId))
                {
                    _logger.DebugFormat("HQL[{0}]:{1}{2}", correlationId, Environment.NewLine, hql);
                }
                else
                {
                    _logger.DebugFormat("HQL:{0}{1}", Environment.NewLine, hql);
                }
            }

            var session = _sessionFactory.GetCurrentSession();
            var query = session.CreateQuery(hql);

            object offsetParameterObject = 0;
            object limitParameterObject = 0;

            int limit = 0;
            int offset = 0;

            if (builderContext.QueryStringParameters.TryGetValue("Limit", out limitParameterObject))
            {
                if (!int.TryParse(limitParameterObject.ToString(), out limit))
                {
                    throw new BadRequestException("Invalid limit specified.");
                }
            }

            if (builderContext.QueryStringParameters.TryGetValue("Offset", out offsetParameterObject))
            {
                if (!int.TryParse(offsetParameterObject.ToString(), out offset))
                {
                    throw new BadRequestException("Invalid offset specified.");
                }
            }

            query.SetFirstResult(offset);

            query.SetMaxResults(
                limit == 0
                    ? 25
                    : limit);

            SetQueryParameters(query, builderContext.ParameterValueByName);
            SetQueryParameters(query, builderContext.CurrentQueryFilterParameterValueByName);

            // Append the where clause for Id selection
            // Add the selection of the main query Id
            if (processorContext.CurrentResourceClass.IdentifyingProperties.Count > 1)
            {
                builderContext.ParentingContext.Where.AppendFormat(
                    "{0}{1}.Id IN (:BaseEntityId)",
                    AndIfNeeded(builderContext.ParentingContext.Where),
                    builderContext.CurrentAlias);
            }
            else
            {
                builderContext.ParentingContext.Where.AppendFormat(
                    "{0}{1}.{2} IN (:BaseEntityId)",
                    AndIfNeeded(builderContext.ParentingContext.Where),
                    builderContext.CurrentAlias,
                    processorContext.CurrentResourceClass.Entity.Identifier.Properties.Single()
                        .PropertyName);
            }

            // This is the main/base query, so execute the query and get the Ids and use as criteria for child queries.

            IList<object> queryResults = null;

            try
            {
                queryResults = query
                    .SetResultTransformer(Transformers.AliasToEntityMap)
                    .List<object>();
            }
            catch (GenericADOException ex)
            {
                // Added this validation to avoid hiding a DatabseConnectionException
                if (ex.InnerException is DatabaseConnectionException)
                {
                    _logger.Error("Query execution failed. Connection to the database", ex);
                    throw ex.InnerException;
                }

                _logger.Error("Query execution failed (likely due to invalid parameter values). ", ex);
                throw new ArgumentException("Query execution failed (likely due to invalid parameter values).");
            }

            // Get the Ids and assign to the parameters
            var mainQueryIds = queryResults.Cast<Hashtable>()
                .Select(ht => ht[BaseEntityIdName])
                .ToList();

            if (!mainQueryIds.Any())
            {
                return false;
            }

            builderContext.ParameterValueByName[BaseEntityIdName] = mainQueryIds;

            var thisQuery = new CompositeQuery(
                processorContext.MemberDisplayName,
                builderContext.PropertyProjections.Select(
                        x => x.DisplayName.ToCamelCase() ?? x.ResourceProperty.PropertyName.ToCamelCase())
                    .ToArray(),
                queryResults,
                builderContext.IsSingleItemResult);

            rootCompositeQuery = thisQuery;
            return true;
        }

        /// <summary>
        /// Builds the artifact for the root resource of the composite definition.
        /// </summary>
        /// <param name="parentResult">The parent build result, for compositional behavior (if applicable).</param>
        /// <param name="builderContext">The builder context.</param>
        /// <param name="processorContext">The composite definition processor context.</param>
        /// <returns>The build result.</returns>
        public CompositeQuery BuildForChildResource(
            CompositeQuery parentResult,
            HqlBuilderContext builderContext,
            CompositeDefinitionProcessorContext processorContext)
        {
            string hql =
                $"select {Environment.NewLine}\t" + builderContext.Select
                                + $"{Environment.NewLine}from " + builderContext.From
                                + (builderContext.Where.Length > 0
                                    ? $"{Environment.NewLine}where " + builderContext.Where
                                    : string.Empty)
                                + $"{Environment.NewLine}order by " + builderContext.OrderBy;

            if (_logger.IsDebugEnabled)
            {
                object correlationId;

                if (builderContext.QueryStringParameters.TryGetValue(
                    SpecialQueryStringParameters.CorrelationId, out correlationId))
                {
                    _logger.DebugFormat("HQL[{0}]:{1}{2}", correlationId, Environment.NewLine, hql);
                }
                else
                {
                    _logger.DebugFormat("HQL:{0}{1}", Environment.NewLine, hql);
                }
            }

            var session = _sessionFactory.GetCurrentSession();
            var query = session.CreateQuery(hql);

            object idValues;

            if (builderContext.ParameterValueByName.TryGetValue(BaseEntityIdName, out idValues))
            {
                _parameterListSetter.SetParameterList(query, "BaseEntityId", idValues as IEnumerable);
            }

            // Apply current query's filter parameters.
            SetQueryParameters(query, builderContext.CurrentQueryFilterParameterValueByName);

            bool isSingleItemResult =
                processorContext.IsReferenceResource()
                || processorContext.IsEmbeddedObject();

            var thisQuery = new CompositeQuery(
                parentResult,
                processorContext.MemberDisplayName.ToCamelCase(),
                builderContext.PropertyProjections.Select(x => x.DisplayName.ToCamelCase() ?? x.ResourceProperty.PropertyName.ToCamelCase())
                              .ToArray(),
                query
                   .SetResultTransformer(Transformers.AliasToEntityMap)
                   .Future<object>(),
                isSingleItemResult);

            parentResult.ChildQueries.Add(thisQuery);

            return thisQuery;
        }

        private void ProcessQueryStringParameters(HqlBuilderContext builderContext, CompositeDefinitionProcessorContext processorContext)
        {
            // Get all non "special" query string parameter for property value equality processing
            var queryStringParameters = GetCriteriaQueryStringParameters(builderContext);

            var parameterAndPropertyTuples = queryStringParameters.Select(
                qsp =>
                {
                    if (!processorContext.CurrentResourceClass.AllPropertyByName.TryGetValue(qsp.Key, out var targetProperty))
                    {
                        ThrowPropertyNotFoundException(qsp.Key);
                    }

                    return (qsp, targetProperty);
                });

            // Resolve any UniqueIds to USIs
            var usisToResolveByPersonType = ResolveUsisForUniqueIdQueryStringParameters();

            foreach (var tuple in parameterAndPropertyTuples)
            {
                var (queryStringParameter, targetProperty) = tuple;

                // TODO: Embedded convention. Types and descriptors at the top level
                string criteriaPropertyName;
                object parameterValue;

                // Handle Lookup conversions
                if (targetProperty.IsDescriptorUsage)
                {
                    var id = _descriptorResolver.GetDescriptorId(
                        targetProperty.DescriptorName,
                        Convert.ToString(queryStringParameter.Value));

                    criteriaPropertyName = targetProperty.EntityProperty.PropertyName;
                    parameterValue = id;
                }

                // Handle UniqueId conversions
                else if (_personEntitySpecification.TryGetUniqueIdPersonType(targetProperty.PropertyName, out string personType))
                {
                    string uniqueId = Convert.ToString(queryStringParameter.Value);

                    if (!usisToResolveByPersonType.TryGetValue(personType, out var uniqueIdByUsi))
                    {
                        // This should never happen because of the pre-processing done on the query string parameters earlier
                        throw new InvalidOperationException($"Unable to find resolved USIs for person type '{personType}'.");
                    }

                    uniqueIdByUsi.TryGetValue(uniqueId, out int usi);

                    // TODO: Embedded convention - Convert UniqueId to USI from Resource model to query Entity model on Person entities
                    // The resource model maps uniqueIds to uniqueIds on the main entity(Student,Staff,Parent)
                    if (_personEntitySpecification.IsPersonEntity(targetProperty.ParentFullName.Name))
                    {
                        criteriaPropertyName = targetProperty.EntityProperty.PropertyName.Replace("UniqueId", "USI");
                    }
                    else
                    {
                        criteriaPropertyName = targetProperty.EntityProperty.PropertyName;
                    }

                    parameterValue = usi;
                }
                else
                {
                    criteriaPropertyName = targetProperty.PropertyName;

                    parameterValue = ConvertParameterValueForProperty(
                        targetProperty,
                        Convert.ToString(queryStringParameter.Value));
                }

                // Add criteria to the query
                builderContext.SpecificationWhere.AppendFormat(
                    "{0}{1}.{2} = :{2}",
                    AndIfNeeded(builderContext.SpecificationWhere),
                    builderContext.CurrentAlias,
                    criteriaPropertyName);

                if (builderContext.CurrentQueryFilterParameterValueByName.ContainsKey(criteriaPropertyName))
                {
                    throw new ArgumentException(
                        string.Format(
                            "The value for parameter '{0}' was already assigned and cannot be reassigned using the query string.",
                            criteriaPropertyName));
                }

                builderContext.CurrentQueryFilterParameterValueByName[criteriaPropertyName] =
                    parameterValue;
            }

            Dictionary<string, Dictionary<string, int>> ResolveUsisForUniqueIdQueryStringParameters()
            {
                var usisToResolveByPersonType = parameterAndPropertyTuples
                    .Where(t => UniqueIdConventions.IsUniqueId(t.targetProperty.PropertyName))
                    .Select(
                        t =>
                        {
                            if (!_personEntitySpecification.TryGetUniqueIdPersonType(t.targetProperty.PropertyName, out string personType))
                            {
                                throw new NotSupportedException(
                                    $"Unable to determine person type from property '{t.targetProperty.PropertyName}'.");
                            }

                            string uniqueId = Convert.ToString(t.qsp.Value);

                            return (personType, uniqueId);
                        })
                    .GroupBy(x => x.personType, x => x.uniqueId)
                    .ToDictionary(x => x.Key, x => x.ToDictionary(k => k, k => default(int)));

                var usiResolutionTasks = usisToResolveByPersonType.Select(x => _personUsiResolver.ResolveUsisAsync(x.Key, x.Value)).ToArray();

                if (usiResolutionTasks.Any())
                {
                    Task.WaitAll(usiResolutionTasks.ToArray(), CancellationToken.None);
                }

                return usisToResolveByPersonType;
            }
        }

        private static List<KeyValuePair<string, object>> GetCriteriaQueryStringParameters(HqlBuilderContext builderContext)
        {
            var queryStringParameters = builderContext.QueryStringParameters
                                                      .Where(p => !SpecialQueryStringParameters.Names.Contains(p.Key))
                                                      .ToList();

            return queryStringParameters;
        }

        private static object ConvertParameterValueForProperty(ResourceProperty targetProperty, object rawValue)
        {
            try
            {
                Type targetType = targetProperty.PropertyType.ToUnderlyingSystemType();

                if (targetType == typeof(Guid))
                {
                    Guid convertedGuid;

                    if (Guid.TryParse(rawValue.ToString(), out convertedGuid))
                    {
                        return convertedGuid;
                    }

                    throw new BadRequestException(
                        $"Invalid query string parameter value provided.  The value for parameter '{targetProperty.PropertyName}' could not be processed as a GUID.");
                }

                var convertedValue = Convert.ChangeType(
                    rawValue,
                    targetType);

                return convertedValue;
            }
            catch (FormatException ex)
            {
                throw new BadRequestException(
                    $"Invalid query string parameter value provided.  The value for parameter '{targetProperty.PropertyName}' could not be processed. {ex.Message}");
            }
        }

        private static void ThrowPropertyNotFoundException(string attemptedPropertyName)
        {
            // Prevent any sort of nefarious injection into the response message, while still providing a helpful message to the caller
            if (Regex.IsMatch(attemptedPropertyName, @"^\w+$"))
            {
                throw new BadRequestException(
                    $"The property '{attemptedPropertyName}' does not exist or is not available.");
            }

            throw new BadRequestException("Invalid query string parameter.");
        }

        private static void ProcessQueryExpressions(
            HqlBuilderContext builderContext,
            CompositeDefinitionProcessorContext processorContext,
            string queryExpressionText)
        {
            var queryExpressions = queryExpressionText.Split(',');

            int n = 0;

            foreach (var queryExpression in queryExpressions)
            {
                var rangeQueryMatch = _rangeRegex.Match(queryExpression);

                if (!rangeQueryMatch.Success)
                {
                    throw new BadRequestException(
                        "The query filter expression was invalid. Currently, only numeric and date range expressions are supported.");
                }

                string targetPropertyName = rangeQueryMatch.Groups["PropertyName"].Value;

                ResourceProperty targetProperty;

                if (!processorContext.CurrentResourceClass.AllPropertyByName.TryGetValue(targetPropertyName, out targetProperty))
                {
                    ThrowPropertyNotFoundException(targetPropertyName);
                }

                string rangeBeginParameterName = "Range" + n + "Begin";
                string rangeEndParameterName = "Range" + n + "End";

                // Add the value to the parameter value collection
                builderContext.CurrentQueryFilterParameterValueByName.Add(
                    rangeBeginParameterName,
                    ConvertParameterValueForProperty(
                        targetProperty,
                        rangeQueryMatch.Groups["BeginValue"].Value));

                builderContext.CurrentQueryFilterParameterValueByName.Add(
                    rangeEndParameterName,
                    ConvertParameterValueForProperty(
                        targetProperty,
                        rangeQueryMatch.Groups["EndValue"].Value));

                // Add the query criteria to the HQL query
                builderContext.SpecificationWhere.AppendFormat(
                    "{0}{1}.{2} {3} :{4} and {1}.{2} {5} :{6}",
                    AndIfNeeded(builderContext.SpecificationWhere),
                    builderContext.CurrentAlias,
                    targetProperty.PropertyName,
                    RangeOperatorBySymbol[rangeQueryMatch.Groups["BeginRangeSymbol"].Value],
                    rangeBeginParameterName,
                    RangeOperatorBySymbol[rangeQueryMatch.Groups["EndRangeSymbol"].Value],
                    rangeEndParameterName);

                n++;
            }
        }

        private void SetQueryParameters(IQuery query, IDictionary<string, object> parameterValueByName)
        {
            foreach (var kvp in parameterValueByName)
            {
                string parameterName = kvp.Key;
                object value = kvp.Value;

                // NOTE: Initially the following guard condition was added to prevent the presence of the
                // CreatedByOwnershipTokenId parameter from breaking composite requests.

                // Don't process parameter values that aren't present in the query
                if (!query.NamedParameters.Contains(parameterName))
                {
                    continue;
                }

                if (parameterName.EndsWith("_Id"))
                {
                    // Parameter is a GUID resource Id
                    query.SetParameter(parameterName, new Guid((string) value));
                }
                else if (!(value is string) && value is IEnumerable)
                {
                    _parameterListSetter.SetParameterList(query, parameterName, value as IEnumerable);
                }
                else
                {
                    query.SetParameter(parameterName, value);
                }
            }
        }

        private static string AndIfNeeded(StringBuilder where)
        {
            return where.Length > 0
                ? " AND "
                : string.Empty;
        }

        private static string OrIfNeeded(StringBuilder where)
        {
            return where.Length > 0
                ? " OR "
                : string.Empty;
        }

        private static string CommaIfNeeded(StringBuilder orderBy)
        {
            return orderBy.Length > 0
                ? $",{Environment.NewLine}\t"
                : string.Empty;
        }

        private static string ConnectingAndIfNeeded(StringBuilder where1, StringBuilder where2)
        {
            return where1.Length > 0 && where2.Length > 0
                ? " AND "
                : string.Empty;
        }
    }
}
