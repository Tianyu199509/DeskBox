using System.Runtime.CompilerServices;
using DeskBox.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public static class SettingsComboBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value",
        typeof(object),
        typeof(SettingsComboBox),
        new PropertyMetadata(null, OnValueChanged));

    private static readonly ConditionalWeakTable<ComboBox, SelectionState> s_states = new();

    public static object? GetValue(DependencyObject element) => element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, object? value) =>
        element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is ComboBox comboBox)
        {
            GetState(comboBox).QueueValueRefresh();
        }
    }

    private static SelectionState GetState(ComboBox comboBox) =>
        s_states.GetValue(comboBox, static combo => new SelectionState(combo));

    private sealed class SelectionState
    {
        private readonly ComboBox _comboBox;
        private bool _isApplyingValue;
        private bool _isRefreshQueued;

        public SelectionState(ComboBox comboBox)
        {
            _comboBox = comboBox;
            _comboBox.DisplayMemberPath = nameof(SettingsOption.DisplayName);
            _comboBox.SelectionChanged += OnSelectionChanged;
            _comboBox.Loaded += OnLoaded;
            _comboBox.RegisterPropertyChangedCallback(
                ItemsControl.ItemsSourceProperty,
                OnItemsSourceChanged);
        }

        public void QueueValueRefresh()
        {
            if (_isRefreshQueued)
            {
                return;
            }

            _isRefreshQueued = true;
            bool queued = _comboBox.DispatcherQueue.TryEnqueue(
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
            QueueValueRefresh();
        }

        private void OnItemsSourceChanged(DependencyObject sender, DependencyProperty property)
        {
            QueueValueRefresh();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingValue || _comboBox.SelectedItem is not SettingsOption option)
            {
                return;
            }

            if (!Equals(GetValue(_comboBox), option.Value))
            {
                SetValue(_comboBox, option.Value);
            }
        }

        private void ApplyValueToSelection()
        {
            object? value = GetValue(_comboBox);
            if (value is null)
            {
                return;
            }

            SettingsOption? matchingOption = _comboBox.Items
                .OfType<SettingsOption>()
                .FirstOrDefault(option => Equals(option.Value, value));
            if (matchingOption is null || ReferenceEquals(_comboBox.SelectedItem, matchingOption))
            {
                return;
            }

            _isApplyingValue = true;
            try
            {
                _comboBox.SelectedItem = matchingOption;
            }
            finally
            {
                _isApplyingValue = false;
            }
        }
    }
}
