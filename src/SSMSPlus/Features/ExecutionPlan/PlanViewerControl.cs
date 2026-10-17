namespace SSMSPlus.Features.ExecutionPlan
{
    using ICSharpCode.AvalonEdit;
    using ICSharpCode.AvalonEdit.Highlighting;
    using Microsoft.VisualStudio.PlatformUI;
    using Microsoft.VisualStudio.Shell;
    using SSMSPlus.Features.Advisor;
    using SSMSPlusCore.Ui.Extensions;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Controls.Primitives;
    using System.Windows.Data;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Shapes;

    /// <summary>
    /// Content of the "Execution Plan Tree" tool window: toolbar, statement picker, the DB2-style tree with an
    /// overview map, a sortable operator list, the raw XML, and a details panel for the selected node.
    /// Built in code so it does not need XAML compilation in the package project.
    /// </summary>
    internal sealed class PlanViewerControl : UserControl
    {
        private const double MinZoom = 0.2;
        private const double MaxZoom = 2.5;

        private readonly PlanTreeCanvas _canvas = new PlanTreeCanvas();
        private readonly ScaleTransform _zoom = new ScaleTransform(1, 1);
        private readonly ScrollViewer _treeScroll = new ScrollViewer();
        private readonly ComboBox _statements = new ComboBox();
        private readonly TextBlock _info = new TextBlock();
        private readonly TextBlock _zoomText = new TextBlock();
        private readonly TabControl _tabs = new TabControl();
        private readonly DataGrid _operators = new DataGrid();
        private readonly TextEditor _xml = new TextEditor();
        private readonly StackPanel _details = new StackPanel();
        private readonly Border _overview = new Border();
        private readonly Rectangle _overviewMap = new Rectangle();
        private readonly Canvas _overviewLayer = new Canvas();
        private readonly Rectangle _overviewViewport = new Rectangle();
        private readonly Button _copyIndex;
        private readonly TextBlock _busy = new TextBlock();
        private readonly Button _cancel;

        private readonly AdvisorView _advisor = new AdvisorView();
        private readonly TabItem _advisorTab = new TabItem { Header = "Advisor" };
        private readonly ScriptTextView _annotated = new ScriptTextView("The statement text with what the plan does on each table (rows, operator, cost, estimated -> actual rows, statistics, findings).", "PlanAnnotatedQuery");
        private readonly ScriptTextView _indexAdvice = new ScriptTextView("Index suggestions of all statements merged per table and compared with the existing indexes.", "PlanIndexAdvice");
        private readonly CompareView _compare = new CompareView();
        private readonly TabItem _indexTab = new TabItem { Header = "Index advice" };
        private readonly TabItem _compareTab = new TabItem { Header = "Compare" };
        private readonly Button _baselineButton;

        // Stage 4: the current plan with its advisor result (kept by the Compare tab), and the texts for the report.
        private PlanSnapshot _current;
        private string _annotatedText;
        private string _indexText;

        private readonly Button _refresh;

        private PlanDocument _document;
        private List<AdvisorFinding> _findings = new List<AdvisorFinding>();
        private AdvisorServerInfo _serverInfo;
        private CancellationTokenSource _serverRead;

        public PlanViewerControl()
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
                // Theme dictionary not available: the default WPF look is still usable.
            }

            var root = new DockPanel { LastChildFill = true };
            Content = root;

            // ---- toolbar
            var toolbar = new WrapPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            _refresh = ToolButton("Refresh", RefreshHint, (s, e) => RefreshRequested?.Invoke());
            _refresh.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(_refresh, true);
            toolbar.Children.Add(_refresh);
            toolbar.Children.Add(ToolButton("Estimated plan", "Get the estimated plan of the selected text (or the whole script) in the active query window - or, when this window is active, in the query window of the last plan. Nothing is executed.", (s, e) => RequestPlan?.Invoke(false)));
            toolbar.Children.Add(ToolButton("Actual plan", "Execute the selected text (or the whole script) in the active query window - or, when this window is active, in the query window of the last plan - and show the actual plan. Data changes are rolled back.", (s, e) => RequestPlan?.Invoke(true)));
            toolbar.Children.Add(ToolButton("Open .sqlplan...", "Open a plan saved from SSMS (.sqlplan or .xml).", (s, e) => OpenFile()));
            toolbar.Children.Add(ToolButton("Paste XML", "Show a plan copied to the clipboard (in SSMS: right click a plan > Show Execution Plan XML, then copy).", (s, e) => PasteXml()));
            toolbar.Children.Add(ToolSeparator());

            toolbar.Children.Add(new TextBlock { Text = "Statement:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
            _statements.MinWidth = 280;
            _statements.MaxWidth = 700;
            _statements.Margin = new Thickness(0, 2, 6, 2);
            _statements.SelectionChanged += (s, e) => ShowStatement(_statements.SelectedItem as PlanStatement);
            toolbar.Children.Add(_statements);
            toolbar.Children.Add(ToolSeparator());

            toolbar.Children.Add(ToolButton("−", "Zoom out (Ctrl + mouse wheel)", (s, e) => SetZoom(_zoom.ScaleX / 1.2)));
            _zoomText.VerticalAlignment = VerticalAlignment.Center;
            _zoomText.MinWidth = 42;
            _zoomText.TextAlignment = TextAlignment.Center;
            toolbar.Children.Add(_zoomText);
            toolbar.Children.Add(ToolButton("+", "Zoom in (Ctrl + mouse wheel)", (s, e) => SetZoom(_zoom.ScaleX * 1.2)));
            toolbar.Children.Add(ToolButton("100%", "Actual size", (s, e) => SetZoom(1)));
            toolbar.Children.Add(ToolButton("Fit", "Fit the whole tree in the window", (s, e) => ZoomToFit()));

            var overviewToggle = new CheckBox { Content = "Overview", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            overviewToggle.Checked += (s, e) => _overview.Visibility = Visibility.Visible;
            overviewToggle.Unchecked += (s, e) => _overview.Visibility = Visibility.Collapsed;
            toolbar.Children.Add(overviewToggle);

            _copyIndex = ToolButton("Copy missing index", "Copy the CREATE INDEX statement(s) suggested by the optimizer", (s, e) => CopyMissingIndexes());
            _copyIndex.Visibility = Visibility.Collapsed;
            toolbar.Children.Add(_copyIndex);

            _baselineButton = ToolButton("Set as baseline", BaselineHint, (s, e) => SetBaseline());
            toolbar.Children.Add(_baselineButton);
            toolbar.Children.Add(ToolButton("Export report...", "Save a report (HTML) with the statements, the advisor findings, the annotated query, the index advice and the comparison.", (s, e) => ExportReport()));

            _busy.VerticalAlignment = VerticalAlignment.Center;
            _busy.Margin = new Thickness(10, 0, 4, 0);
            _busy.FontStyle = FontStyles.Italic;
            toolbar.Children.Add(_busy);
            _cancel = ToolButton("Cancel", "Cancel the running query", (s, e) => CancelRequested?.Invoke());
            _cancel.Visibility = Visibility.Collapsed;
            toolbar.Children.Add(_cancel);

            // ---- info bar
            var infoBorder = new Border { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 1, 0, 1) };
            infoBorder.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            _info.TextWrapping = TextWrapping.Wrap;
            infoBorder.Child = _info;
            DockPanel.SetDock(infoBorder, Dock.Top);
            root.Children.Add(infoBorder);

            // ---- legend
            var legend = new WrapPanel { Margin = new Thickness(8, 2, 8, 2) };
            foreach (var category in new[] { PlanNodeCategory.Scan, PlanNodeCategory.Seek, PlanNodeCategory.Lookup, PlanNodeCategory.Join, PlanNodeCategory.Sort, PlanNodeCategory.Aggregate, PlanNodeCategory.Spool, PlanNodeCategory.Parallelism, PlanNodeCategory.Other, PlanNodeCategory.Table, PlanNodeCategory.Index })
            {
                legend.Children.Add(new Border
                {
                    Width = 12,
                    Height = 12,
                    Background = PlanTreeCanvas.FillFor(category),
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(6, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                legend.Children.Add(new TextBlock { Text = PlanTreeCanvas.CategoryLegend(category), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            }
            legend.Children.Add(new TextBlock
            {
                Text = "    Border: red ≥ 25% of the cost, orange ≥ 10%   ⚠ warning   rows: estimated → actual",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            DockPanel.SetDock(legend, Dock.Top);
            root.Children.Add(legend);

            // ---- main area: tabs | splitter | details
            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            root.Children.Add(main);

            Grid.SetColumn(_tabs, 0);
            main.Children.Add(_tabs);

            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetColumn(splitter, 1);
            main.Children.Add(splitter);

            var detailsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8) };
            detailsScroll.Content = _details;
            Grid.SetColumn(detailsScroll, 2);
            main.Children.Add(detailsScroll);

            _tabs.Items.Add(new TabItem { Header = "Tree", Content = BuildTreeArea() });
            _advisorTab.Content = _advisor;
            _tabs.Items.Add(_advisorTab);
            _tabs.Items.Add(new TabItem { Header = "Annotated query", Content = _annotated });
            _indexTab.Content = _indexAdvice;
            _tabs.Items.Add(_indexTab);
            _compareTab.Content = _compare;
            _compare.HeaderChanged += header => _compareTab.Header = header;
            _compare.PinnedChanged += UpdateBaselineButton;
            _tabs.Items.Add(_compareTab);
            _tabs.Items.Add(new TabItem { Header = "Operators", Content = BuildOperatorsGrid() });
            _tabs.Items.Add(new TabItem { Header = "XML", Content = BuildXmlEditor() });

            _canvas.SelectedNodeChanged += (s, node) => ShowDetails(node);
            _advisor.FindingSelected += f => SelectFinding(f, false);
            _advisor.ShowInTreeRequested += f => SelectFinding(f, true);
            _advisor.ReadServerRequested = ReadServerInfoForCurrent;
            _annotated.ReadServerRequested = ReadServerInfoForCurrent;
            _indexAdvice.ReadServerRequested = ReadServerInfoForCurrent;

            // F5 in the plan window: Refresh (the SSMS F5 / Execute only applies to query windows).
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None && _refresh.IsEnabled && RefreshRequested != null)
                {
                    e.Handled = true;
                    RefreshRequested();
                }
            };

            UpdateZoomText();
            ShowEmpty();
        }

        private const string RefreshHint = "Get the plan again (F5): same query window, same kind of plan (estimated / actual), with its current text or selection. The new plan is compared with the previous one (or with the baseline) in the Compare tab.";

        /// <summary>Raised by Refresh / F5: get the last plan again from the same query window.</summary>
        public Action RefreshRequested { get; set; }

        /// <summary>Enables Refresh and shows in its tooltip what will be refreshed (e.g. "estimated plan of SQLQuery1.sql").</summary>
        public void SetRefreshTarget(string target)
        {
            _refresh.IsEnabled = !string.IsNullOrEmpty(target);
            _refresh.ToolTip = string.IsNullOrEmpty(target) ? RefreshHint : "Refresh the " + target + " (F5). Uses the current text or selection of that query window; the new plan is compared with the previous one (or the baseline) in the Compare tab.";
        }

        /// <summary>Raised by the Estimated / Actual buttons; the argument is true for the actual plan.</summary>
        public Action<bool> RequestPlan { get; set; }

        public Action CancelRequested { get; set; }

        /// <summary>Reads catalog information (statistics, indexes, row counts) for the tables of a plan; set by PlanCommands.</summary>
        public Func<PlanDocument, CancellationToken, Task<AdvisorServerInfo>> ReadServerInfo { get; set; }

        /// <summary>Opens a script in a new query window; set by PlanCommands.</summary>
        public Action<string> OpenScript
        {
            get => _advisor.OpenScriptRequested;
            set
            {
                _advisor.OpenScriptRequested = value;
                _annotated.OpenScriptRequested = value;
                _indexAdvice.OpenScriptRequested = value;
            }
        }

        public void SetBusy(string message, bool cancellable)
        {
            _busy.Text = message ?? string.Empty;
            _cancel.Visibility = cancellable && !string.IsNullOrEmpty(message) ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ShowError(string message)
        {
            SetBusy(null, false);
            _info.Text = message;
            _info.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _info.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x3B));
        }

        /// <param name="readServerInfo">Also read statistics and indexes from the server for the advisor (plans just obtained from the server).</param>
        public void ShowDocument(PlanDocument document, bool readServerInfo = false)
        {
            SetBusy(null, false);
            _info.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _serverRead?.Cancel();
            _serverInfo = null;
            _document = document;
            _statements.ItemsSource = document?.Statements;
            _xml.Text = document?.Xml ?? string.Empty;
            RunAdvisor();

            if (document == null || document.Statements.Count == 0)
            {
                ShowEmpty();
                if (document != null)
                    _info.Text = "The plan does not contain any query plan (for example only SET or DECLARE statements).";
                return;
            }

            // Start with the most expensive statement.
            var first = document.Statements.OrderByDescending(s => s.SubtreeCost).First();
            _statements.SelectedItem = null;
            _statements.SelectedItem = first; // raises SelectionChanged -> ShowStatement

            _tabs.SelectedIndex = 0;

            if (readServerInfo)
                ReadServerInfoForCurrent();
        }

        private void ShowEmpty()
        {
            _canvas.ShowPlan(null);
            _findings = new List<AdvisorFinding>();
            _advisor.SetFindings(null, false);
            _advisor.SetStatus(null, false);
            _advisorTab.Header = "Advisor";
            _indexTab.Header = "Index advice";
            _annotated.SetText(null, false);
            _indexAdvice.SetText(null, false);
            _current = null;
            _annotatedText = null;
            _indexText = null;
            _operators.ItemsSource = null;
            _details.Children.Clear();
            _copyIndex.Visibility = Visibility.Collapsed;
            _info.Text = "Open a query window and use \"Estimated plan\" or \"Actual plan\" (also in the editor right-click menu), open a .sqlplan file, or paste a plan XML.";
        }

        private void ShowStatement(PlanStatement statement)
        {
            if (statement == null)
                return;

            _canvas.ShowPlan(statement);
            _operators.ItemsSource = statement.Operators().OrderByDescending(o => o.CostPercent).ThenBy(o => o.NodeId).Select(o => new OperatorRow(o)).ToList();
            UpdateInfo(statement);

            _canvas.Select(statement.Root, false);
            Defer(() =>
            {
                _treeScroll.ScrollToHorizontalOffset(Math.Max(0, (_canvas.Width * _zoom.ScaleX - _treeScroll.ViewportWidth) / 2));
                _treeScroll.ScrollToTop();
                UpdateOverviewViewport();
            });
        }

        private void UpdateInfo(PlanStatement statement)
        {
            if (statement == null)
                return;

            var parts = new List<string>
            {
                (_document?.HasActual == true ? "Actual plan" : "Estimated plan") + (string.IsNullOrEmpty(_document?.Source) ? string.Empty : " – " + _document.Source),
                "statement cost " + PlanTreeCanvas.FormatNumber(statement.SubtreeCost),
                "estimated rows " + PlanTreeCanvas.FormatCount(statement.EstimatedRows),
            };
            if (statement.HasActual)
                parts.Add("actual rows " + PlanTreeCanvas.FormatCount(statement.Root.ActualRows));
            if (!string.IsNullOrEmpty(statement.DegreeOfParallelism))
                parts.Add("DOP " + statement.DegreeOfParallelism);
            if (!string.IsNullOrEmpty(statement.MemoryGrantKb))
                parts.Add("memory grant " + statement.MemoryGrantKb + " KB");

            int warnings = statement.Root.DescendantsAndSelf().Count(n => n.HasWarnings);
            if (warnings > 0)
                parts.Add(warnings + " node(s) with warnings ⚠");
            if (statement.MissingIndexes.Count > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, "missing index (impact {0:0.#}%)", statement.MissingIndexes.Max(m => m.Impact)));
            if (_findings.Count > 0)
                parts.Add("advisor: " + AdvisorView.Summary(_findings));

            _info.Text = string.Join("   |   ", parts);
            _copyIndex.Visibility = statement.MissingIndexes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- advisor

        private void RunAdvisor()
        {
            AdvisorResult result = null;
            try
            {
                result = QueryAdvisor.AnalyzeAll(_document, _serverInfo);
                _findings = result.Findings;
                if (_serverInfo == null)
                {
                    _advisor.SetStatus(PlanTable.From(_document).Any(t => !t.IsTemporary)
                        ? "Statistics come from the plan only. \"Read from server\" adds row counts, all statistics and the index definitions."
                        : null, false);
                }
            }
            catch (Exception ex)
            {
                _findings = new List<AdvisorFinding>();
                _advisor.SetStatus("The advisor failed: " + ex.Message, true);
            }

            bool canRead = ReadServerInfo != null && PlanTable.From(_document).Any(t => !t.IsTemporary);
            _advisor.SetFindings(_document == null ? null : _findings, canRead);
            int high = _findings.Count(f => f.Severity == AdvisorSeverity.High);
            _advisorTab.Header = _findings.Count == 0 ? "Advisor" : string.Format(CultureInfo.CurrentCulture, high > 0 ? "Advisor ({0}, {1} high)" : "Advisor ({0})", _findings.Count, high);

            // Annotated query and consolidated index advice (stage 3).
            try
            {
                _annotatedText = QueryAnnotator.Annotate(_document, _serverInfo, _findings);
                _annotated.SetText(_annotatedText, canRead && _serverInfo == null);
                var advice = IndexAdvisor.Recommend(result?.IndexCandidates ?? new List<IndexCandidate>(), _serverInfo);
                _indexText = _document == null ? null : IndexAdvisor.Report(advice, _serverInfo);
                _indexAdvice.SetText(_indexText, canRead && _serverInfo == null);
                int suggested = advice.Sum(a => a.Recommendations.Count(r => r.Action != IndexAction.AlreadyCovered));
                _indexTab.Header = suggested == 0 ? "Index advice" : string.Format(CultureInfo.CurrentCulture, "Index advice ({0})", suggested);
            }
            catch (Exception ex)
            {
                _annotated.SetText("-- The annotation failed: " + ex.Message, false);
                _indexAdvice.SetText("-- The index advice failed: " + ex.Message, false);
            }

            // Stage 4: keep the current plan for the comparison (the snapshot keeps its time across server reads).
            if (_document != null && result != null)
            {
                if (_current == null || !ReferenceEquals(_current.Document, _document))
                    _current = new PlanSnapshot { Document = _document, Result = result };
                else
                    _current.Result = result;
                _compare.AddOrUpdate(_current);
            }
        }

        // ---------------------------------------------------------------- compare with baseline, report

        private const string BaselineHint = "Pin this plan as the baseline: the next plans (Refresh / F5) are compared with it in the Compare tab. Without a baseline each plan is compared with the previous one.";

        private void SetBaseline()
        {
            if (_current == null)
            {
                ShowError("Get or open a plan first, then set it as the baseline.");
                return;
            }

            _compare.Pin(_current);
        }

        private void UpdateBaselineButton()
        {
            var pinned = _compare.Pinned;
            _baselineButton.Content = pinned == null ? "Set as baseline" : "Baseline #" + pinned.Number;
            _baselineButton.ToolTip = pinned == null
                ? BaselineHint
                : "Baseline: " + pinned.Label + ". Click to pin the current plan instead; \"Clear baseline\" in the Compare tab unpins it.";
        }

        private void ExportReport()
        {
            if (_current == null)
            {
                ShowError("Get or open a plan first, then export the report.");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export query report",
                Filter = "HTML report (*.html)|*.html",
                FileName = "SSMSPlus-query-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".html",
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var html = AdvisorReport.BuildHtml(_current.Document, _current.Result, _serverInfo, _annotatedText, _indexText, _compare.Comparison);
                File.WriteAllText(dialog.FileName, html, new System.Text.UTF8Encoding(false));
                SetBusy("Report saved: " + dialog.FileName, false);
            }
            catch (Exception ex)
            {
                ShowError("Could not save the report: " + ex.Message);
            }
        }

        private void ReadServerInfoForCurrent()
        {
            var document = _document;
            var reader = ReadServerInfo;
            if (document == null || reader == null)
                return;

            _serverRead?.Cancel();
            var cts = new CancellationTokenSource();
            _serverRead = cts;
            _advisor.SetStatus("Reading statistics and indexes from the server...", false);

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var info = await reader(document, cts.Token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (cts.IsCancellationRequested || !ReferenceEquals(document, _document) || info == null)
                        return;

                    _serverInfo = info;
                    RunAdvisor();
                    UpdateInfo(_statements.SelectedItem as PlanStatement);

                    int tables = info.Tables.Count();
                    if (info.Errors.Count > 0)
                        _advisor.SetStatus(string.Join(" ", info.Errors), true);
                    else if (tables == 0)
                        _advisor.SetStatus("None of the tables of the plan was found on " + info.Server + ".", true);
                    else
                        _advisor.SetStatus(string.Format(CultureInfo.CurrentCulture, "Statistics and indexes of {0} table(s) read from {1} at {2:HH:mm:ss}.", tables, info.Server, info.ReadAt), false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (ReferenceEquals(document, _document))
                        _advisor.SetStatus("Could not read from the server: " + ex.Message, true);
                }
            });
        }

        /// <summary>Shows the operator of a finding: details panel only, or selected in the Tree tab.</summary>
        private void SelectFinding(AdvisorFinding finding, bool showTree)
        {
            if (finding?.Node == null)
                return;

            if (finding.Statement != null && !ReferenceEquals(_statements.SelectedItem, finding.Statement))
                _statements.SelectedItem = finding.Statement;

            if (showTree)
            {
                _tabs.SelectedIndex = 0;
                Defer(() => _canvas.Select(finding.Node, true));
            }
            else
            {
                _canvas.Select(finding.Node, false);
            }
        }

        /// <summary>Runs an action on the UI thread after the current layout pass (scrolling needs the new sizes).</summary>
        private static void Defer(Action action)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await System.Threading.Tasks.Task.Yield(); // run after the pending layout / render work
                action();
            });
        }

        private void ShowDetails(PlanNode node)
        {
            _details.Children.Clear();
            if (node == null)
                return;

            var header = new TextBlock
            {
                Text = node.IsObject ? node.Title : (node.IsRoot ? node.Title : string.Format(CultureInfo.InvariantCulture, "({0}) {1}", node.NodeId, node.PhysicalOp)),
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _details.Children.Add(header);

            foreach (var p in node.GetProperties())
            {
                _details.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
                var value = new TextBox
                {
                    Text = p.Value,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                };
                value.SetResourceReference(ForegroundProperty, p.Name == "Warning" ? EnvironmentColors.ToolWindowTextBrushKey : EnvironmentColors.ToolWindowTextBrushKey);
                if (p.Name == "Warning")
                    value.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x3B));
                _details.Children.Add(value);
            }

            var statement = _statements.SelectedItem as PlanStatement;
            if (node.IsRoot && statement != null && statement.MissingIndexes.Count > 0)
            {
                _details.Children.Add(new TextBlock { Text = "Suggested index", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
                _details.Children.Add(new TextBox
                {
                    Text = string.Join(Environment.NewLine + Environment.NewLine, statement.MissingIndexes.Select(m => m.ToCreateIndex())),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                });
            }
        }

        private UIElement BuildTreeArea()
        {
            var host = new Grid();
            host.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);

            var zoomHost = new Border { Child = _canvas, LayoutTransform = _zoom, Background = Brushes.Transparent };
            _treeScroll.Content = zoomHost;
            _treeScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _treeScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _treeScroll.PreviewMouseWheel += OnTreeMouseWheel;
            _treeScroll.ScrollChanged += (s, e) => UpdateOverviewViewport();
            host.Children.Add(_treeScroll);

            // Click on an empty area clears nothing but gives the canvas keyboard focus for scrolling.
            zoomHost.MouseLeftButtonDown += (s, e) => _treeScroll.Focus();

            // Overview map (like DB2): a scaled picture of the whole tree with the visible area marked.
            _overview.Width = 200;
            _overview.Height = 150;
            _overview.HorizontalAlignment = HorizontalAlignment.Left;
            _overview.VerticalAlignment = VerticalAlignment.Top;
            _overview.Margin = new Thickness(8);
            _overview.BorderThickness = new Thickness(1);
            _overview.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            _overview.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _overview.Opacity = 0.95;

            var overviewGrid = new Grid();
            _overviewMap.Fill = new VisualBrush(_canvas) { Stretch = Stretch.Uniform, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            overviewGrid.Children.Add(_overviewMap);
            _overviewViewport.Stroke = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x16));
            _overviewViewport.StrokeThickness = 1.5;
            _overviewViewport.Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x16, 0xA3, 0x16));
            _overviewLayer.Children.Add(_overviewViewport);
            overviewGrid.Children.Add(_overviewLayer);
            _overview.Child = overviewGrid;
            _overview.Cursor = Cursors.Hand;
            _overview.MouseLeftButtonDown += (s, e) => { _overview.CaptureMouse(); NavigateFromOverview(e.GetPosition(_overviewLayer)); e.Handled = true; };
            _overview.MouseMove += (s, e) => { if (_overview.IsMouseCaptured) NavigateFromOverview(e.GetPosition(_overviewLayer)); };
            _overview.MouseLeftButtonUp += (s, e) => _overview.ReleaseMouseCapture();
            host.Children.Add(_overview);

            return host;
        }

        private double OverviewScale()
        {
            if (_canvas.Width <= 0 || _canvas.Height <= 0 || double.IsNaN(_canvas.Width))
                return 0;
            return Math.Min(_overview.Width / _canvas.Width, _overview.Height / _canvas.Height);
        }

        private void UpdateOverviewViewport()
        {
            double scale = OverviewScale();
            if (scale <= 0 || _zoom.ScaleX <= 0)
            {
                _overviewViewport.Visibility = Visibility.Collapsed;
                return;
            }

            _overviewViewport.Visibility = Visibility.Visible;
            double x = _treeScroll.HorizontalOffset / _zoom.ScaleX;
            double y = _treeScroll.VerticalOffset / _zoom.ScaleX;
            double w = _treeScroll.ViewportWidth / _zoom.ScaleX;
            double h = _treeScroll.ViewportHeight / _zoom.ScaleX;
            Canvas.SetLeft(_overviewViewport, x * scale);
            Canvas.SetTop(_overviewViewport, y * scale);
            _overviewViewport.Width = Math.Max(4, Math.Min(_canvas.Width, w) * scale);
            _overviewViewport.Height = Math.Max(4, Math.Min(_canvas.Height, h) * scale);
        }

        private void NavigateFromOverview(Point p)
        {
            double scale = OverviewScale();
            if (scale <= 0)
                return;
            double cx = p.X / scale * _zoom.ScaleX;
            double cy = p.Y / scale * _zoom.ScaleX;
            _treeScroll.ScrollToHorizontalOffset(Math.Max(0, cx - _treeScroll.ViewportWidth / 2));
            _treeScroll.ScrollToVerticalOffset(Math.Max(0, cy - _treeScroll.ViewportHeight / 2));
        }

        private void OnTreeMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;
            SetZoom(e.Delta > 0 ? _zoom.ScaleX * 1.1 : _zoom.ScaleX / 1.1);
            e.Handled = true;
        }

        private void SetZoom(double value)
        {
            value = Math.Max(MinZoom, Math.Min(MaxZoom, value));
            _zoom.ScaleX = value;
            _zoom.ScaleY = value;
            UpdateZoomText();
            Defer(UpdateOverviewViewport);
        }

        private void ZoomToFit()
        {
            if (_canvas.Width <= 0 || _treeScroll.ViewportWidth <= 0)
                return;
            SetZoom(Math.Min(_treeScroll.ViewportWidth / _canvas.Width, _treeScroll.ViewportHeight / _canvas.Height));
        }

        private void UpdateZoomText()
        {
            _zoomText.Text = Math.Round(_zoom.ScaleX * 100).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private UIElement BuildOperatorsGrid()
        {
            _operators.AutoGenerateColumns = false;
            _operators.IsReadOnly = true;
            _operators.SelectionMode = DataGridSelectionMode.Single;
            _operators.CanUserAddRows = false;
            _operators.HeadersVisibility = DataGridHeadersVisibility.Column;
            _operators.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;

            AddColumn("Node", nameof(OperatorRow.NodeId), 50, null);
            AddColumn("Operator", nameof(OperatorRow.Operator), 170, null);
            AddColumn("Object", nameof(OperatorRow.Object), 200, null);
            AddColumn("Cost %", nameof(OperatorRow.CostPercent), 65, "0.0");
            AddColumn("Operator cost", nameof(OperatorRow.OwnCost), 90, "0.#####");
            AddColumn("Subtree cost", nameof(OperatorRow.SubtreeCost), 90, "0.#####");
            AddColumn("Est. rows", nameof(OperatorRow.EstimatedRows), 90, "#,0.##");
            AddColumn("Actual rows", nameof(OperatorRow.ActualRows), 90, "#,0");
            AddColumn("Act / Est", nameof(OperatorRow.Ratio), 70, "0.##x");
            AddColumn("Executions", nameof(OperatorRow.Executions), 75, "#,0");
            AddColumn("Logical reads", nameof(OperatorRow.LogicalReads), 90, "#,0");
            AddColumn("Warnings", nameof(OperatorRow.Warnings), 300, null);

            _operators.MouseDoubleClick += (s, e) =>
            {
                if (_operators.SelectedItem is OperatorRow row)
                {
                    _tabs.SelectedIndex = 0;
                    Defer(() => _canvas.Select(row.Node, true));
                }
            };
            _operators.SelectionChanged += (s, e) =>
            {
                if (_operators.SelectedItem is OperatorRow row)
                    ShowDetails(row.Node);
            };

            return _operators;
        }

        private void AddColumn(string header, string path, double width, string format)
        {
            var binding = new Binding(path);
            if (format != null)
                binding.StringFormat = "{0:" + format + "}";
            _operators.Columns.Add(new DataGridTextColumn { Header = header, Binding = binding, Width = width, SortMemberPath = path });
        }

        private UIElement BuildXmlEditor()
        {
            _xml.IsReadOnly = true;
            _xml.ShowLineNumbers = true;
            _xml.FontFamily = new FontFamily("Consolas");
            _xml.FontSize = 12;
            _xml.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML");
            _xml.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _xml.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            EditorZoomBehavior.SetZoomKey(_xml, "PlanXml");
            return _xml;
        }

        private void OpenFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Execution plans (*.sqlplan;*.xml)|*.sqlplan;*.xml|All files (*.*)|*.*",
                Title = "Open execution plan",
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                ShowDocument(ShowPlanParser.Parse(File.ReadAllText(dialog.FileName), System.IO.Path.GetFileName(dialog.FileName)));
            }
            catch (Exception ex)
            {
                ShowError("The file is not a valid execution plan: " + ex.Message);
            }
        }

        private void PasteXml()
        {
            string text = null;
            try
            {
                text = Clipboard.GetText();
            }
            catch
            {
            }

            if (!ShowPlanParser.LooksLikeShowPlan(text))
            {
                ShowError("The clipboard does not contain an execution plan XML. In SSMS right click a graphical plan > Show Execution Plan XML..., select all and copy.");
                return;
            }

            try
            {
                ShowDocument(ShowPlanParser.Parse(text, "clipboard"));
            }
            catch (Exception ex)
            {
                ShowError("The clipboard text is not a valid execution plan: " + ex.Message);
            }
        }

        private void CopyMissingIndexes()
        {
            if (_statements.SelectedItem is PlanStatement statement && statement.MissingIndexes.Count > 0)
            {
                try
                {
                    Clipboard.SetDataObject(string.Join(Environment.NewLine + Environment.NewLine, statement.MissingIndexes.Select(m => m.ToCreateIndex())), true);
                    SetBusy("CREATE INDEX copied to the clipboard.", false);
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
            }
        }

        private static Button ToolButton(string text, string tooltip, RoutedEventHandler click)
        {
            var b = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2), MinWidth = 28 };
            b.Click += click;
            return b;
        }

        private static UIElement ToolSeparator()
        {
            var r = new Rectangle { Width = 1, Margin = new Thickness(6, 4, 6, 4) };
            r.SetResourceReference(Shape.FillProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return r;
        }

        /// <summary>Row of the Operators tab.</summary>
        public sealed class OperatorRow
        {
            public OperatorRow(PlanNode node)
            {
                Node = node;
            }

            public PlanNode Node { get; }
            public int NodeId => Node.NodeId;
            public string Operator => Node.PhysicalOp;
            public string Object => Node.ObjectDisplayName;
            public double CostPercent => Node.CostPercent;
            public double OwnCost => Node.OwnCost;
            public double SubtreeCost => Node.SubtreeCost;
            public double EstimatedRows => Node.EstimateRowsAllExecutions;
            public double? ActualRows => Node.HasActual ? (double?)Node.ActualRows : null;
            public double? Ratio => Node.RowEstimateRatio;
            public double? Executions => Node.HasActual ? (double?)Node.ActualExecutions : null;
            public double? LogicalReads => Node.ActualLogicalReads;
            public string Warnings => string.Join("; ", Node.AllWarnings());
        }
    }
}
