// Selection handling is adapted from SQLExtended (MIT License), Copyright (c) 2026 James Noonan
// https://github.com/jamtheradar/SQLExtended
namespace SSMSPlus.Features.ResultsGrid
{
    using Microsoft.SqlServer.Management.UI.Grid;
    using System;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Drawing;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    /// <summary>The selected part of a results grid, as display strings. A null cell value is SQL NULL.</summary>
    internal sealed class GridSelectionData
    {
        public List<string> ColumnNames { get; } = new List<string>();

        /// <summary>SQL type per column, e.g. "nvarchar(50)"; null when SSMS internals are not available.</summary>
        public List<string> SqlTypes { get; } = new List<string>();

        public List<string[]> Rows { get; } = new List<string[]>();
    }

    /// <summary>
    /// Reads SSMS results grids. A results pane hosts one GridControl per result set; its IGridStorage is
    /// SQLEditors' QEResultSet, whose column names, types and null flags are read through reflection with
    /// fallbacks. Storage column 0 is the row-number column; data columns start at 1.
    /// Everything here must run on the UI thread.
    /// </summary>
    internal static class ResultsGridAccess
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>The results grid that has the keyboard focus (the one right-clicked), or null.</summary>
        public static GridControl GetFocusedGrid()
        {
            IntPtr handle = GetFocus();
            if (handle == IntPtr.Zero)
                return null;

            Control control = Control.FromHandle(handle);
            while (control != null && !(control is GridControl))
                control = control.Parent;

            return control as GridControl;
        }

        /// <summary>All results grids under a window, in result-set order.</summary>
        public static List<GridControl> FindGridsUnder(IntPtr hwnd)
        {
            var grids = new List<GridControl>();
            if (hwnd == IntPtr.Zero)
                return grids;

            EnumChildWindows(hwnd, (child, _) =>
            {
                if (Control.FromHandle(child) is GridControl grid)
                    grids.Add(grid);
                return true;
            }, IntPtr.Zero);

            return grids;
        }

        public struct CellRange
        {
            public long RowStart;
            public long RowEnd;
            public int ColStart; // storage index
            public int ColEnd;   // storage index
        }

        /// <summary>
        /// Converts the grid selection into storage-indexed ranges. For whole-column selections the block rows are
        /// meaningless (all rows are selected); for whole-row selections the block columns are. Column indexes
        /// are UI indexes, except for SingleCell / SingleColumn selections where the grid already reports storage indexes.
        /// </summary>
        public static List<CellRange> GetSelectedRanges(GridControl grid)
        {
            var ranges = new List<CellRange>();
            IGridStorage storage = grid?.GridStorage;
            BlockOfCellsCollection blocks = grid?.SelectedCells;
            if (storage == null || blocks == null || blocks.Count == 0)
                return ranges;

            long numRows = storage.NumRows();
            int numCols = grid.ColumnsNumber;
            if (numRows <= 0 || numCols <= 0)
                return ranges;

            GridSelectionType type = grid.SelectionType;
            bool wholeColumns = type == GridSelectionType.ColumnBlocks || type == GridSelectionType.SingleColumn;
            bool wholeRows = type == GridSelectionType.RowBlocks || type == GridSelectionType.SingleRow;
            bool storageIndexed = type == GridSelectionType.SingleCell || type == GridSelectionType.SingleColumn;

            foreach (BlockOfCells block in blocks)
            {
                if (block == null || block.IsEmpty)
                    continue;

                var range = new CellRange
                {
                    RowStart = wholeColumns ? 0 : block.Y,
                    RowEnd = wholeColumns ? numRows - 1 : block.Bottom,
                    ColStart = wholeRows ? 0 : block.X,
                    ColEnd = wholeRows ? numCols - 1 : block.Right,
                };

                if (!storageIndexed && !wholeRows)
                {
                    int a = ToStorageColumn(grid, range.ColStart);
                    int b = ToStorageColumn(grid, range.ColEnd);
                    range.ColStart = Math.Min(a, b);
                    range.ColEnd = Math.Max(a, b);
                }

                range.RowStart = Math.Max(0, range.RowStart);
                range.RowEnd = Math.Min(numRows - 1, range.RowEnd);
                range.ColStart = Math.Max(0, range.ColStart);
                range.ColEnd = Math.Min(numCols - 1, range.ColEnd);
                if (range.RowEnd < range.RowStart || range.ColEnd < range.ColStart)
                    continue;

                ranges.Add(range);
            }

            return ranges;
        }

