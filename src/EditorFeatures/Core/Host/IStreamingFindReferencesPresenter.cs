﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.FindUsages;
using Microsoft.CodeAnalysis.Navigation;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.Editor.Host;

/// <summary>
/// API for hosts to provide if they can present FindUsages results in a streaming manner.
/// i.e. if they support showing results as they are found instead of after all of the results
/// are found.
/// </summary>
internal interface IStreamingFindUsagesPresenter
{
    /// <summary>
    /// Tells the presenter that a search is starting.  The returned <see cref="FindUsagesContext"/>
    /// is used to push information about the search into.  i.e. when a reference is found
    /// <see cref="FindUsagesContext.OnReferencesFoundAsync"/> should be called.  When the
    /// search completes <see cref="FindUsagesContext.OnCompletedAsync"/> should be called. 
    /// etc. etc.
    /// </summary>
    /// <param name="title">A title to display to the user in the presentation of the results.</param>
    /// <param name="options">Options</param>
    /// <returns>A cancellation token that will be triggered if the presenter thinks the search
    /// should stop.  This can normally happen if the presenter view is closed, or recycled to
    /// start a new search in it.  Callers should only use this if they intend to report results
    /// asynchronously and thus relinquish their own control over cancellation from their own
    /// surrounding context.  If the caller intends to populate the presenter synchronously,
    /// then this cancellation token can be ignored.</returns>
    (FindUsagesContext context, CancellationToken cancellationToken) StartSearch(string title, StreamingFindUsagesPresenterOptions options);

    /// <summary>
    /// Clears all the items from the presenter.
    /// </summary>
    void ClearAll();
}

/// <summary>
/// <see cref="IStreamingFindUsagesPresenter.StartSearch(string, StreamingFindUsagesPresenterOptions)"/> options.
/// </summary>
/// <param name="SupportsReferences">
/// Whether or not showing references is supported.
/// If true, then the presenter can group by definition, showing references underneath.
/// It can also show messages about no references being found at the end of the search.
/// If false, the presenter will not group by definitions, and will show the definition
/// items in isolation.</param>
/// <param name="IncludeContainingTypeAndMemberColumns"></param>
/// <param name="IncludeKindColumn"></param>
internal readonly record struct StreamingFindUsagesPresenterOptions(
    bool SupportsReferences = false,
    bool IncludeContainingTypeAndMemberColumns = false,
    bool IncludeKindColumn = false)
{
    public static readonly StreamingFindUsagesPresenterOptions Default = new();
}

internal static class IStreamingFindUsagesPresenterExtensions
{
    public static async Task<bool> TryPresentLocationOrNavigateIfOneAsync(
        this IStreamingFindUsagesPresenter presenter,
        IThreadingContext threadingContext,
        Workspace workspace,
        string title,
        ImmutableArray<DefinitionItem> items,
        CancellationToken cancellationToken)
    {
        var location = await presenter.GetStreamingLocationAsync(
            threadingContext, workspace, title, items, cancellationToken).ConfigureAwait(false);
        return await location.TryNavigateToAsync(
            threadingContext, new NavigationOptions(PreferProvisionalTab: true, ActivateTab: true), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a navigable location that will take the user to the location there's only destination, or which will
    /// present all the locations if there are many.
    /// </summary>
    public static async Task<INavigableLocation?> GetStreamingLocationAsync(
        this IStreamingFindUsagesPresenter presenter,
        IThreadingContext threadingContext,
        Workspace workspace,
        string title,
        ImmutableArray<DefinitionItem> items,
        CancellationToken cancellationToken)
    {
        if (items.IsDefaultOrEmpty)
            return null;

        using var _ = ArrayBuilder<(DefinitionItem item, INavigableLocation location)>.GetInstance(out var builder);
        foreach (var item in items)
        {
            // Ignore any definitions that we can't navigate to.
            var navigableItem = await item.GetNavigableLocationAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (navigableItem != null)
            {
                // If there's a third party external item we can navigate to.  Defer to that item and finish.
                if (item.IsExternal)
                    return navigableItem;

                builder.Add((item, navigableItem));
            }
        }

        if (builder.Count == 0)
            return null;

        if (builder is [{ item.SourceSpans.Length: <= 1, location: var location }])
        {
            // There was only one location to navigate to.  Just directly go to that location. If we're directly
            // going to a location we need to activate the preview so that focus follows to the new cursor position.
            return location;
        }

        if (presenter == null)
            return null;

        var navigableItems = builder.SelectAsArray(t => t.item);
        return new NavigableLocation(async (options, cancellationToken) =>
        {
            // Can only navigate or present items on UI thread.
            await threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // We have multiple definitions, or we have definitions with multiple locations. Present this to the
            // user so they can decide where they want to go to.
            //
            // We ignore the cancellation token returned by StartSearch as we're in a context where
            // we've computed all the results and we're synchronously populating the UI with it.
            var (context, _) = presenter.StartSearch(title, StreamingFindUsagesPresenterOptions.Default);
            try
            {
                foreach (var item in navigableItems)
                    await context.OnDefinitionFoundAsync(item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await context.OnCompletedAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        });
    }
}
