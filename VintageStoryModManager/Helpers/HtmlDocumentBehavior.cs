using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace VintageStoryModManager.Helpers;

/// <summary>
/// Attaches an HTML string to a <see cref="FlowDocumentScrollViewer"/> and renders
/// it as a rich <see cref="FlowDocument"/> using <see cref="HtmlToFlowDocument"/>.
/// </summary>
public static class HtmlDocumentBehavior
{
    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.RegisterAttached(
            "Html",
            typeof(string),
            typeof(HtmlDocumentBehavior),
            new PropertyMetadata(null, OnHtmlChanged));

    public static string? GetHtml(DependencyObject obj)
    {
        return (string?)obj.GetValue(HtmlProperty);
    }

    public static void SetHtml(DependencyObject obj, string? value)
    {
        obj.SetValue(HtmlProperty, value);
    }

    private static void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FlowDocumentScrollViewer viewer)
            return;

        var html = e.NewValue as string;
        Brush? foreground = viewer.Foreground;
        FontFamily? fontFamily = viewer.FontFamily;
        var fontSize = viewer.FontSize;

        viewer.Document = HtmlToFlowDocument.Convert(html, foreground, fontFamily, fontSize);
    }
}
