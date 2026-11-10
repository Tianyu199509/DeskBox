using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public static class SettingsSegmented
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.RegisterAttached(
        "Options",
        typeof(object),
        typeof(SettingsSegmented),
        new PropertyMetadata(null, OnOptionsChanged));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value",
        typeof(object),
        typeof(SettingsSegmented),
        new PropertyMetadata(null, OnValueChanged));

    private static readonly ConditionalWeakTable<Segmented, SelectionState> s_states = new();

    public static object? GetOptions(DependencyObject element) => element.GetValue(OptionsProperty);

    public static void SetOptions(DependencyObject element, object? value) =>
        element.SetValue(OptionsProperty, value);

    public static object? GetValue(DependencyObject element) => element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, object? value) =>
        element.SetValue(ValueProperty, value);

    private static void OnOptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Segmented segmented)
        {
            GetState(segmented).RebuildItems(args.NewValue);
        }
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Segmented segmented)
        {
            GetState(segmented).QueueValueRefresh();
        }
    }

    private static SelectionState GetState(Segmented segmented) =>
        s_states.GetValue(segmented, static control => new SelectionState(control));

    private sealed class SelectionState
    {
        private static readonly Thickness ItemPadding = new(12, 5, 12, 5);

        private readonly Segmented _segmented;
        private readonly List<(INotifyPropertyChanged Notify, SettingsOption Option, SegmentedItem Item, PropertyChangedEventHandler Handler)> _subscriptions = [];
        private bool _isApplyingValue;
        private bool _isRefreshQueued;

        public SelectionState(Segmented segmented)
        {
            _segmented = segmented;
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(_segmented);
            _segmented.SelectionChanged += OnSelectionChanged;
            _segmented.Loaded += OnLoaded;
            _segmented.ActualThemeChanged += OnActualThemeChanged;
            _segmented.Unloaded += OnUnloaded;
        }

        public void RebuildItems(object? optionsSource)
        {
            DetachOptionSubscriptions();

            _isApplyingValue = true;
            try
            {
                _segmented.Items.Clear();

                if (optionsSource is IEnumerable enumerable)
                {
                    foreach (object? entry in enumerable)
                    {
                        if (entry is not SettingsOption option)
                        {
                            continue;
                        }

                        var item = new SegmentedItem
                        {
                            Content = option.DisplayName,
                            Tag = option.Value,
                            Padding = ItemPadding
                        };

                        if (entry is INotifyPropertyChanged notify)
                        {
                            PropertyChangedEventHandler handler = (_, e) =>
                            {
                                if (string.IsNullOrEmpty(e.PropertyName) ||
                                    e.PropertyName == nameof(SettingsOption.DisplayName))
                                {
                                    item.Content = option.DisplayName;
                                }
                            };
                            notify.PropertyChanged += handler;
                            _subscriptions.Add((notify, option, item, handler));
                        }

                        _segmented.Items.Add(item);
                    }
                }
            }
            finally
            {
                _isApplyingValue = false;
            }

            ApplyValueToSelection();
            QueueValueRefresh();
        }

        public void QueueValueRefresh()
        {
            if (_isRefreshQueued)
            {
                return;
            }

            if (_segmented.DispatcherQueue is not DispatcherQueue dispatcherQueue)
            {
                ApplyValueToSelection();
                return;
            }

            _isRefreshQueued = true;
            bool queued = dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    _isRefreshQueued = false;
                    ApplyValueToSelection();
                });
            if (!queued)
            {
                _isRefreshQueued = false;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(_segmented);
            ReattachOptionSubscriptionsIfNeeded();
            QueueValueRefresh();
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(_segmented);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            foreach (var (notify, _, _, handler) in _subscriptions)
            {
                notify.PropertyChanged -= handler;
            }
        }

        private void ReattachOptionSubscriptionsIfNeeded()
        {
            foreach (var (notify, option, item, handler) in _subscriptions)
            {
                notify.PropertyChanged -= handler;
                notify.PropertyChanged += handler;
                item.Content = option.DisplayName;
            }
        }

        private void DetachOptionSubscriptions()
        {
            foreach (var (notify, _, _, handler) in _subscriptions)
            {
                notify.PropertyChanged -= handler;
            }

            _subscriptions.Clear();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingValue || _segmented.SelectedItem is not SegmentedItem selectedItem)
            {
                return;
            }

            if (!Equals(GetValue(_segmented), selectedItem.Tag))
            {
                SetValue(_segmented, selectedItem.Tag);
            }
        }

        private void ApplyValueToSelection()
        {
            object? value = GetValue(_segmented);
            if (value is null)
            {
                return;
            }

            SegmentedItem? matchingItem = _segmented.Items
                .OfType<SegmentedItem>()
                .FirstOrDefault(item => Equals(item.Tag, value));
            if (matchingItem is null || ReferenceEquals(_segmented.SelectedItem, matchingItem))
            {
                return;
            }

            _isApplyingValue = true;
            try
            {
                _segmented.SelectedItem = matchingItem;
            }
            finally
            {
                _isApplyingValue = false;
            }
        }
    }
}
