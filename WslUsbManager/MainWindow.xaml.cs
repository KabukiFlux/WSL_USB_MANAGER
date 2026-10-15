// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WslUsbManager.Services;
using WslUsbManager.ViewModels;

namespace WslUsbManager;

public partial class MainWindow : Window
{
    private const int MaxLogChars = 200_000;

    public MainWindow()
    {
        InitializeComponent();
        Logger.LineLogged += OnLineLogged;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Vm.InitializeAsync();

    private void OnClosed(object? sender, EventArgs e)
    {
        Logger.LineLogged -= OnLineLogged;
        Vm.Dispose();
    }

    private void OnLineLogged(string line) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (LogBox.Text.Length > MaxLogChars)
                LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

    private void OnClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows, not headers/scrollbars or the star/auto toggles.
        if (e.OriginalSource is DependencyObject src && FindParent<DataGridRow>(src) is not null &&
            FindParent<System.Windows.Controls.Primitives.ToggleButton>(src) is null &&
            Vm.ToggleAttachCommand.CanExecute(null))
            Vm.ToggleAttachCommand.Execute(null);
    }

    private void OnCustomArgsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Vm.RunCustomCommand.CanExecute(null)) Vm.RunCustomCommand.Execute(null);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
