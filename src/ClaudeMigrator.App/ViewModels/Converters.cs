using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ClaudeMigrator.App.ViewModels;

public sealed class StepHighlightConverter : IValueConverter
{
    public static readonly StepHighlightConverter Instance = new();

    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.Parse("#1f6feb"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#0F1014"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isCurrent && isCurrent ? HighlightBrush : DimBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool boolean && boolean;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
