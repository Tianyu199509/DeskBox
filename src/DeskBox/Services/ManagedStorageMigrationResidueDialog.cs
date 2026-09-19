using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

/// <summary>
/// Dialogs for the storage-migration leftover flows: a completed migration
/// whose old-root source folders could not be cleaned up, and a retry that
/// found the previous attempt's complete destination copy still in place.
/// Every destructive action offered here goes to the recycle bin.
/// </summary>
internal static class ManagedStorageMigrationResidueDialog
{
    /// <summary>
    /// Reports a completed migration with leftover old-root folders and
    /// offers to recycle them. Never deletes without the explicit choice.
    /// </summary>
    public static async Task ShowMigrationResidueAsync(
        XamlRoot xamlRoot,
        LocalizationService localizationService,
        Func<IEnumerable<string>, Task<int>> recycleFoldersAsync,
        ManagedStorageMigrationResult result)
    {
        IReadOnlyList<ManagedStorageMigrationResidue> residues = result.Residues;
        if (residues.Count == 0)
        {
            return;
        }

        string folderList = string.Join(
            Environment.NewLine,
            residues
                .Select(residue => Path.GetFileName(residue.SourceFolder.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)))
                .ToList());
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = localizationService.T("Settings.Dialog.MigrateResidueTitle"),
            PrimaryButtonText = localizationService.T("Settings.Dialog.MigrateResidueCleanButton"),
            CloseButtonText = localizationService.T("Settings.Dialog.MigrateResidueKeepButton"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = localizationService.Format(
                        "Settings.Dialog.MigrateResidueBody",
                        residues.Count,
                        result.OldRootPath) +
                    Environment.NewLine +
                    folderList,
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        int recycledCount = await recycleFoldersAsync(
            residues.Select(residue => residue.SourceFolder));
        if (recycledCount == residues.Count)
        {
            return;
        }

        var failureDialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = localizationService.T("Settings.Dialog.MigrateResidueCleanFailedTitle"),
            CloseButtonText = localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = localizationService.Format(
                    "Settings.Dialog.MigrateResidueCleanFailedBody",
                    residues.Count - recycledCount),
                TextWrapping = TextWrapping.Wrap
            }
        };
        await failureDialog.ShowAsync();
    }

    /// <summary>
    /// Reports a failed migration whose rollback could not return every
    /// folder: lists the folders still sitting in the new root, explains that
    /// files may now exist in both roots, and offers a conservative retry
    /// (never overwrites or deletes). Kept open until every folder is back or
    /// the user gives up (feedback #112).
    /// </summary>
    public static async Task ShowRollbackFailureAsync(
        XamlRoot xamlRoot,
        LocalizationService localizationService,
        Func<IReadOnlyList<ManagedStorageRollbackFailure>, Task<IReadOnlyList<ManagedStorageRollbackFailure>>> retryRollbackAsync,
        ManagedStorageRollbackFailureException failure)
    {
        IReadOnlyList<ManagedStorageRollbackFailure> failures = failure.Failures;
        if (failures.Count == 0)
        {
            return;
        }

        string bodyText = localizationService.Format(
            "Settings.Dialog.MigrateRollbackBody",
            failure.OriginalFailure.Message);

        while (true)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = localizationService.T("Settings.Dialog.MigrateRollbackTitle"),
                PrimaryButtonText = localizationService.T("Settings.Dialog.MigrateRollbackRetryButton"),
                CloseButtonText = localizationService.T("Common.Ok"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Spacing = 0,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = bodyText,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = string.Join(
                                Environment.NewLine,
                                failures.Select(item =>
                                    $"{item.WidgetName}: {item.DestinationFolder}")),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 8, 0, 0)
                        },
                        new TextBlock
                        {
                            Text = localizationService.T("Settings.Dialog.MigrateRollbackHint"),
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                            Opacity = 0.75,
                            Margin = new Thickness(0, 8, 0, 0)
                        },
                    }
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            failures = await retryRollbackAsync(failures);
            if (failures.Count == 0)
            {
                var doneDialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = localizationService.T("Settings.Dialog.MigrateRollbackTitle"),
                    CloseButtonText = localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = localizationService.T("Settings.Dialog.MigrateRollbackRetryComplete"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                await doneDialog.ShowAsync();
                return;
            }

            // Some folders are still stuck; redisplay with only those listed.
            bodyText = localizationService.Format(
                "Settings.Dialog.MigrateRollbackRetryPartial",
                failures.Count);
        }
    }

    /// <summary>
    /// Warns that the destination still holds the previous attempt's
    /// complete copy and offers to recycle it before retrying.
    /// </summary>
    public static async Task<ContentDialogResult> ShowStaleDestinationAsync(
        XamlRoot xamlRoot,
        LocalizationService localizationService,
        IReadOnlyList<string> staleDestinationFolders)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = localizationService.T("Settings.Dialog.MigrateStaleTitle"),
            PrimaryButtonText = localizationService.T("Settings.Dialog.MigrateStaleCleanButton"),
            CloseButtonText = localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                Text = localizationService.Format(
                        "Settings.Dialog.MigrateStaleBody",
                        staleDestinationFolders.Count) +
                    Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        staleDestinationFolders
                            .Select(path => Path.GetFileName(path.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar)))
                            .ToList()),
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync();
    }
}
