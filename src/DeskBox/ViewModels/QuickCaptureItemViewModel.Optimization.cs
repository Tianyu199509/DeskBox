using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public sealed partial class QuickCaptureItemViewModel
{
    private string? _cachedTitleLine;
    private string? _cachedSummaryLine;

    public bool IsArchived => _model.ArchivedAt.HasValue;

    public DateTimeOffset? ArchivedAt => _model.ArchivedAt;

    public Visibility PinnedBadgeVisibility =>
        IsPinned && !_showPinnedSortControls ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ArchivedBadgeVisibility =>
        IsArchived ? Visibility.Visible : Visibility.Collapsed;

    public string DisplayTitleLine
    {
        get
        {
            EnsureSplitDisplayLines();
            return _cachedTitleLine!;
        }
    }

    public string DisplaySummaryText
    {
        get
        {
            EnsureSplitDisplayLines();
            return _cachedSummaryLine!;
        }
    }

    public bool HasDisplaySummary => !string.IsNullOrWhiteSpace(DisplaySummaryText);

    public Visibility DisplaySummaryVisibility =>
        HasDisplaySummary ? Visibility.Visible : Visibility.Collapsed;

    public (int Completed, int Total) ChecklistStats =>
        ContentFormat == TextContentFormat.Markdown
            ? QuickCaptureTagParser.ExtractChecklistStats(_model.Body)
            : (0, 0);

    public bool HasChecklist => ChecklistStats.Total > 0;

    public string ChecklistBadgeText
    {
        get
        {
            (int completed, int total) = ChecklistStats;
            return total > 0 ? $"{completed}/{total}" : string.Empty;
        }
    }

    public Visibility ChecklistBadgeVisibility =>
        HasChecklist ? Visibility.Visible : Visibility.Collapsed;

    public bool HasTags => Tags.Count > 0;

    public string TagsSummaryText
    {
        get
        {
            if (Tags.Count == 0)
            {
                return string.Empty;
            }

            const int maxShown = 2;
            string joined = string.Join(" ", Tags.Take(maxShown).Select(tag => $"#{tag}"));
            return Tags.Count > maxShown
                ? $"{joined} +{Tags.Count - maxShown}"
                : joined;
        }
    }

    public Visibility TagsVisibility =>
        HasTags ? Visibility.Visible : Visibility.Collapsed;

    public string SearchKeyword => _searchText;

    public string TimestampTooltipText
    {
        get
        {
            string created = _localizationService.Format(
                "QuickCapture.Detail.Created",
                _model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            if ((_model.UpdatedAt - _model.CreatedAt).Duration() < TimeSpan.FromSeconds(2))
            {
                return created;
            }

            string updated = _model.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return $"{created}\n{updated}";
        }
    }

    public void RefreshRelativeTimestamps()
    {
        OnPropertyChanged(nameof(CreatedAtText));
        OnPropertyChanged(nameof(UpdatedAtText));
    }

    internal void InvalidateSplitDisplayLines()
    {
        _cachedTitleLine = null;
        _cachedSummaryLine = null;
    }

    internal void NotifyOptimizationPropertiesChanged(QuickCaptureItem old, QuickCaptureItem current)
    {
        if (old.Body != current.Body ||
            old.Title != current.Title ||
            old.Type != current.Type ||
            old.ContentFormat != current.ContentFormat)
        {
            InvalidateSplitDisplayLines();
            OnPropertyChanged(nameof(DisplayTitleLine));
            OnPropertyChanged(nameof(DisplaySummaryText));
            OnPropertyChanged(nameof(HasDisplaySummary));
            OnPropertyChanged(nameof(DisplaySummaryVisibility));
            OnPropertyChanged(nameof(ChecklistStats));
            OnPropertyChanged(nameof(HasChecklist));
            OnPropertyChanged(nameof(ChecklistBadgeText));
            OnPropertyChanged(nameof(ChecklistBadgeVisibility));
        }

        if (old.IsPinned != current.IsPinned)
        {
            OnPropertyChanged(nameof(PinnedBadgeVisibility));
        }

        if (old.ArchivedAt != current.ArchivedAt)
        {
            OnPropertyChanged(nameof(IsArchived));
            OnPropertyChanged(nameof(ArchivedAt));
            OnPropertyChanged(nameof(ArchivedBadgeVisibility));
        }

        if (!(old.Tags ?? []).SequenceEqual(current.Tags ?? [], StringComparer.CurrentCultureIgnoreCase))
        {
            OnPropertyChanged(nameof(HasTags));
            OnPropertyChanged(nameof(TagsSummaryText));
            OnPropertyChanged(nameof(TagsVisibility));
        }

        if (old.CreatedAt != current.CreatedAt || old.UpdatedAt != current.UpdatedAt)
        {
            OnPropertyChanged(nameof(TimestampTooltipText));
        }
    }

    internal static string FormatRelativeTimestamp(
        DateTimeOffset timestamp,
        LocalizationService localizationService)
    {
        DateTimeOffset local = timestamp.ToLocalTime();
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan elapsed = now - local;

        if (elapsed >= TimeSpan.Zero)
        {
            if (elapsed.TotalSeconds < 60)
            {
                return localizationService.T("QuickCapture.JustNow");
            }

            if (elapsed.TotalMinutes < 60)
            {
                int minutes = Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes));
                return localizationService.Format("QuickCapture.MinutesAgoFormat", minutes);
            }
        }

        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm");
        }

        if (local.Year == now.Year)
        {
            return local.ToString("MM-dd HH:mm");
        }

        return local.ToString("yyyy-MM-dd HH:mm");
    }

    private void EnsureSplitDisplayLines()
    {
        if (_cachedTitleLine is not null && _cachedSummaryLine is not null)
        {
            return;
        }

        string explicitTitle = (_model.Title ?? string.Empty).Trim();
        string fullDisplay = DisplayText ?? string.Empty;
        string[] nonEmptyLines = fullDisplay
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            _cachedTitleLine = explicitTitle;
            if (nonEmptyLines.Length == 0)
            {
                _cachedSummaryLine = string.Empty;
                return;
            }

            int startIndex = string.Equals(nonEmptyLines[0], explicitTitle, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            _cachedSummaryLine = string.Join(" · ", nonEmptyLines.Skip(startIndex).Take(3));
            return;
        }

        if (nonEmptyLines.Length == 0)
        {
            _cachedTitleLine = fullDisplay.Trim();
            _cachedSummaryLine = string.Empty;
            return;
        }

        _cachedTitleLine = nonEmptyLines[0];
        _cachedSummaryLine = nonEmptyLines.Length > 1
            ? string.Join(" · ", nonEmptyLines.Skip(1).Take(3))
            : string.Empty;
    }
}
