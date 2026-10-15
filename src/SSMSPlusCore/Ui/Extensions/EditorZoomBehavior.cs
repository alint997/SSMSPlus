using ICSharpCode.AvalonEdit;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SSMSPlusCore.Ui.Extensions
{
    /// <summary>
    /// Lets the user zoom the text of an AvalonEdit <see cref="TextEditor"/> like an SSMS query window:
    /// Ctrl + mouse wheel, Ctrl+Shift+. / Ctrl+Shift+, (SSMS shortcuts), Ctrl+Plus / Ctrl+Minus, Ctrl+0 to reset,
    /// and a context menu (Zoom In / Zoom Out / Reset Zoom, Copy, Select All).
    /// The zoom level is remembered per <c>ZoomKey</c> (HKCU\Software\SSMSPlus\Zoom).
    /// The base font family and size follow the SSMS text editor font (Tools > Options > Environment >
    /// Fonts and Colors > Text Editor), read again each time the editor is shown.
    /// </summary>
    public static class EditorZoomBehavior
    {
        private const double MinZoom = 0.5;
        private const double MaxZoom = 4.0;
        private const string RegistryPath = @"Software\SSMSPlus\Zoom";

        public static readonly DependencyProperty ZoomKeyProperty =
            DependencyProperty.RegisterAttached("ZoomKey", typeof(string), typeof(EditorZoomBehavior), new PropertyMetadata(null, OnZoomKeyChanged));

        private static readonly DependencyProperty BaseFontSizeProperty =
            DependencyProperty.RegisterAttached("BaseFontSize", typeof(double), typeof(EditorZoomBehavior), new PropertyMetadata(0d));

        private static readonly DependencyProperty ZoomProperty =
            DependencyProperty.RegisterAttached("Zoom", typeof(double), typeof(EditorZoomBehavior), new PropertyMetadata(1d));

        public static string GetZoomKey(DependencyObject obj) => (string)obj.GetValue(ZoomKeyProperty);

        public static void SetZoomKey(DependencyObject obj, string value) => obj.SetValue(ZoomKeyProperty, value);

        private static void OnZoomKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextEditor editor) || e.OldValue != null)
                return;

            editor.SetValue(BaseFontSizeProperty, editor.FontSize > 0 ? editor.FontSize : 12d);
            editor.PreviewMouseWheel += OnPreviewMouseWheel;
            editor.PreviewKeyDown += OnPreviewKeyDown;
            editor.ContextMenu = BuildContextMenu(editor);
            editor.Loaded += (s, args) => ApplySsmsFont(editor);
            ApplySsmsFont(editor);
            ApplyZoom(editor, LoadZoom((string)e.NewValue), save: false);
        }

        /// <summary>Uses the font family and size of the SSMS text editor as the base (100%) font.</summary>
        private static void ApplySsmsFont(TextEditor editor)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!(Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte))
                    return;

                var properties = dte.Properties["FontsAndColors", "TextEditor"];
                if (properties == null)
                    return;

                var family = properties.Item("FontFamily")?.Value as string;
                var sizeValue = properties.Item("FontSize")?.Value;
                if (!string.IsNullOrWhiteSpace(family))
                    editor.FontFamily = new FontFamily(family);

                if (sizeValue != null)
                {
                    // Points (as in the SSMS options) to WPF device independent pixels.
                    double points = Convert.ToDouble(sizeValue, CultureInfo.InvariantCulture);
                    if (points > 0)
                    {
                        editor.SetValue(BaseFontSizeProperty, points * 96.0 / 72.0);
                        ApplyZoom(editor, (double)editor.GetValue(ZoomProperty), save: false);
                    }
                }
            }
            catch
            {
                // Not available (e.g. outside SSMS): keep the font from the XAML.
            }
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || !(sender is TextEditor editor))
                return;

            Step(editor, e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(sender is TextEditor editor) || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.OemPeriod when shift:
                case Key.OemPlus:
                case Key.Add:
                    Step(editor, 1);
                    e.Handled = true;
                    break;
                case Key.OemComma when shift:
                case Key.OemMinus:
                case Key.Subtract:
                    Step(editor, -1);
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    ApplyZoom(editor, 1, save: true);
                    e.Handled = true;
                    break;
            }
        }

        private static void Step(TextEditor editor, int direction)
        {
            double zoom = (double)editor.GetValue(ZoomProperty);
            zoom = direction > 0 ? zoom * 1.1 : zoom / 1.1;
            // Snap to 100% when passing close to it.
            if (Math.Abs(zoom - 1) < 0.04)
                zoom = 1;
            ApplyZoom(editor, zoom, save: true);
        }

        private static void ApplyZoom(TextEditor editor, double zoom, bool save)
        {
            zoom = Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
            editor.SetValue(ZoomProperty, zoom);
            double baseSize = (double)editor.GetValue(BaseFontSizeProperty);
            editor.FontSize = Math.Round(baseSize * zoom, 1);
            editor.ToolTip = null;

            if (save)
                SaveZoom(GetZoomKey(editor), zoom);
        }

        private static ContextMenu BuildContextMenu(TextEditor editor)
        {
            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
            copy.Click += (s, e) => editor.Copy();
            var selectAll = new MenuItem { Header = "Select All", InputGestureText = "Ctrl+A" };
            selectAll.Click += (s, e) => editor.SelectAll();
            var zoomIn = new MenuItem { Header = "Zoom In", InputGestureText = "Ctrl+Shift+." };
            zoomIn.Click += (s, e) => Step(editor, 1);
            var zoomOut = new MenuItem { Header = "Zoom Out", InputGestureText = "Ctrl+Shift+," };
            zoomOut.Click += (s, e) => Step(editor, -1);
            var reset = new MenuItem { InputGestureText = "Ctrl+0" };
            reset.Click += (s, e) => ApplyZoom(editor, 1, save: true);

            menu.Items.Add(copy);
            menu.Items.Add(selectAll);
            menu.Items.Add(new Separator());
            menu.Items.Add(zoomIn);
            menu.Items.Add(zoomOut);
            menu.Items.Add(reset);
            menu.Opened += (s, e) =>
            {
                double zoom = (double)editor.GetValue(ZoomProperty);
                reset.Header = "Reset Zoom (" + Math.Round(zoom * 100).ToString(CultureInfo.InvariantCulture) + "%)";
                copy.IsEnabled = editor.SelectionLength > 0;
            };
            return menu;
        }

        private static double LoadZoom(string key)
        {
            try
            {
                using (var reg = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    var value = reg?.GetValue(key) as string;
                    if (value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom) && zoom > 0)
                        return zoom;
                }
            }
            catch
            {
            }

            return 1;
        }

        private static void SaveZoom(string key, double zoom)
        {
            if (string.IsNullOrEmpty(key))
                return;
            try
            {
                using (var reg = Registry.CurrentUser.CreateSubKey(RegistryPath))
                    reg?.SetValue(key, zoom.ToString("0.###", CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }
    }
}
