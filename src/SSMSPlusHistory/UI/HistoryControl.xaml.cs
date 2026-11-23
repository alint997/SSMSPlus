using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using SSMSPlusCore.Di;
using SSMSPlusHistory.UI.VM;

namespace SSMSPlusHistory.UI
{
    /// <summary>
    /// Interaction logic for Simple.xaml
    /// </summary>
    public partial class HistoryControl : UserControl
    {
        public HistoryControl()
        {
            InitializeComponent();
            SSMSPlusCore.Ui.VsThemeHelper.ApplyThemedScrollBars(this);
            var vm = ServiceLocator.GetRequiredService<HistoryControlVM>();
            this.DataContext = vm;
            this.Loaded += HistoryControl_Loaded;
            vm.ItemDeleted += FocusRow;
        }

        /// <summary>
        /// After a delete the grid gets a new list and the focused row is gone: give the keyboard focus to the
        /// row selected in its place (once the rows are generated), so Delete / arrows keep working.
        /// </summary>
        private void FocusRow(SearchFilterResultVM item)
        {
            var grid = objectsGrid.Children.OfType<DataGrid>().FirstOrDefault();
            if (grid == null)
                return;

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await Task.Yield(); // after the grid has taken the new list

                if (item == null || !grid.Items.Contains(item))
                {
                    grid.Focus();
                    return;
                }

                grid.SelectedItem = item;
                grid.ScrollIntoView(item);
                grid.UpdateLayout();
                if (grid.Columns.Count > 0)
                    grid.CurrentCell = new DataGridCellInfo(item, grid.Columns[0]);

                var row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                var cell = row != null && grid.Columns.Count > 0 ? grid.Columns[0].GetCellContent(row)?.Parent as DataGridCell : null;
                if (cell != null)
                    cell.Focus();
                else if (row != null)
                    row.Focus();
                else
                    grid.Focus();
            });
        }

        private void HistoryControl_Loaded(object sender, RoutedEventArgs e)
        {
            QueryFilter.Focus();
            this.Loaded -= HistoryControl_Loaded;
        }
    }
}
