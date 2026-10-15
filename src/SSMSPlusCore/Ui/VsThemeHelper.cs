using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace SSMSPlusCore.Ui
{
    /// <summary>
    /// Applies the SSMS / Visual Studio themed styles that are only available at runtime
    /// (they live in the shell application resources, so they cannot be referenced from XAML at compile time).
    /// </summary>
    public static class VsThemeHelper
    {
        /// <summary>
        /// Uses the themed SSMS scroll bars inside <paramref name="root"/> (DataGrid, editors, text boxes...).
        /// Does nothing outside SSMS.
        /// </summary>
        public static void ApplyThemedScrollBars(FrameworkElement root)
        {
            if (root == null)
                return;

            try
            {
                var style = (Application.Current?.TryFindResource(VsResourceKeys.ScrollBarStyleKey) ?? root.TryFindResource(VsResourceKeys.ScrollBarStyleKey)) as Style;
                if (style != null && (style.TargetType == null || style.TargetType.IsAssignableFrom(typeof(ScrollBar))))
                    root.Resources[typeof(ScrollBar)] = style;
            }
            catch (Exception)
            {
                // not running inside SSMS / Visual Studio: keep the default WPF scroll bars
            }
        }
    }
}
