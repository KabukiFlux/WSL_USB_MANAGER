// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WslUsbManager.Models;

namespace WslUsbManager.Infrastructure;

public sealed class FamilyToBrushConverter : IValueConverter
{
    private static Brush B(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    private static readonly Dictionary<DetectedFamily, Brush> Brushes = new()
    {
        [DetectedFamily.Arduino] = B("#00878F"),
        [DetectedFamily.EspressifEsp32] = B("#D7262F"),
        [DetectedFamily.Stm32] = B("#03234B"),
        [DetectedFamily.RaspberryPiPico] = B("#C51A4A"),
        [DetectedFamily.Fpga] = B("#E07B00"),
        [DetectedFamily.GenericSerialBridge] = B("#6A4C93"),
    };
    private static readonly Brush OtherBrush = B("#8A8F98");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DetectedFamily f && Brushes.TryGetValue(f, out var b) ? b : OtherBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

public sealed class StateToBrushConverter : IValueConverter
{
    private static Brush B(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    private static readonly Brush Attached = B("#2E9E44");
    private static readonly Brush Shared = B("#E0A100");
    private static readonly Brush NotShared = B("#9AA0A6");
    private static readonly Brush Offline = B("#D0D3D8");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Attached" => Attached,
        "Shared" => Shared,
        "Offline" => Offline,
        _ => NotShared,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
