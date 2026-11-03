// Portions adapted from Microsoft PowerToys (KeyVisual / KeyCharPresenter),
// Copyright (c) Microsoft Corporation, licensed under the MIT License.

using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DeskBox.Controls;

public enum KeyVisualState
{
    Normal,
    Error,
    Warning
}

public enum KeyVisualSize
{
    Hero,
    Compact
}

public enum KeyVisualTokenKind
{
    Text,
    Glyph,
    Windows,
    Copilot,
    Badge
}

public readonly record struct KeyVisualToken(
    KeyVisualTokenKind Kind,
    string Text)
{
    public static KeyVisualToken Windows => new(KeyVisualTokenKind.Windows, "Win");
    public static KeyVisualToken Copilot => new(KeyVisualTokenKind.Copilot, "Copilot");
    public static KeyVisualToken FromText(string text) => new(KeyVisualTokenKind.Text, text);
    public static KeyVisualToken FromGlyph(string glyph, string accessibleName) => new(KeyVisualTokenKind.Glyph, glyph + "|" + accessibleName);
    public static KeyVisualToken FromBadge(string badgeText) => new(KeyVisualTokenKind.Badge, badgeText);
}

public sealed partial class KeyVisualControl : UserControl
{
    private KeyVisualToken _token = KeyVisualToken.FromText(string.Empty);
    private KeyVisualState _state = KeyVisualState.Normal;
    private KeyVisualSize _size = KeyVisualSize.Hero;

