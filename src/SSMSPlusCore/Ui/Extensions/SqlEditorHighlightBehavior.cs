using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using SSMSPlusCore.Ui.Search;
using System.Collections.Generic;
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
        private static IHighlightingDefinition _tsqlDefinition;

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

            if (_tsqlDefinition == null)
                _tsqlDefinition = HighlightingManager.Instance.GetDefinition("TSQL");

            if (editor.SyntaxHighlighting == null)
                editor.SyntaxHighlighting = _tsqlDefinition;

            renderer = new SearchMatchRenderer();
            editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
            editor.SetValue(RendererProperty, renderer);
            return renderer;
        }

        private sealed class SearchMatchRenderer : IBackgroundRenderer
        {
            private static readonly Brush MatchBrush = CreateBrush();

            public IList<TextSegment> Matches { get; set; } = new List<TextSegment>();

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
                        drawingContext.DrawRectangle(MatchBrush, null, rect);
                }
            }

            private static Brush CreateBrush()
            {
                var brush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xD7, 0x00)); // gold, like the previous viewer
                brush.Freeze();
                return brush;
            }
        }
    }
}
