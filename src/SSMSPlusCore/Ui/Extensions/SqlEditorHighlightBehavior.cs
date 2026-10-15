using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using SSMSPlusCore.Ui.Search;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Controls;
using System.Xml;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SSMSPlusCore.Ui.Extensions
{
    /// <summary>
    /// Shows a <see cref="TextFragments"/> (query text + search matches) in an AvalonEdit <see cref="TextEditor"/>
    /// with T-SQL syntax highlighting; the fragments of type Highlight are painted in gold
    /// (upstream issue akarzazi/SSMSPlus#4).
    /// </summary>
    public static class SqlEditorHighlightBehavior
    {
        private static IHighlightingDefinition _tsqlLight;
        private static IHighlightingDefinition _tsqlDark;

        public static TextFragments GetHighlightContext(DependencyObject obj)
        {
            return (TextFragments)obj.GetValue(HighlightContextProperty);
        }

        public static void SetHighlightContext(DependencyObject obj, TextFragments value)
        {
            obj.SetValue(HighlightContextProperty, value);
        }

        public static readonly DependencyProperty HighlightContextProperty =
            DependencyProperty.RegisterAttached("HighlightContext",
                typeof(TextFragments),
                typeof(SqlEditorHighlightBehavior),
                new PropertyMetadata(null, OnHighlightContextChanged));

        private static readonly DependencyProperty RendererProperty =
            DependencyProperty.RegisterAttached("Renderer",
                typeof(SearchMatchRenderer),
                typeof(SqlEditorHighlightBehavior),
                new PropertyMetadata(null));

        private static void OnHighlightContextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextEditor editor))
                return;

            var renderer = EnsureSetup(editor);

            var context = e.NewValue as TextFragments;
            if (context == null)
            {
                renderer.Matches = new List<TextSegment>();
                editor.Text = string.Empty;
                return;
            }

            var matches = new List<TextSegment>();
            var text = new System.Text.StringBuilder();
            foreach (var fragment in context.Fragments)
            {
                if (fragment.FragmentType == TextFragmentType.Highlight && fragment.Value.Length > 0)
                    matches.Add(new TextSegment { StartOffset = text.Length, Length = fragment.Value.Length });

                text.Append(fragment.Value);
            }

            renderer.Matches = matches;
            editor.Text = text.ToString();
            editor.TextArea.TextView.Redraw();

            if (matches.Count > 0)
            {
                var location = editor.Document.GetLocation(matches[0].StartOffset);
                editor.ScrollTo(location.Line, location.Column);
            }
            else
            {
                editor.ScrollToHome();
            }
        }

        private static SearchMatchRenderer EnsureSetup(TextEditor editor)
        {
            var renderer = (SearchMatchRenderer)editor.GetValue(RendererProperty);
            if (renderer != null)
                return renderer;

            renderer = new SearchMatchRenderer();
            editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
            editor.SetValue(RendererProperty, renderer);

            ApplyTheme(editor, renderer);

            // the Background comes from the SSMS theme (DynamicResource): follow theme changes
            var descriptor = DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(TextEditor));
            descriptor?.AddValueChanged(editor, (s, e) => ApplyTheme(editor, renderer));

            return renderer;
        }

        /// <summary>
        /// Light themes use the AvalonEdit T-SQL colors, dark themes use colors close to the SSMS dark editor.
        /// </summary>
        private static void ApplyTheme(TextEditor editor, SearchMatchRenderer renderer)
        {
            var dark = IsDark(editor.Background);
            editor.SyntaxHighlighting = dark ? GetDarkDefinition() : GetLightDefinition();
            renderer.Dark = dark;
            editor.TextArea.TextView.Redraw();
        }

        private static bool IsDark(Brush background)
        {
            if (!(background is SolidColorBrush solid))
                return false;

            var c = solid.Color;
            var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return luminance < 0.5;
        }

        private static IHighlightingDefinition GetLightDefinition()
        {
            if (_tsqlLight == null)
                _tsqlLight = HighlightingManager.Instance.GetDefinition("TSQL");
            return _tsqlLight;
        }

        private static IHighlightingDefinition GetDarkDefinition()
        {
            if (_tsqlDark != null)
                return _tsqlDark;

            try
            {
                // same T-SQL definition as AvalonEdit, with the colors of the SSMS / VS dark editor
                var stream = typeof(TextEditor).Assembly.GetManifestResourceStream("ICSharpCode.AvalonEdit.Highlighting.Resources.TSQL-Mode.xshd");
                if (stream == null)
                    return GetLightDefinition();

                XshdSyntaxDefinition xshd;
                using (stream)
                using (var reader = new XmlTextReader(stream))
                {
                    xshd = HighlightingLoader.LoadXshd(reader);
                }

                foreach (var color in xshd.Elements.OfType<XshdColor>())
                {
                    switch (color.Name)
                    {
                        case "Comment": color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(0x57, 0xA6, 0x4A)); break;
                        case "Char": color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(0xD6, 0x9D, 0x85)); break;
                        case "Keywords": color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(0x56, 0x9C, 0xD6)); break;
                    }
                }

                _tsqlDark = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
            }
            catch (Exception)
            {
                _tsqlDark = GetLightDefinition();
            }

            return _tsqlDark;
        }

        private sealed class SearchMatchRenderer : IBackgroundRenderer
        {
            private static readonly Brush LightMatchBrush = CreateBrush(Color.FromArgb(0xB0, 0xFF, 0xD7, 0x00)); // gold
            private static readonly Brush DarkMatchBrush = CreateBrush(Color.FromArgb(0xC0, 0x7A, 0x5C, 0x12)); // dark gold, readable with light text

            public IList<TextSegment> Matches { get; set; } = new List<TextSegment>();

            public bool Dark { get; set; }

            public KnownLayer Layer => KnownLayer.Selection;

            public void Draw(TextView textView, DrawingContext drawingContext)
            {
                if (Matches == null || Matches.Count == 0 || textView.Document == null)
                    return;

                textView.EnsureVisualLines();
                var documentLength = textView.Document.TextLength;
                foreach (var match in Matches.Where(m => m.EndOffset <= documentLength))
                {
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, match))
                        drawingContext.DrawRectangle(Dark ? DarkMatchBrush : LightMatchBrush, null, rect);
                }
            }

            private static Brush CreateBrush(Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
    }
}
