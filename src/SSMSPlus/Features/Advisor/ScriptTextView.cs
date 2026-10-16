namespace SSMSPlus.Features.Advisor
{
    using ICSharpCode.AvalonEdit;
    using ICSharpCode.AvalonEdit.Highlighting;
    using Microsoft.VisualStudio.PlatformUI;
    using SSMSPlusCore.Ui.Extensions;
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    /// <summary>
    /// Read-only T-SQL text with a small toolbar (Copy, Open in query window, Read from server), used by the
    /// "Annotated query" and "Index advice" tabs of the Execution Plan Tree window. Built in code, themed.
    /// </summary>
    internal sealed class ScriptTextView : UserControl
    {
        private readonly TextEditor _editor = new TextEditor();
        private readonly TextBlock _hint = new TextBlock();
        private readonly Button _readServer;

        /// <param name="zoomKey">Key under which the zoom level is remembered (EditorZoomBehavior).</param>
        public ScriptTextView(string hint, string zoomKey)
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var root = new DockPanel { LastChildFill = true };
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            Content = root;

            var bar = new WrapPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(bar, Dock.Top);
            bar.Children.Add(MakeButton("Copy", "Copy the text to the clipboard", (s, e) => Copy()));
            bar.Children.Add(MakeButton("Open in query window", "Open the text in a new query window (it is not executed)", (s, e) =>
            {
                if (!string.IsNullOrEmpty(_editor.Text))
                    OpenScriptRequested?.Invoke(_editor.Text);
            }));
            _readServer = MakeButton("Read from server", "Read row counts, statistics and index definitions of the tables, using the connection of the query window.", (s, e) => ReadServerRequested?.Invoke());
            bar.Children.Add(_readServer);
            _hint.Text = hint;
            _hint.VerticalAlignment = VerticalAlignment.Center;
            _hint.Margin = new Thickness(10, 0, 4, 0);
            _hint.FontStyle = FontStyles.Italic;
            _hint.TextWrapping = TextWrapping.Wrap;
            _hint.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            bar.Children.Add(_hint);
            root.Children.Add(bar);

            _editor.IsReadOnly = true;
            _editor.ShowLineNumbers = false;
            _editor.WordWrap = true;
            _editor.FontFamily = new FontFamily("Consolas");
            _editor.FontSize = 12;
            _editor.Padding = new Thickness(6);
            _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
            _editor.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _editor.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            // SSMS editor font and size, zoom with Ctrl + mouse wheel / Ctrl+Shift+. / Ctrl+Shift+, / Ctrl+0 and the
            // context menu, remembered per view - like the query preview of Execution History.
            EditorZoomBehavior.SetZoomKey(_editor, zoomKey);
            root.Children.Add(_editor);
        }

        public Action<string> OpenScriptRequested { get; set; }

        public Action ReadServerRequested { get; set; }

        public void SetText(string text, bool canReadServer)
        {
            _editor.Text = text ?? string.Empty;
            _editor.ScrollToHome();
            _readServer.Visibility = canReadServer ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Copy()
        {
            if (string.IsNullOrEmpty(_editor.Text))
                return;
            try
            {
                Clipboard.SetDataObject(_editor.Text, true);
            }
            catch
            {
                // Clipboard busy: nothing else to do.
            }
        }

        private static Button MakeButton(string text, string tooltip, RoutedEventHandler click)
        {
            var b = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += click;
            return b;
        }
    }
}
