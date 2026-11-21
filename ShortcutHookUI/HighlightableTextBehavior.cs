using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShortcutHookUI;

public static class HighlightableTextBehavior
{
    public static readonly DependencyProperty FullTextProperty =
        DependencyProperty.RegisterAttached(
            "FullText",
            typeof(string),
            typeof(HighlightableTextBehavior),
            new PropertyMetadata(string.Empty, OnHighlightParamsChanged));

    public static readonly DependencyProperty HighlightPatternProperty =
        DependencyProperty.RegisterAttached(
            "HighlightPattern",
            typeof(string),
            typeof(HighlightableTextBehavior),
            new PropertyMetadata(string.Empty, OnHighlightParamsChanged));

    public static string GetFullText(DependencyObject obj) => (string)obj.GetValue(FullTextProperty);
    public static void SetFullText(DependencyObject obj, string value) => obj.SetValue(FullTextProperty, value);

    public static string GetHighlightPattern(DependencyObject obj) => (string)obj.GetValue(HighlightPatternProperty);
    public static void SetHighlightPattern(DependencyObject obj, string value) => obj.SetValue(HighlightPatternProperty, value);

    private static void OnHighlightParamsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb)
        {
            var text = GetFullText(tb);
            var query = GetHighlightPattern(tb);
            var fg = tb.Foreground ?? Br("#BBBBBB");

            tb.Inlines.Clear();
            if (string.IsNullOrEmpty(query) || !FuzzyMatch(text, query))
            {
                tb.Inlines.Add(new Run(text));
                return;
            }

            var matchPos = new bool[text.Length];
            int qi = 0;
            for (int i = 0; i < text.Length && qi < query.Length; i++)
            {
                if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(query[qi]))
                {
                    matchPos[i] = true;
                    qi++;
                }
            }

            int start = 0;
            var accentBrush = Br("#5B9CF6"); // AccentBrush matching MainWindow layout
            while (start < text.Length)
            {
                if (matchPos[start])
                {
                    int end = start;
                    while (end < text.Length && matchPos[end]) end++;
                    tb.Inlines.Add(new Run(text.Substring(start, end - start))
                    {
                        FontWeight = FontWeights.SemiBold,
                        Foreground = accentBrush
                    });
                    start = end;
                }
                else
                {
                    int end = start;
                    while (end < text.Length && !matchPos[end]) end++;
                    tb.Inlines.Add(new Run(text.Substring(start, end - start))
                    {
                        Foreground = fg
                    });
                    start = end;
                }
            }
        }
    }

    private static bool FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        int qi = 0;
        foreach (char c in text)
        {
            if (char.ToUpperInvariant(c) == char.ToUpperInvariant(query[qi]))
                if (++qi == query.Length) return true;
        }
        return false;
    }

    private static SolidColorBrush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
