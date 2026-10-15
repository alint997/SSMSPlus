namespace SSMSPlus.Features.ResultsGrid
{
    using EnvDTE80;
    using Microsoft.SqlServer.Management.UI.Grid;
    using Microsoft.VisualStudio.Shell;
    using System;
    using System.Collections.Generic;
    using System.Windows.Threading;

    /// <summary>
    /// Shows Count / Sum / Avg / Min / Max of the selected results-grid cells in the SSMS status bar,
    /// the way Excel does. SSMS creates new grids on every execution and has no event for that, so the
    /// grids of the active window are discovered with a light poll; selection changes are debounced.
    /// </summary>
    internal sealed class GridAggregatesStatusBar
    {
        private readonly DTE2 _dte;
        private readonly HashSet<GridControl> _attached = new HashSet<GridControl>();
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _debounceTimer;
        private GridControl _pendingGrid;
        private bool _textIsOurs;

        public GridAggregatesStatusBar(DTE2 dte)
        {
            _dte = dte;

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
            _pollTimer.Tick += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                AttachToActiveWindowGrids();
            };

            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _debounceTimer.Tick += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _debounceTimer.Stop();
                Update(_pendingGrid);
            };
        }

        public void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _pollTimer.Start();
        }

        private void AttachToActiveWindowGrids()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var window = _dte?.ActiveWindow;
                if (window == null || window.Type != EnvDTE.vsWindowType.vsWindowTypeDocument)
                    return;

                IntPtr hwnd = (IntPtr)window.HWnd;
                foreach (var grid in ResultsGridAccess.FindGridsUnder(hwnd))
                {
                    if (grid == null || grid.IsDisposed || !_attached.Add(grid))
                        continue;

                    grid.SelectionChanged += OnSelectionChanged;
                    grid.Disposed += OnGridDisposed;
                }
            }
            catch
            {
                // The active window can go away between the check and the enumeration.
            }
        }

        private void OnGridDisposed(object sender, EventArgs e)
        {
            if (sender is GridControl grid)
            {
                grid.SelectionChanged -= OnSelectionChanged;
                grid.Disposed -= OnGridDisposed;
                _attached.Remove(grid);
                if (ReferenceEquals(_pendingGrid, grid))
                    _pendingGrid = null;
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _pendingGrid = sender as GridControl;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void Update(GridControl grid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string text = null;
            try
            {
                if (grid != null && !grid.IsDisposed)
                    text = GridAggregates.Compute(grid)?.ToStatusText();
            }
            catch
            {
                text = null;
            }

            // EnvDTE status bar: IVsStatusbar exists in two referenced interop assemblies (ambiguous).
            var statusBar = _dte?.StatusBar;
            if (statusBar == null)
                return;

            try
            {
                if (text != null)
                {
                    statusBar.Text = text;
                    _textIsOurs = true;
                }
                else if (_textIsOurs)
                {
                    statusBar.Clear();
                    _textIsOurs = false;
                }
            }
            catch
            {
                // The status bar can be unavailable while SSMS is shutting down.
            }
        }
    }
}
