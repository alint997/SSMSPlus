using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SSMSPlusCore.Ui.Dialogs
{
    public enum SsmsPlusMessageKind
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Small message dialog that follows the SSMS theme (light / dark), replacing the plain
    /// System.Windows.MessageBox. The message text is selectable so it can be copied.
    /// </summary>
    public static class SsmsPlusMessageBox
    {
        public static void ShowInfo(string title, string message) => Show(title, message, SsmsPlusMessageKind.Info);

        public static void ShowWarning(string title, string message) => Show(title, message, SsmsPlusMessageKind.Warning);

        public static void ShowError(string title, string message) => Show(title, message, SsmsPlusMessageKind.Error);

        public static void Show(string title, string message, SsmsPlusMessageKind kind)
        {
            try
            {
                CreateWindow(title, message, kind).ShowDialog();
            }
            catch
            {
                // last resort: the standard message box
                MessageBox.Show(title + Environment.NewLine + Environment.NewLine + message, "SSMS Plus");
            }
        }

        private static Window CreateWindow(string title, string message, SsmsPlusMessageKind kind)
        {
            var window = new Window
            {
                Title = "SSMS Plus",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
                MinWidth = 380,
                MaxWidth = 620,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5
            };

            var owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            window.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            window.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // icon (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10)
            var icon = new TextBlock
            {
                Text = GetGlyph(kind),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 30,
                Foreground = new SolidColorBrush(GetColor(kind)),
                Margin = new Thickness(0, 2, 16, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRowSpan(icon, 2);
            root.Children.Add(icon);

            var heading = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            Grid.SetColumn(heading, 1);
            root.Children.Add(heading);

            // read-only TextBox: wraps, scrolls for long texts (stack traces) and can be selected / copied
            var text = new TextBox
            {
                Text = message ?? string.Empty,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            text.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            Grid.SetRow(text, 1);
            Grid.SetColumn(text, 1);
            root.Children.Add(text);

            var ok = new Button
            {
                Content = "OK",
                IsDefault = true,
                IsCancel = true,
                MinWidth = 86,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            ok.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            ok.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            ok.Click += (s, e) => window.Close();
            Grid.SetRow(ok, 2);
            Grid.SetColumnSpan(ok, 2);
            root.Children.Add(ok);

            window.Content = root;
            window.Loaded += (s, e) => Keyboard.Focus(ok);
            return window;
        }

        private static string GetGlyph(SsmsPlusMessageKind kind)
        {
            switch (kind)
            {
                case SsmsPlusMessageKind.Warning: return ""; // Warning
                case SsmsPlusMessageKind.Error: return "";   // ErrorBadge
                default: return "";                          // Info
            }
        }

        private static Color GetColor(SsmsPlusMessageKind kind)
        {
            switch (kind)
            {
                case SsmsPlusMessageKind.Warning: return Color.FromRgb(0xE8, 0xA3, 0x17);
                case SsmsPlusMessageKind.Error: return Color.FromRgb(0xE5, 0x48, 0x48);
                default: return Color.FromRgb(0x3B, 0x8E, 0xEA);
            }
        }
    }
}
