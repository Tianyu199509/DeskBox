using System.Globalization;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Helpers;

public sealed record CustomWidgetFirstRunSetupOutcome(
    bool Applied,
    Dictionary<string, string> Settings,
    Dictionary<string, string> Secrets);

/// <summary>
/// Fluent 2 First-Run Setup onboarding dialog for imported or community-shared Custom Widgets.
/// Guides users through configuring DPAPI-isolated API secrets (<c>type: "secret"</c>) and
/// personal local paths/preferences without touching JSON or <c>store.json</c>.
/// </summary>
public static class CustomWidgetFirstRunSetupDialogHelper
{
    public static async Task<CustomWidgetFirstRunSetupOutcome> ShowAsync(
        XamlRoot xamlRoot,
        ElementTheme requestedTheme,
        LocalizationService loc,
        IntPtr ownerHwnd,
        CustomWidgetManifest manifest,
        CustomWidgetSetupInspection inspection,
        string? activeCulture = null)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(inspection);

        var collectedSettings = new Dictionary<string, string>(StringComparer.Ordinal);
        var collectedSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!inspection.HasAnyConfigurableFields)
        {
            return new CustomWidgetFirstRunSetupOutcome(false, collectedSettings, collectedSecrets);
        }

        string culture = string.IsNullOrWhiteSpace(activeCulture) ? loc.CurrentCultureName : activeCulture;
        string widgetName = manifest.ResolveLocalizedName(culture);

        var rootStack = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            MaxWidth = 520,
            FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(loc.CurrentCultureName)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight
        };

        rootStack.Children.Add(new TextBlock
        {
            Text = loc.Format("CustomWidget.Onboarding.DialogSubtitle", widgetName),
            FontSize = 12.5,
            LineHeight = 18.5,
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap
        });

        if (inspection.Fields.Any(static f => f.IsSecret))
        {
            var vaultBanner = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 8, 11, 8),
                BorderThickness = new Thickness(1),
                Background = ResolveBrush("SubtleFillColorSecondaryBrush", Windows.UI.Color.FromArgb(0x18, 0x38, 0xBD, 0xF8), requestedTheme),
                BorderBrush = ResolveBrush("DividerStrokeColorDefaultBrush", Windows.UI.Color.FromArgb(0x28, 0x80, 0x80, 0x80), requestedTheme)
            };
            var vaultRow = new Grid { ColumnSpacing = 8 };
            vaultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            vaultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vaultRow.Children.Add(new FontIcon
            {
                Glyph = "\uE8D7",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            });
            var vaultNote = new TextBlock
            {
                Text = loc.T("CustomWidget.Onboarding.SecretVaultNotice"),
                FontSize = 11.5,
                LineHeight = 17,
                Opacity = 0.86,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(vaultNote, 1);
            vaultRow.Children.Add(vaultNote);
            vaultBanner.Child = vaultRow;
            rootStack.Children.Add(vaultBanner);
        }

        var fieldsPanel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 2, 4, 2) };
        var commitReaders = new List<Action>();

        foreach (CustomWidgetSetupFieldSpec field in inspection.Fields)
        {
            CustomWidgetSettingDefinition def = field.Definition;
            string key = def.Key;
            string label = string.IsNullOrWhiteSpace(def.Label) ? key : def.Label;

            if (field.IsSecret)
            {
                var secGroup = new StackPanel { Spacing = 4 };
                secGroup.Children.Add(BuildFieldLabel(label, field.NeedsImmediateAttention, requestedTheme));

                var pwdBox = new PasswordBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FlowDirection = FlowDirection.LeftToRight,
                    CornerRadius = new CornerRadius(6),
                    PlaceholderText = !string.IsNullOrWhiteSpace(field.CurrentValue)
                        ? CustomWidgetSecretStore.MaskSecret(field.CurrentValue)
                        : loc.T("CustomWidget.Secret.SettingsFieldPlaceholder")
                };
                AutomationProperties.SetName(pwdBox, label);
                secGroup.Children.Add(pwdBox);
                fieldsPanel.Children.Add(secGroup);

                string existingSecret = field.CurrentValue;
                commitReaders.Add(() =>
                {
                    string typed = pwdBox.Password.Trim();
                    if (!string.IsNullOrEmpty(typed))
                    {
                        collectedSecrets[key] = typed;
                    }
                    else if (!string.IsNullOrEmpty(existingSecret))
                    {
                        collectedSecrets[key] = existingSecret;
                    }
                });
                continue;
            }

            if (string.Equals(def.Type, "toggle", StringComparison.OrdinalIgnoreCase))
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var toggle = new ToggleSwitch
                {
                    MinWidth = 0,
                    OnContent = string.Empty,
                    OffContent = string.Empty,
                    IsOn = CustomWidgetExpressionEvaluator.IsTruthy(field.CurrentValue),
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(toggle, label);
                Grid.SetColumn(toggle, 1);
                row.Children.Add(toggle);
                fieldsPanel.Children.Add(row);

                commitReaders.Add(() =>
                {
                    collectedSettings[key] = toggle.IsOn ? "true" : "false";
                });
                continue;
            }

            if (def.Options.Count > 0)
            {
                IReadOnlyList<CustomWidgetOptionSpec> effectiveOpts =
                    CustomWidgetContextMenuBuilder.ResolveEffectiveSettingOptions(def, culture);
                var choiceGroup = new StackPanel { Spacing = 4 };
                choiceGroup.Children.Add(BuildFieldLabel(label, false));

                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = new CornerRadius(6)
                };
                AutomationProperties.SetName(combo, label);

                int selectedIdx = 0;
                for (int i = 0; i < effectiveOpts.Count; i++)
                {
                    CustomWidgetOptionSpec opt = effectiveOpts[i];
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = string.IsNullOrWhiteSpace(opt.Label) ? opt.Value : opt.Label,
                        Tag = opt.Value
                    });
                    if (string.Equals(opt.Value, field.CurrentValue, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIdx = i;
                    }
                }

                if (combo.Items.Count > 0)
                {
                    combo.SelectedIndex = selectedIdx;
                }

                choiceGroup.Children.Add(combo);
                fieldsPanel.Children.Add(choiceGroup);

                commitReaders.Add(() =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string selVal })
                    {
                        collectedSettings[key] = selVal;
                    }
                });
                continue;
            }

            if (string.Equals(def.Type, "number", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(def.Type, "int", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(def.Type, "integer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(def.Type, "double", StringComparison.OrdinalIgnoreCase))
            {
                double defNum = double.TryParse(def.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    ? d
                    : 0;
                double initNum = double.TryParse(field.CurrentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
                    ? c
                    : defNum;
                bool isInt = !string.Equals(def.Type, "double", StringComparison.OrdinalIgnoreCase) &&
                             Math.Abs(defNum - Math.Truncate(defNum)) < 1e-6;

                var numGroup = new StackPanel { Spacing = 4 };
                numGroup.Children.Add(BuildFieldLabel(label, false));
                var numBox = new NumberBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = new CornerRadius(6),
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    Minimum = defNum >= 0 ? 0 : -1_000_000,
                    Maximum = 1_000_000,
                    Value = initNum
                };
                AutomationProperties.SetName(numBox, label);
                numGroup.Children.Add(numBox);
                fieldsPanel.Children.Add(numGroup);

                commitReaders.Add(() =>
                {
                    if (!double.IsNaN(numBox.Value))
                    {
                        collectedSettings[key] = isInt
                            ? ((long)Math.Round(numBox.Value)).ToString(CultureInfo.InvariantCulture)
                            : numBox.Value.ToString("0.##", CultureInfo.InvariantCulture);
                    }
                });
                continue;
            }

            // Text or Path/File field
            string initialText = field.IsPathLike
                ? CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(field.CurrentValue)
                : field.CurrentValue;

            var textGroup = new StackPanel { Spacing = 4 };
            textGroup.Children.Add(BuildFieldLabel(label, field.NeedsImmediateAttention, requestedTheme));

            var textBox = new TextBox
            {
                Text = initialText,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            if (field.IsPathLike)
            {
                textBox.FlowDirection = FlowDirection.LeftToRight;
            }
            AutomationProperties.SetName(textBox, label);

            if (field.IsPathLike && ownerHwnd != IntPtr.Zero && Win32Helper.IsWindow(ownerHwnd))
            {
                var pathRow = new Grid { ColumnSpacing = 6 };
                pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pathRow.Children.Add(textBox);

                bool pickFile = field.IsFilePathLike;
                var browseBtn = new Button
                {
                    CornerRadius = new CornerRadius(6),
                    Content = loc.T(pickFile
                        ? "CustomWidget.Onboarding.BrowseFileButton"
                        : "CustomWidget.Onboarding.BrowseFolderButton")
                };
                browseBtn.Click += async (_, _) =>
                {
                    browseBtn.IsEnabled = false;
                    try
                    {
                        if (pickFile)
                        {
                            IReadOnlyList<string> files = await FileOpenPickerService.PickFilesAsync(ownerHwnd);
                            if (files.Count > 0 && !string.IsNullOrWhiteSpace(files[0]))
                            {
                                textBox.Text = files[0];
                            }
                        }
                        else
                        {
                            string? pickedFolder = await FolderPickerService.PickFolderAsync(ownerHwnd, "widget-first-run");
                            if (!string.IsNullOrWhiteSpace(pickedFolder))
                            {
                                textBox.Text = pickedFolder;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[FirstRunSetup] Picker failed: {ex.Message}");
                    }
                    finally
                    {
                        browseBtn.IsEnabled = true;
                    }
                };
                Grid.SetColumn(browseBtn, 1);
                pathRow.Children.Add(browseBtn);
                textGroup.Children.Add(pathRow);
            }
            else
            {
                textGroup.Children.Add(textBox);
            }

            fieldsPanel.Children.Add(textGroup);

            commitReaders.Add(() =>
            {
                collectedSettings[key] = textBox.Text.Trim();
            });
        }

        rootStack.Children.Add(new ScrollViewer
        {
            MaxHeight = 340,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = fieldsPanel
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = requestedTheme,
            FlowDirection = rootStack.FlowDirection,
            Title = loc.T("CustomWidget.Onboarding.DialogTitle"),
            Content = rootStack,
            PrimaryButtonText = loc.T("CustomWidget.Onboarding.ApplyButton"),
            CloseButtonText = loc.T("CustomWidget.Onboarding.SkipButton"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (Application.Current?.Resources is { } res &&
            res.TryGetValue("DefaultContentDialogStyle", out object? styleObj) &&
            styleObj is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return new CustomWidgetFirstRunSetupOutcome(false, collectedSettings, collectedSecrets);
        }

        foreach (Action readField in commitReaders)
        {
            readField();
        }

        return new CustomWidgetFirstRunSetupOutcome(true, collectedSettings, collectedSecrets);
    }

    private static UIElement BuildFieldLabel(string label, bool attentionDot, ElementTheme requestedTheme = ElementTheme.Default)
    {
        if (!attentionDot)
        {
            return new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });
        row.Children.Add(new TextBlock
        {
            Text = "•",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = ResolveBrush("AccentTextFillColorPrimaryBrush", Colors.DeepSkyBlue, requestedTheme)
        });
        return row;
    }

    private static Brush ResolveBrush(string key, Windows.UI.Color fallback, ElementTheme requestedTheme = ElementTheme.Default)
    {
        if (NeutralInteractionBrush.ResolveThemedResource(key, requestedTheme) is Brush themed)
        {
            return themed;
        }

        if (Application.Current?.Resources is { } res &&
            res.TryGetValue(key, out object? obj) &&
            obj is Brush b)
        {
            return b;
        }

        return new SolidColorBrush(fallback);
    }
}
