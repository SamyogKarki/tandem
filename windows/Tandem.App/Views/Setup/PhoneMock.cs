using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Tandem.App.Views.Setup;

public enum MockEnd { None, Chevron, SwitchOff, SwitchOn }

/// <summary>One row on the drawn settings screen; the highlighted row gets an accent outline and a hint badge.</summary>
public sealed record MockRow(string Text, MockEnd End = MockEnd.None, bool Highlight = false, string? Badge = null, string? Detail = null);

/// <summary>
/// A simple drawing of a phone showing a Settings screen, with the thing to tap highlighted.
/// Drawn rather than screenshotted: stays crisp, follows light/dark theme, and fits every brand.
/// </summary>
public sealed class PhoneMock : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 18, 16, 14), TextWrapping = TextWrapping.Wrap };

    public PhoneMock()
    {
        var screen = new StackPanel();
        screen.Children.Add(new Border // camera / notch
        {
            Width = 44, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, Background = Brush("TextFillColorTertiaryBrush"),
        });
        screen.Children.Add(_title);
        screen.Children.Add(_rows);

        Content = new Border
        {
            Width = 256,
            Height = 440,
            CornerRadius = new CornerRadius(30),
            BorderThickness = new Thickness(7),
            BorderBrush = Brush("ControlStrongStrokeColorDefaultBrush"),
            Background = Brush("SolidBackgroundFillColorBaseBrush"),
            Child = screen,
        };
        // Decorative: the step text next to it says the same thing for screen readers.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    public void Show(string title, IEnumerable<MockRow> rows)
    {
        _title.Text = title;
        _rows.Children.Clear();
        foreach (var row in rows) _rows.Children.Add(BuildRow(row));
    }

    private static UIElement BuildRow(MockRow row)
    {
        var grid = new Grid { Padding = new Thickness(14, 11, 14, 11), ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = row.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, FontWeight = row.Highlight ? FontWeights.SemiBold : FontWeights.Normal });
        if (row.Detail is not null)
            texts.Children.Add(new TextBlock { Text = row.Detail, FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(texts);

        if (End(row.End) is { } end)
        {
            Grid.SetColumn(end, 1);
            grid.Children.Add(end);
        }

        var container = new Grid();
        container.Children.Add(new Border
        {
            Margin = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(row.Highlight ? 2 : 0),
            BorderBrush = Brush("AccentFillColorDefaultBrush"),
            Background = row.Highlight ? Brush("SubtleFillColorSecondaryBrush") : null,
            Child = grid,
        });
        if (row.Badge is not null)
        {
            // The hint ("Tap 7 times") sits on the row's top edge, like a callout.
            container.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -10, 16, 0),
                Padding = new Thickness(8, 2, 8, 3),
                CornerRadius = new CornerRadius(8),
                Background = Brush("AccentFillColorDefaultBrush"),
                Child = new TextBlock { Text = row.Badge, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextOnAccentFillColorPrimaryBrush") },
            });
        }
        return container;
    }

    private static FrameworkElement? End(MockEnd end) => end switch
    {
        MockEnd.Chevron => new FontIcon { Glyph = "", FontSize = 12, Foreground = Brush("TextFillColorTertiaryBrush") },
        MockEnd.SwitchOff or MockEnd.SwitchOn => Switch(end == MockEnd.SwitchOn),
        _ => null,
    };

    private static FrameworkElement Switch(bool on)
    {
        var knob = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 0),
            Background = on ? Brush("TextOnAccentFillColorPrimaryBrush") : Brush("TextFillColorSecondaryBrush"),
        };
        return new Border
        {
            Width = 38, Height = 20, CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(on ? 0 : 1),
            BorderBrush = Brush("TextFillColorSecondaryBrush"),
            Background = on ? Brush("AccentFillColorDefaultBrush") : null,
            Child = knob,
        };
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
