// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Threading;
using WslUsbManager.Services;

namespace WslUsbManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode: run one usbipd command, write the result file, exit. No UI.
        if (e.Args.Length > 0 && e.Args[0] == ElevatedRunner.ElevatedSwitch)
        {
            Shutdown(ElevatedRunner.RunElevatedChild(e.Args));
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        Logger.Info($"WSL USB Manager {typeof(App).Assembly.GetName().Version} starting");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "WSL USB Manager — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
