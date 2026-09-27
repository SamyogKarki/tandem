using Microsoft.UI.Xaml;

namespace Tandem.App;

/// <summary>Small functions for x:Bind, so XAML doesn't need converter classes.</summary>
public static class Ui
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
}
