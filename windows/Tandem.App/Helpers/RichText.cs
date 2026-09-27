using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Tandem.App;

/// <summary>Builds wrapped TextBlocks from light markup: **bold** segments, everything else plain.</summary>
internal static class RichText
{
    public static TextBlock Block(string markup, Style? style = null)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (style is not null) block.Style = style;
        var bold = false;
        foreach (var part in markup.Split("**"))
        {
            if (part.Length > 0)
            {
                var run = new Run { Text = part };
                if (bold) run.FontWeight = FontWeights.SemiBold;
                block.Inlines.Add(run);
            }
            bold = !bold;
        }
        return block;
    }
}
