using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace NasDrive.Mac.Views;

/// <summary>Add/edit drive page.</summary>
public partial class EditorView : UserControl
{
    public EditorView() => AvaloniaXamlLoader.Load(this);
}

/// <summary>Value converters used by the views.</summary>
public static class Converters
{
    /// <summary>true → green (success), false → red (error).</summary>
    public static readonly IValueConverter OkToBrush =
        new FuncValueConverter<bool, IBrush>(ok => ok ? Brushes.MediumSeaGreen : Brushes.IndianRed);
}