    public KeyVisualControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsEnabledChanged += OnIsEnabledChanged;
    }

    public KeyVisualToken Token
    {
        get => _token;
        set
        {
            _token = value;
            ApplyContentAndSize();
            ApplyVisualState();
        }
    }

    public KeyVisualState State
    {
        get => _state;
        set
        {
            _state = value;
            ApplyVisualState();
        }
    }

    public KeyVisualSize VisualSize
    {
        get => _size;
        set
        {
            _size = value;
            ApplyContentAndSize();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyContentAndSize();
        ApplyVisualState();
    }

    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ApplyVisualState();
    }

    private void ApplyContentAndSize()
    {
        bool isHero = _size == KeyVisualSize.Hero;
        bool isBadge = _token.Kind == KeyVisualTokenKind.Badge;
        double fontSize = isHero
            ? (isBadge ? 14 : 16)
            : (isBadge ? 11 : 12);

        KeyHolder.MinWidth = isHero
            ? (isBadge ? 42 : 52)
            : (isBadge ? 28 : 34);
        KeyHolder.MinHeight = isHero
            ? (isBadge ? 38 : 48)
            : (isBadge ? 24 : 28);
        KeyHolder.Padding = isHero
            ? (isBadge ? new Thickness(12, 8, 12, 8) : new Thickness(20, 14, 20, 14))
            : (isBadge ? new Thickness(6, 4, 6, 4) : new Thickness(8, 5, 8, 5));
        KeyHolder.CornerRadius = new CornerRadius(isHero ? 8 : 4);

        KeyTextBlock.FontSize = fontSize;
        KeyFontIcon.FontSize = fontSize;
        KeyGlyphBox.Width = fontSize;
        KeyGlyphBox.Height = fontSize;
        WindowsIconBox.Width = fontSize;
        WindowsIconBox.Height = fontSize;
        CopilotIconBox.Width = fontSize;
        CopilotIconBox.Height = fontSize;
        CopilotLabel.FontSize = fontSize;

        KeyTextBlock.Visibility = Visibility.Collapsed;
        KeyGlyphBox.Visibility = Visibility.Collapsed;
        WindowsIconBox.Visibility = Visibility.Collapsed;
        CopilotPanel.Visibility = Visibility.Collapsed;

        switch (_token.Kind)
        {
            case KeyVisualTokenKind.Windows:
                WindowsIconBox.Visibility = Visibility.Visible;
                AutomationProperties.SetName(this, "Windows");
                break;

            case KeyVisualTokenKind.Copilot:
                CopilotPanel.Visibility = Visibility.Visible;
                AutomationProperties.SetName(this, "Copilot");
                break;

            case KeyVisualTokenKind.Glyph:
            {
                int sep = _token.Text.IndexOf('|');
                string glyph = sep >= 0 ? _token.Text[..sep] : _token.Text;
                string name = sep >= 0 ? _token.Text[(sep + 1)..] : _token.Text;
                KeyFontIcon.Glyph = glyph;
                KeyGlyphBox.Visibility = Visibility.Visible;
                AutomationProperties.SetName(this, name);
                break;
            }

            default:
                KeyTextBlock.Text = _token.Text;
                KeyTextBlock.Visibility = Visibility.Visible;
                AutomationProperties.SetName(this, _token.Text);
                break;
        }
    }

    private void ApplyVisualState()
    {
        bool isBadge = _token.Kind == KeyVisualTokenKind.Badge;
        string stateName;
        if (isBadge)
        {
            stateName = _state == KeyVisualState.Error
                ? "BadgeInvalidState"
                : !IsEnabled
                    ? "BadgeDisabledState"
                    : "BadgeNormalState";
        }
        else if (_state == KeyVisualState.Error)
        {
            stateName = "InvalidState";
        }
        else if (_state == KeyVisualState.Warning)
        {
            stateName = "WarningState";
        }
        else if (!IsEnabled)
        {
            stateName = "DisabledState";
        }
        else
        {
            stateName = "NormalState";
        }

        VisualStateManager.GoToState(this, stateName, useTransitions: true);
    }

    public static KeyVisualControl CreateHeroVisual(KeyVisualToken token, KeyVisualState state = KeyVisualState.Normal)
    {
        return new KeyVisualControl
        {
            VisualSize = KeyVisualSize.Hero,
            Token = token,
            State = state
        };
    }

    public static KeyVisualControl CreateCompactVisual(KeyVisualToken token, KeyVisualState state = KeyVisualState.Normal)
    {
        return new KeyVisualControl
        {
            VisualSize = KeyVisualSize.Compact,
            Token = token,
            State = state
        };
    }

    public static FrameworkElement CreateInlineButtonContent(
        GlobalHotkeyActivation activation,
        string fallbackText,
        bool showEditIcon = true,
        KeyVisualState state = KeyVisualState.Normal,
        string doubleTapBadgeText = "×2")
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        IReadOnlyList<KeyVisualToken> tokens = BuildTokens(activation, doubleTapBadgeText);
        if (tokens.Count == 0)
        {
            panel.Children.Add(CreateCompactVisual(KeyVisualToken.FromText(fallbackText), state));
        }
        else
        {
            foreach (KeyVisualToken token in tokens)
            {
                panel.Children.Add(CreateCompactVisual(token, state));
            }
        }

        if (showEditIcon)
        {
            panel.Children.Add(new FontIcon
            {
                Glyph = "\uE70F",
                FontSize = 12,
                Opacity = 0.72,
                Margin = new Thickness(4, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        return panel;
    }

    public static IReadOnlyList<KeyVisualToken> BuildTokens(
        GlobalHotkeyActivation activation,
        string doubleTapBadgeText = "×2")
    {
        switch (activation.Kind)
        {
            case HotkeyActivationKind.DoubleControl:
                return
                [
                    KeyVisualToken.FromText("Ctrl"),
                    KeyVisualToken.FromBadge(doubleTapBadgeText)
                ];

            case HotkeyActivationKind.WindowsTap:
                return [KeyVisualToken.Windows];

            case HotkeyActivationKind.DoubleTap when activation.IsModifierOnlyDoubleTap:
                return
                [
                    CreateModifierToken(activation.DoubleTapModifier),
                    KeyVisualToken.FromBadge(doubleTapBadgeText)
                ];

            case HotkeyActivationKind.DoubleTap:
                return BuildLiveCaptureTokens(
                    activation.Gesture.Modifiers,
                    activation.Gesture.VirtualKey,
                    isDoubleTap: true,
                    doubleTapBadgeText);

            default:
                return BuildLiveCaptureTokens(
                    activation.Gesture.Modifiers,
                    activation.Gesture.VirtualKey,
                    isDoubleTap: false,
                    doubleTapBadgeText);
        }
    }

    public static IReadOnlyList<KeyVisualToken> BuildLiveCaptureTokens(
        HotkeyModifierKeys modifiers,
        int primaryKey,
        bool isDoubleTap,
        string doubleTapBadgeText = "×2")
    {
        var tokens = new List<KeyVisualToken>(6);
        var gesture = new GlobalHotkeyGesture(modifiers, primaryKey);
        if (primaryKey > 0 && gesture.Equals(GlobalHotkeyService.CopilotKeyGesture))
        {
            tokens.Add(KeyVisualToken.Copilot);
            if (isDoubleTap)
            {
                tokens.Add(KeyVisualToken.FromBadge(doubleTapBadgeText));
            }

            return tokens;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Windows))
        {
            tokens.Add(KeyVisualToken.Windows);
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            tokens.Add(KeyVisualToken.FromText("Ctrl"));
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            tokens.Add(KeyVisualToken.FromText("Alt"));
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            tokens.Add(KeyVisualToken.FromText("Shift"));
        }

        if (primaryKey > 0)
        {
            tokens.Add(CreateVirtualKeyToken(primaryKey));
        }

        if (tokens.Count > 0 && isDoubleTap)
        {
            tokens.Add(KeyVisualToken.FromBadge(doubleTapBadgeText));
        }

        return tokens;
    }

    private static KeyVisualToken CreateModifierToken(HotkeyModifierKeys modifier)
    {
        return modifier switch
        {
            HotkeyModifierKeys.Windows => KeyVisualToken.Windows,
            HotkeyModifierKeys.Control => KeyVisualToken.FromText("Ctrl"),
            HotkeyModifierKeys.Alt => KeyVisualToken.FromText("Alt"),
            HotkeyModifierKeys.Shift => KeyVisualToken.FromText("Shift"),
            _ => KeyVisualToken.FromText(modifier.ToString())
        };
    }

    private static KeyVisualToken CreateVirtualKeyToken(int virtualKey)
    {
        return ((VirtualKey)virtualKey) switch
        {
            VirtualKey.Enter => KeyVisualToken.FromGlyph("\uE751", "Enter"),
            VirtualKey.Back => KeyVisualToken.FromGlyph("\uE750", "Backspace"),
            VirtualKey.Left => KeyVisualToken.FromGlyph("\uE0E2", "Left"),
            VirtualKey.Right => KeyVisualToken.FromGlyph("\uE0E3", "Right"),
            VirtualKey.Up => KeyVisualToken.FromGlyph("\uE0E4", "Up"),
            VirtualKey.Down => KeyVisualToken.FromGlyph("\uE0E5", "Down"),
            _ => KeyVisualToken.FromText(GlobalHotkeyService.FormatVirtualKey(virtualKey))
        };
    }
}
