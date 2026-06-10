// Copyright (c) DeskBox. All rights reserved.

using Microsoft.UI.Xaml;

namespace DeskBox;

/// <summary>
/// Temporary main window for DeskBox.
/// Will be replaced by tray icon and desktop widget windows.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
