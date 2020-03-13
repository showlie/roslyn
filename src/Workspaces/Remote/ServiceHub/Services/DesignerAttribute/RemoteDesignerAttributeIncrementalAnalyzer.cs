﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.DesignerAttribute;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Remote
{
    internal sealed partial class RemoteDesignerAttributeIncrementalAnalyzer : IncrementalAnalyzerBase
    {
        private const string DataKey = "DesignerAttributeData";

        private readonly Workspace _workspace;

        /// <summary>
        /// Channel back to VS to inform it of the designer attributes we discover.
        /// </summary>
        private readonly RemoteEndPoint _endPoint;

        /// <summary>
        /// Storage where we can keep track of what we know about the project and have informed VS
        /// (and the project systems) about.
        /// </summary>
        private readonly IPersistentStorage _storage;

        public RemoteDesignerAttributeIncrementalAnalyzer(Workspace workspace, RemoteEndPoint endPoint)
        {
            _workspace = workspace;
            _endPoint = endPoint;

            var storageService = _workspace.Services.GetRequiredService<IPersistentStorageService>();
            _storage = storageService.GetStorage(workspace.CurrentSolution);
        }

        public override Task AnalyzeProjectAsync(Project project, bool semanticsChanged, InvocationReasons reasons, CancellationToken cancellationToken)
            => AnalyzeProjectAsync(project, specificDoc: null, cancellationToken);

        public override Task AnalyzeDocumentAsync(Document document, SyntaxNode bodyOpt, InvocationReasons reasons, CancellationToken cancellationToken)
        {
            // don't need to reanalyze file if just a method body was edited.  That can't
            // affect designer attributes.
            if (bodyOpt != null)
                return Task.CompletedTask;

            // When we register our analyzer we will get called into for every document to
            // 'reanalyze' them all.  Ignore those as we would prefer to analyze the project
            // en-mass.
            if (reasons.Contains(PredefinedInvocationReasons.Reanalyze))
                return Task.CompletedTask;

            return AnalyzeProjectAsync(document.Project, document, cancellationToken);
        }

        private async Task AnalyzeProjectAsync(Project project, Document? specificDoc, CancellationToken cancellationToken)
        {
            if (!project.SupportsCompilation)
                return;

            // We need to reanalyze the project whenever it (or any of its dependencies) have
            // changed.  We need to know about dependencies since if a downstream project adds the
            // DesignerCategory attribute to a class, that can affect us when we examine the classes
            // in this project.
            var projectVersion = await project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);

            var latestInfos = await ComputeLatestInfosAsync(
                project, projectVersion, specificDoc, cancellationToken).ConfigureAwait(false);

            // Now get all the values that actually changed and notify VS about them. We don't need
            // to tell it about the ones that didn't change since that will have no effect on the
            // user experience.
            //
            //  !  is safe here as `i.changed` implies `i.info` is non-null.
            var changedInfos = latestInfos.Where(i => i.changed).Select(i => i.info!.Value).ToList();
            if (changedInfos.Count > 0)
            {
                await _endPoint.InvokeAsync(
                    nameof(IDesignerAttributeServiceCallback.RegisterDesignerAttributesAsync),
                    new object[] { changedInfos },
                    cancellationToken).ConfigureAwait(false);
            }

            // now that we've notified VS, persist all the infos we have (changed or otherwise) back
            // to disk.  We want to do this even when the data is unchanged so that our version
            // stamps will be correct for the next time we come around to analyze this project.
            //
            // Note: we have a potential race condition here.  Specifically, for simplicity, the VS
            // side will return immediately, without actually notifying the project system.  That
            // means that we could persist the data to local storage that isn't in sync with what
            // the project system knows about.  i.e. if VS is closed or crashes before that
            // information is persisted, then these two systems will be in disagreement.  this is
            // believed to not be a big issue given how small a time window this would be and how
            // easy it would be to get out of that state (just edit the file).

            await PersistLatestInfosAsync(projectVersion, latestInfos, cancellationToken).ConfigureAwait(false);
        }

        private async Task PersistLatestInfosAsync(VersionStamp projectVersion, (Document, DesignerInfo? info, bool changed)[] latestInfos, CancellationToken cancellationToken)
        {
            foreach (var (doc, info, _) in latestInfos)
            {
                // Skip documents that didn't change contents/version at all.  No point in writing
                // back out the exact same data as before.
                if (info == null)
                    continue;

                using var memoryStream = new MemoryStream();
                using var writer = new ObjectWriter(memoryStream);

                PersistInfoTo(writer, info.Value, projectVersion);

                memoryStream.Position = 0;
                await _storage.WriteStreamAsync(
                    doc, DataKey, memoryStream, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<(Document, DesignerInfo? info, bool changed)[]> ComputeLatestInfosAsync(
            Project project, VersionStamp projectVersion,
            Document? specificDoc, CancellationToken cancellationToken)
        {
            var compilation = await project.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);
            var designerCategoryType = compilation.DesignerCategoryAttributeType();

            using var _1 = ArrayBuilder<Task<(Document, DesignerInfo?, bool changed)>>.GetInstance(out var tasks);
            foreach (var document in project.Documents)
            {
                // If we're only analyzing a specific document, then skip the rest.
                if (specificDoc != null && document != specificDoc)
                    continue;

                tasks.Add(ComputeDesignerAttributeInfoAsync(
                    projectVersion, designerCategoryType, document, cancellationToken));
            }

            var latestInfos = await Task.WhenAll(tasks).ConfigureAwait(false);
            return latestInfos;
        }

        private async Task<(Document, DesignerInfo?, bool changed)> ComputeDesignerAttributeInfoAsync(
            VersionStamp projectVersion, INamedTypeSymbol? designerCategoryType,
            Document document, CancellationToken cancellationToken)
        {
            // First check and see if we have stored information for this doc and if that
            // information is up to date.
            using var stream = await _storage.ReadStreamAsync(document, DataKey, cancellationToken).ConfigureAwait(false);
            using var reader = ObjectReader.TryGetReader(stream, cancellationToken: cancellationToken);
            var persisted = TryReadPersistedInfo(reader);
            if (persisted.category != null && persisted.projectVersion == projectVersion)
            {
                // We were able to read out the old data, and it matches our current project
                // version.  Just return back that nothing changed here.  We won't tell VS about
                // this, and we won't re-persist this later.
                return default;
            }

            // We either haven't computed the designer info, or our data was out of date.  We need
            // So recompute here.  Figure out what the current category is, and if that's different
            // from what we previously stored.
            var category = await DesignerAttributeHelpers.ComputeDesignerAttributeCategoryAsync(
                designerCategoryType, document, cancellationToken).ConfigureAwait(false);
            var info = new DesignerInfo
            {
                Category = category,
                DocumentId = document.Id,
            };

            return (document, info, changed: category != persisted.category);
        }
    }
}
