using System;
using Avalonia.Markup.Xaml;
using NasDrive.Services;

namespace NasDrive.Mac.Views;

/// <summary>Localized text in XAML: <c>Text="{v:T Main.Add}"</c>.</summary>
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
