namespace SSMSPlus.Features.Blocking
{
    using Microsoft.VisualStudio.PlatformUI;
    using Microsoft.VisualStudio.Shell;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Data;
    using System.Windows.Media;
    using System.Windows.Threading;

    /// <summary>
    /// Content of the "Blocking Monitor" tool window: who blocks whom right now, as a tree (head blockers first),
    /// with idle sessions that keep a transaction open highlighted, auto refresh and a details panel.
    /// </summary>
    internal sealed class BlockingMonitorControl : UserControl
    {
        private static readonly Brush HeadBlockerRow = Frozen(Color.FromArgb(0x55, 0xE5, 0x48, 0x3B));
        private static readonly Brush IdleBlockerRow = Frozen(Color.FromArgb(0x70, 0xE5, 0x48, 0x3B));
        private static readonly Brush LongWaitRow = Frozen(Color.FromArgb(0x40, 0xF0, 0xA0, 0x30));
        private static readonly Brush IdleRow = Frozen(Color.FromArgb(0x35, 0x8A, 0x7C, 0xD8));
        private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x3B));

        private readonly TextBlock _server = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly Border _permissionWarning = new Border();
        private readonly ComboBox _interval = new ComboBox();
        private readonly CheckBox _includeIdle = new CheckBox();
        private readonly DataGrid _grid = new DataGrid();
        private readonly StackPanel _details = new StackPanel();
        private readonly TextBox _sql = new TextBox();
        private readonly Button _showPlan;
        private readonly Button _scriptKill;
        private readonly Button _copySql;
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        private string _connectionString;
        private BlockingSnapshot _snapshot;
        private bool _refreshing;
        private CancellationTokenSource _cts;

        public BlockingMonitorControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            try
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/SSMSPlusCore;component/Ui/Design/VsTheme.xaml", UriKind.Absolute),
                });
            }
            catch
            {
            }

            var root = new DockPanel();
            Content = root;

            // ---- toolbar
            var toolbar = new WrapPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            toolbar.Children.Add(MakeButton("Refresh", "Read the current blocking situation now (F5)", (s, e) => RefreshNow()));
            toolbar.Children.Add(MakeButton("Use active query window", "Monitor the server of the active query window", (s, e) => ChangeServerRequested?.Invoke()));

            toolbar.Children.Add(new TextBlock { Text = "Auto refresh:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
            foreach (var item in new[] { "Off", "2 s", "5 s", "10 s", "30 s" })
                _interval.Items.Add(item);
            _interval.SelectedIndex = 2;
            _interval.MinWidth = 70;
            _interval.Margin = new Thickness(0, 2, 6, 2);
            _interval.SelectionChanged += (s, e) => UpdateTimer();
            toolbar.Children.Add(_interval);

            _includeIdle.Content = "Show idle sessions with an open transaction";
            _includeIdle.IsChecked = true;
            _includeIdle.VerticalAlignment = VerticalAlignment.Center;
            _includeIdle.Margin = new Thickness(8, 0, 8, 0);
            _includeIdle.ToolTip = "Also list sessions that are not running anything but keep a transaction open (a forgotten BEGIN TRAN), even before they block someone.";
            _includeIdle.Checked += (s, e) => RefreshNow();
            _includeIdle.Unchecked += (s, e) => RefreshNow();
            toolbar.Children.Add(_includeIdle);

            // ---- server + status
            var info = new StackPanel { Margin = new Thickness(8, 2, 8, 4) };
            _server.FontWeight = FontWeights.SemiBold;
            info.Children.Add(_server);
            _status.TextWrapping = TextWrapping.Wrap;
            info.Children.Add(_status);
            DockPanel.SetDock(info, Dock.Top);
            root.Children.Add(info);

            _permissionWarning.Padding = new Thickness(8, 4, 8, 4);
            _permissionWarning.Margin = new Thickness(8, 0, 8, 4);
            _permissionWarning.BorderThickness = new Thickness(1);
            _permissionWarning.BorderBrush = WarningBrush;
            _permissionWarning.Visibility = Visibility.Collapsed;
            _permissionWarning.Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Your login does not have VIEW SERVER STATE: SQL Server shows only your own session, so blocking caused by other users is NOT visible here. "
                    + "An empty list does not mean there is no blocking. Ask an administrator for: GRANT VIEW SERVER STATE TO [your login];",
            };
            DockPanel.SetDock(_permissionWarning, Dock.Top);
            root.Children.Add(_permissionWarning);

            // ---- main: grid | splitter | details
            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
            root.Children.Add(main);

            BuildGrid();
            Grid.SetColumn(_grid, 0);
            main.Children.Add(_grid);

            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
            splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetColumn(splitter, 1);
            main.Children.Add(splitter);

            var detailsPanel = new DockPanel { Margin = new Thickness(8) };
            var buttons = new WrapPanel();
            DockPanel.SetDock(buttons, Dock.Top);
            _copySql = MakeButton("Copy SQL", "Copy the statement of the selected session", (s, e) => CopySql());
            _showPlan = MakeButton("Show plan", "Show the cached plan of the running statement in Execution Plan Tree", (s, e) => ShowPlan());
            _scriptKill = MakeButton("Script KILL", "Open a new query window with KILL for the selected session (nothing is executed)", (s, e) => ScriptKill());
            buttons.Children.Add(_copySql);
            buttons.Children.Add(_showPlan);
            buttons.Children.Add(_scriptKill);
            detailsPanel.Children.Add(buttons);

            var detailsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };
            detailsScroll.Content = _details;
            DockPanel.SetDock(detailsScroll, Dock.Top);
            detailsPanel.Children.Add(detailsScroll);

            detailsPanel.Children.Add(new TextBlock { Text = "SQL", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
            DockPanel.SetDock(detailsPanel.Children[detailsPanel.Children.Count - 1], Dock.Top);
            _sql.IsReadOnly = true;
            _sql.FontFamily = new FontFamily("Consolas");
            _sql.TextWrapping = TextWrapping.Wrap;
            _sql.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _sql.AcceptsReturn = true;
            detailsPanel.Children.Add(_sql);

            Grid.SetColumn(detailsPanel, 2);
            main.Children.Add(detailsPanel);

            _timer.Tick += (s, e) => RefreshNow();
            IsVisibleChanged += (s, e) => UpdateTimer();
            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.F5)
                {
                    RefreshNow();
                    e.Handled = true;
                }
            };

            ShowDetails(null);
            _server.Text = "No server";
            _status.Text = "Open a query window connected to the server to monitor, then use \"Use active query window\".";
        }

        /// <summary>Raised by "Use active query window".</summary>
        public Action ChangeServerRequested { get; set; }

        /// <summary>Shows a plan XML in the Execution Plan Tree window.</summary>
        public Func<string, string, Task> ShowPlanRequested { get; set; }

        /// <summary>Opens a new query window with the given script.</summary>
        public Action<string> ScriptRequested { get; set; }

        public void SetServer(string connectionString, string displayName)
        {
            _connectionString = connectionString;
            _server.Text = displayName;
            _snapshot = null;
            _grid.ItemsSource = null;
            RefreshNow();
            UpdateTimer();
        }

        public void ShowError(string message)
        {
            _status.Text = message;
            _status.Foreground = WarningBrush;
        }

        private void UpdateTimer()
        {
            int seconds;
            switch (_interval.SelectedIndex)
            {
                case 1: seconds = 2; break;
                case 2: seconds = 5; break;
                case 3: seconds = 10; break;
                case 4: seconds = 30; break;
                default: seconds = 0; break;
            }

            _timer.Stop();
            if (seconds > 0 && IsVisible && _connectionString != null)
            {
                _timer.Interval = TimeSpan.FromSeconds(seconds);
                _timer.Start();
            }
        }

        private void RefreshNow()
        {
            if (_refreshing || _connectionString == null)
                return;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                _refreshing = true;
                _cts?.Dispose();
                _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                bool includeIdle = _includeIdle.IsChecked == true;
                string connectionString = _connectionString;
                try
                {
                    var snapshot = await Task.Run(() => BlockingReader.ReadAsync(connectionString, includeIdle, _cts.Token));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (connectionString == _connectionString)
                        ShowSnapshot(snapshot);
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowError("Refresh failed: " + ex.Message);
                }
                finally
                {
                    _refreshing = false;
                }
            });
        }

        private void ShowSnapshot(BlockingSnapshot snapshot)
        {
            int? selectedId = (_grid.SelectedItem as BlockingSession)?.SessionId;
            _snapshot = snapshot;
            _grid.ItemsSource = snapshot.Flat;

            if (!string.IsNullOrEmpty(snapshot.ServerName))
                _server.Text = snapshot.ServerName + (string.IsNullOrEmpty(snapshot.ProductVersion) ? string.Empty : "  (" + snapshot.ProductVersion + (snapshot.IsAzureSqlDatabase ? ", Azure SQL Database" : string.Empty) + ")");

            _permissionWarning.Visibility = snapshot.CanSeeAllSessions ? Visibility.Collapsed : Visibility.Visible;
            if (snapshot.IsAzureSqlDatabase && _permissionWarning.Child is TextBlock warning)
                warning.Text = "Azure SQL Database: only sessions of this database are visible, and only your own without VIEW DATABASE STATE.";

            _status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var parts = new List<string> { "Refreshed " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) };
            int blocked = snapshot.BlockedCount;
            if (blocked == 0)
            {
                parts.Add("no blocking");
            }
            else
            {
                int heads = snapshot.Roots.Count(r => r.IsHeadBlocker);
                parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} blocked session(s), {1} head blocker(s)", blocked, heads));
                parts.Add("longest wait " + BlockingFormat.Duration(TimeSpan.FromMilliseconds(snapshot.LongestWaitMs)));
            }

            int idle = snapshot.Flat.Count(s => s.IsIdleWithOpenTransaction);
            if (idle > 0)
                parts.Add(idle + " idle session(s) with an open transaction");
            _status.Text = string.Join("   |   ", parts);

            var reselect = selectedId.HasValue ? snapshot.Flat.FirstOrDefault(s => s.SessionId == selectedId.Value) : null;
            if (reselect == null)
                reselect = snapshot.Flat.FirstOrDefault();
            _grid.SelectedItem = reselect;
            ShowDetails(reselect);
        }

        private void BuildGrid()
        {
            _grid.AutoGenerateColumns = false;
            _grid.IsReadOnly = true;
            _grid.CanUserSortColumns = false; // keep the tree order
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;

            AddColumn("Session", nameof(BlockingSession.SessionLabel), 150);
            AddColumn("Role", nameof(BlockingSession.Role), 240);
            AddColumn("Blocking", nameof(BlockingSession.BlockingCountText), 65);
            AddColumn("Wait", nameof(BlockingSession.WaitText), 75);
            AddColumn("Wait type", nameof(BlockingSession.WaitType), 110);
            AddColumn("Open tran", nameof(BlockingSession.TransactionText), 90);
            AddColumn("Status", nameof(BlockingSession.StatusText), 120);
            AddColumn("Login", nameof(BlockingSession.LoginName), 130);
            AddColumn("Host", nameof(BlockingSession.HostName), 110);
            AddColumn("Program", nameof(BlockingSession.ProgramName), 160);
            AddColumn("Database", nameof(BlockingSession.DatabaseName), 110);
            AddColumn("Wait resource", nameof(BlockingSession.WaitResource), 160);
            AddColumn("SQL", nameof(BlockingSession.SqlPreview), 400);

            _grid.LoadingRow += (s, e) =>
            {
                var session = e.Row.Item as BlockingSession;
                if (session == null)
                    return;
                if (session.IsHeadBlocker)
                    e.Row.Background = session.IsIdleWithOpenTransaction ? IdleBlockerRow : HeadBlockerRow;
                else if (session.IsBlocked && session.WaitTimeMs >= 30000)
                    e.Row.Background = LongWaitRow;
                else if (session.IsIdleWithOpenTransaction)
                    e.Row.Background = IdleRow;
                else
                    e.Row.ClearValue(BackgroundProperty);
                e.Row.FontWeight = session.IsHeadBlocker ? FontWeights.SemiBold : FontWeights.Normal;
            };

            _grid.SelectionChanged += (s, e) => ShowDetails(_grid.SelectedItem as BlockingSession);
        }

        private void AddColumn(string header, string path, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
        }

        private void ShowDetails(BlockingSession session)
        {
            _details.Children.Clear();
            _sql.Text = session?.Sql ?? string.Empty;
            _copySql.IsEnabled = session != null && !string.IsNullOrEmpty(session.Sql);
            _showPlan.IsEnabled = session?.PlanHandle != null;
            _scriptKill.IsEnabled = session != null && session.IsUserProcess && !session.IsMe;
            if (session == null)
                return;

            void Add(string name, string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                line.Inlines.Add(new System.Windows.Documents.Run(name + ": ") { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new System.Windows.Documents.Run(value));
                _details.Children.Add(line);
            }

            _details.Children.Add(new TextBlock
            {
                Text = "Session " + session.SessionId + (session.IsMe ? " (you)" : string.Empty),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 6, 0, 4),
            });

            if (session.IsIdleWithOpenTransaction && session.Blocked.Count > 0)
            {
                _details.Children.Add(new TextBlock
                {
                    Text = "This session is idle but keeps a transaction open: the locks it holds block the sessions below it. "
                        + "Usually a BEGIN TRAN without COMMIT / ROLLBACK (in SSMS or in an application). "
                        + "The SQL shown is the last batch it ran.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = WarningBrush,
                    Margin = new Thickness(0, 0, 0, 6),
                });
            }

            if (session.SqlNeedsParameters)
            {
                _details.Children.Add(new TextBlock
                {
                    Text = "The SQL below is parameterized (SQL Server stores it without the values). Copy SQL adds DECLAREs with the values the request runs with, when SQL Server can provide them.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 0, 0, 6),
                });
            }

            Add("Role", session.Role);
            Add("Blocked by", session.IsBlocked ? session.BlockingSessionId.ToString(CultureInfo.InvariantCulture) : null);
            Add("Blocking", session.Blocked.Count > 0
                ? string.Join(", ", session.Blocked.Select(b => b.SessionId)) + (session.TotalBlocked > session.Blocked.Count ? " (" + session.TotalBlocked + " in total)" : string.Empty)
                : null);
            Add("Login", session.LoginName);
            Add("Host", session.HostName);
            Add("Program", session.ProgramName);
            Add("Database", session.DatabaseName);
            Add("Status", session.StatusText);
            Add("Wait", string.IsNullOrEmpty(session.WaitType) ? null : session.WaitType + " · " + session.WaitText);
            Add("Wait resource", session.WaitResource);
            Add("Running since", session.RequestStart.HasValue ? BlockingFormat.Duration(session.ServerTime - session.RequestStart.Value) : null);
            Add("Idle for", session.IdleFor.HasValue ? BlockingFormat.Duration(session.IdleFor.Value) : null);
            Add("Open transactions", session.OpenTransactionCount > 0 ? session.OpenTransactionCount.ToString(CultureInfo.InvariantCulture) : null);
            Add("Transaction open for", session.TransactionAge.HasValue ? BlockingFormat.Duration(session.TransactionAge.Value) : null);
            Add("CPU", session.HasRequest ? session.CpuTimeMs.ToString("N0", CultureInfo.CurrentCulture) + " ms" : null);
            Add("Logical reads", session.HasRequest ? session.LogicalReads.ToString("N0", CultureInfo.CurrentCulture) : null);
        }

        private void CopySql()
        {
            if (!(_grid.SelectedItem is BlockingSession session) || string.IsNullOrEmpty(session.Sql))
                return;

            string connectionString = _connectionString;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                string text = session.Sql;
                try
                {
                    // Parameterized text ("(@1 int)SELECT ... = @1") is turned into DECLAREs with the values.
                    if (session.SqlNeedsParameters && connectionString != null)
                        text = await Task.Run(() => BlockingReader.BuildRunnableSqlAsync(connectionString, session, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowError("Could not read the parameter values: " + ex.Message);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    Clipboard.SetDataObject(text ?? session.Sql, true);
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
            });
        }

        private void ShowPlan()
        {
            if (!(_grid.SelectedItem is BlockingSession session) || _connectionString == null)
                return;

            string connectionString = _connectionString;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var xml = await Task.Run(() => BlockingReader.ReadPlanAsync(connectionString, session, CancellationToken.None));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (string.IsNullOrEmpty(xml))
                    {
                        ShowError("The plan of session " + session.SessionId + " is not available (no running statement or the plan is not cached).");
                        return;
                    }

                    if (ShowPlanRequested != null)
                        await ShowPlanRequested(xml, "session " + session.SessionId + " (cached plan)");
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowError("Could not read the plan: " + ex.Message);
                }
            });
        }

        private void ScriptKill()
        {
            if (!(_grid.SelectedItem is BlockingSession session))
                return;

            var script =
                "-- Session " + session.SessionId + ": " + session.LoginName + " @ " + session.HostName + " (" + session.ProgramName + ")\r\n" +
                (session.OpenTransactionCount > 0 ? "-- KILL rolls back its open transaction" + (session.TransactionAge.HasValue ? " (open for " + BlockingFormat.Duration(session.TransactionAge.Value) + ")" : string.Empty) + "; the rollback can take a while.\r\n" : string.Empty) +
                "-- Review before running.\r\n" +
                "KILL " + session.SessionId.ToString(CultureInfo.InvariantCulture) + ";\r\n";
            ScriptRequested?.Invoke(script);
        }

        private static Button MakeButton(string text, string tooltip, RoutedEventHandler click)
        {
            var b = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += click;
            return b;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