        /// <summary>Number of cells covered by the ranges (an upper bound when blocks overlap).</summary>
        public static long CountCells(List<CellRange> ranges)
        {
            long cells = 0;
            foreach (var r in ranges)
                cells += (r.RowEnd - r.RowStart + 1) * (r.ColEnd - r.ColStart + 1);
            return cells;
        }

        /// <summary>
        /// Reads the selected rows and columns as a table. Multiple blocks are combined into the union of their
        /// rows and columns. When the selection is a single cell, the whole grid is returned.
        /// </summary>
        public static GridSelectionData ReadSelection(GridControl grid, bool wholeGridIfSingleCell)
        {
            IGridStorage storage = grid.GridStorage;
            var ranges = GetSelectedRanges(grid);

            bool singleCell = ranges.Count == 0 || (ranges.Count == 1 && CountCells(ranges) == 1);
            if (singleCell && wholeGridIfSingleCell)
            {
                ranges = new List<CellRange>
                {
                    new CellRange { RowStart = 0, RowEnd = storage.NumRows() - 1, ColStart = 1, ColEnd = grid.ColumnsNumber - 1 }
                };
            }

            var rowSet = new SortedSet<long>();
            var colSet = new HashSet<int>();
            foreach (var r in ranges)
            {
                for (long row = r.RowStart; row <= r.RowEnd; row++)
                    rowSet.Add(row);
                for (int col = Math.Max(1, r.ColStart); col <= r.ColEnd; col++)
                    colSet.Add(col);
            }

            // Keep the columns in the order the user sees them.
            var columns = colSet.OrderBy(c => ToUiColumn(grid, c)).ToList();

            var meta = StorageMetadata.For(storage);
            var data = new GridSelectionData();
            foreach (int col in columns)
            {
                data.ColumnNames.Add(meta.ColumnName(grid, col));
                data.SqlTypes.Add(meta.ColumnType(col));
            }

            if (rowSet.Count > 0)
            {
                try { storage.EnsureRowsInBuf(rowSet.Min, rowSet.Max); } catch { }
            }

            foreach (long row in rowSet)
            {
                var values = new string[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                    values[i] = meta.Cell(storage, row, columns[i]);
                data.Rows.Add(values);
            }

            return data;
        }

        private static int ToStorageColumn(GridControl grid, int uiColumn)
        {
            try { return grid.GetStorageColumnIndexByUIIndex(uiColumn); }
            catch { return uiColumn; }
        }

        private static int ToUiColumn(GridControl grid, int storageColumn)
        {
            try { return grid.GetUIColumnIndexByStorageIndex(storageColumn); }
            catch { return storageColumn; }
        }

        /// <summary>Best-effort access to QEResultSet internals.</summary>
        internal sealed class StorageMetadata
        {
            private StringCollection _names;
            private MethodInfo _typeName;
            private MethodInfo _isNull;

            public static StorageMetadata For(IGridStorage storage)
            {
                var meta = new StorageMetadata();
                if (storage == null)
                    return meta;

                Type type = storage.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                try { meta._names = type.GetProperty("ColumnNames", flags)?.GetValue(storage) as StringCollection; } catch { }
                try { meta._typeName = type.GetMethod("GetFormattedDataTypeName", flags, null, new[] { typeof(int) }, null); } catch { }
                try { meta._isNull = type.GetMethod("IsCellDataNull", flags, null, new[] { typeof(long), typeof(int) }, null); } catch { }
                meta._storage = storage;
                return meta;
            }

            private IGridStorage _storage;

            public string ColumnName(GridControl grid, int storageColumn)
            {
                int dataColumn = storageColumn - 1;
                if (_names != null && dataColumn >= 0 && dataColumn < _names.Count && !string.IsNullOrEmpty(_names[dataColumn]))
                    return _names[dataColumn];

                try
                {
                    grid.GetHeaderInfo(storageColumn, out string header, out Bitmap _);
                    if (!string.IsNullOrEmpty(header))
                        return header;
                }
                catch { }

                return "Column" + storageColumn;
            }

            public string ColumnType(int storageColumn)
            {
                if (_typeName == null)
                    return null;
                try { return _typeName.Invoke(_storage, new object[] { storageColumn - 1 }) as string; }
                catch { _typeName = null; return null; }
            }

            public string Cell(IGridStorage storage, long row, int storageColumn)
            {
                if (_isNull != null)
                {
                    try
                    {
                        if ((bool)_isNull.Invoke(storage, new object[] { row, storageColumn }))
                            return null;
                    }
                    catch { _isNull = null; }
                }

                string value = storage.GetCellDataAsString(row, storageColumn);
                if (_isNull == null && value == "NULL")
                    return null;
                return value;
            }
        }
    }
}
