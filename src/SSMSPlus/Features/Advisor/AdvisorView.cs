namespace SSMSPlus.Features.Advisor
{
    using ICSharpCode.AvalonEdit;
    using ICSharpCode.AvalonEdit.Highlighting;
    using Microsoft.VisualStudio.PlatformUI;
    using SSMSPlusCore.Ui.Extensions;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Data;
    using System.Windows.Documents;
    using System.Windows.Media;

    /// <summary>
    /// "Advisor" tab of the Execution Plan Tree window: list of findings ordered by severity, and the
    /// explanation, suggestion and script of the selected one. Built in code (no XAML).
    /// </summary>
    internal sealed class AdvisorView : UserControl
    {
        private static readonly Brush HighBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x3B));
        private static readonly Brush MediumBrush = Frozen(Color.FromRgb(0xE8, 0x9A, 0x1C));
        private static readonly Brush InfoBrush = Frozen(Color.FromRgb(0x3B, 0x8E, 0xEA));

        private readonly TextBlock _summary = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly DataGrid _grid = new DataGrid();
        private readonly StackPanel _text = new StackPanel();
        private readonly TextEditor _script = new TextEditor();
        private readonly Button _copyScript;
        private readonly Button _openScript;
        private readonly Button _showInTree;
        private readonly Button _readServer;
        private bool _filling;

        public AdvisorView()
        {
            // Follow the SSMS theme (the tab content would otherwise use the default white background).
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var root = new Grid();
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            root.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Content = root;

            // ---- top bar
            var bar = new WrapPanel { Margin = new Thickness(4) };
            _summary.FontWeight = FontWeights.SemiBold;
            _summary.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _summary.VerticalAlignment = VerticalAlignment.Center;
            _summary.Margin = new Thickness(4, 0, 12, 0);
            bar.Children.Add(_summary);
            _readServer = MakeButton("Read from server", "Read row counts, statistics (last update, modifications, sample) and index definitions of the tables in the plan, using the connection of the query window.", (s, e) => ReadServerRequested?.Invoke());
            bar.Children.Add(_readServer);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(10, 0, 4, 0);
            _status.FontStyle = FontStyles.Italic;
            _status.TextWrapping = TextWrapping.Wrap;
            bar.Children.Add(_status);
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            // ---- findings
            _grid.AutoGenerateColumns = false;
            _grid.IsReadOnly = true;
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.CanUserAddRows = false;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;

            var severityStyle = new Style(typeof(TextBlock));
            severityStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(FindingRow.SeverityBrush))));
            severityStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            _grid.Columns.Add(new DataGridTextColumn { Header = "Severity", Binding = new Binding(nameof(FindingRow.SeverityLabel)), Width = 80, ElementStyle = severityStyle, SortMemberPath = nameof(FindingRow.SeverityOrder) });
            AddColumn("Rule", nameof(FindingRow.RuleId), 45);
            AddColumn("Category", nameof(FindingRow.Category), 90);
            AddColumn("Finding", nameof(FindingRow.Title), 300);
            AddColumn("Object", nameof(FindingRow.Object), 230);
            AddColumn("Operator", nameof(FindingRow.Location), 170);
            AddColumn("Stmt", nameof(FindingRow.StatementIndex), 45);
            _grid.SelectionChanged += (s, e) => ShowFinding((_grid.SelectedItem as FindingRow)?.Finding);
            _grid.MouseDoubleClick += (s, e) =>
            {
                if (_grid.SelectedItem is FindingRow row)
                    ShowInTreeRequested?.Invoke(row.Finding);
            };
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
            splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetRow(splitter, 2);
            root.Children.Add(splitter);

            // ---- details: text | script
            var details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(details, 3);
            root.Children.Add(details);

            var textScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8) };
            textScroll.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            textScroll.Content = _text;
            Grid.SetColumn(textScroll, 0);
            details.Children.Add(textScroll);

            var detailsSplitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            detailsSplitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            Grid.SetColumn(detailsSplitter, 1);
            details.Children.Add(detailsSplitter);

            var scriptPanel = new DockPanel();
            var scriptBar = new WrapPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(scriptBar, Dock.Top);
            _copyScript = MakeButton("Copy script", "Copy the suggested T-SQL to the clipboard", (s, e) => CopyScript());
            _openScript = MakeButton("Open in query window", "Open the suggested T-SQL in a new query window (it is not executed)", (s, e) =>
            {
                if (!string.IsNullOrEmpty(_script.Text))
                    OpenScriptRequested?.Invoke(_script.Text);
            });
            _showInTree = MakeButton("Show in tree", "Select the operator in the plan tree", (s, e) =>
            {
                if (_grid.SelectedItem is FindingRow row)
                    ShowInTreeRequested?.Invoke(row.Finding);
            });
            scriptBar.Children.Add(_copyScript);
            scriptBar.Children.Add(_openScript);
            scriptBar.Children.Add(_showInTree);
            scriptPanel.Children.Add(scriptBar);

            _script.IsReadOnly = true;
            _script.ShowLineNumbers = false;
            _script.WordWrap = true;
            _script.FontFamily = new FontFamily("Consolas");
            _script.FontSize = 12;
            _script.Padding = new Thickness(4);
            _script.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
            _script.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _script.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            EditorZoomBehavior.SetZoomKey(_script, "AdvisorScript");
            scriptPanel.Children.Add(_script);
            Grid.SetColumn(scriptPanel, 2);
            details.Children.Add(scriptPanel);

            SetFindings(null, false);
        }

        /// <summary>A finding was selected in the list (show the operator details).</summary>
        public event Action<AdvisorFinding> FindingSelected;

        /// <summary>Double click / "Show in tree".</summary>
        public event Action<AdvisorFinding> ShowInTreeRequested;

        public Action ReadServerRequested { get; set; }

        public Action<string> OpenScriptRequested { get; set; }

        public IReadOnlyList<AdvisorFinding> Findings { get; private set; } = new List<AdvisorFinding>();

        public void SetFindings(List<AdvisorFinding> findings, bool canReadServer)
        {
            Findings = findings ?? new List<AdvisorFinding>();
            _filling = true;
            try
            {
                _grid.ItemsSource = Findings.Select(f => new FindingRow(f)).ToList();
                _grid.SelectedIndex = Findings.Count > 0 ? 0 : -1;
            }
            finally
            {
                _filling = false;
            }
            _readServer.IsEnabled = canReadServer;

            if (findings == null)
                _summary.Text = "No plan.";
            else if (Findings.Count == 0)
                _summary.Text = "No findings.";
            else
                _summary.Text = Summary(Findings);

            if (Findings.Count == 0)
                ShowFinding(null);
        }

        public static string Summary(IReadOnlyCollection<AdvisorFinding> findings) => QueryAdvisor.Summary(findings);

        public void SetStatus(string text, bool error)
        {
            _status.Text = text ?? string.Empty;
            if (error)
                _status.Foreground = HighBrush;
            else
                _status.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        }

        private void ShowFinding(AdvisorFinding finding)
        {
            _text.Children.Clear();
            _script.Text = finding?.Script ?? string.Empty;
            _copyScript.IsEnabled = !string.IsNullOrEmpty(finding?.Script);
            _openScript.IsEnabled = _copyScript.IsEnabled && OpenScriptRequested != null;
            _showInTree.IsEnabled = finding?.Node != null;

            if (finding == null)
            {
                _text.Children.Add(Paragraph(Findings.Count == 0
                    ? "The advisor checks the plan for expensive lookups and scans, wrong row estimates, spills, implicit conversions, missing indexes and out-of-date statistics. Nothing to report for this plan."
                    : string.Empty));
                return;
            }

            _text.Children.Add(new TextBlock
            {
                Text = finding.RuleId + "  " + finding.Title,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushFor(finding.Severity),
                Margin = new Thickness(0, 0, 0, 6),
            });
            if (!string.IsNullOrEmpty(finding.Object))
                _text.Children.Add(Paragraph(finding.Object));
            AddSection("Why", finding.Explanation);
            AddSection("Suggestion", finding.Suggestion);
            if (!string.IsNullOrEmpty(finding.Script))
                AddSection(null, "Review the script before running it: it changes indexes or statistics on the server.");

            if (!_filling)
                FindingSelected?.Invoke(finding);
        }

        private void AddSection(string header, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (header != null)
            {
                var title = new TextBlock { Text = header, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 12, 0, 3) };
                title.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                _text.Children.Add(title);
            }
            _text.Children.Add(Paragraph(text));
        }

        private static TextBox Paragraph(string text)
        {
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
            };
            box.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return box;
        }

        private void CopyScript()
        {
            if (string.IsNullOrEmpty(_script.Text))
                return;
            try
            {
                Clipboard.SetDataObject(_script.Text, true);
                SetStatus("Script copied to the clipboard.", false);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, true);
            }
        }

        private void AddColumn(string header, string path, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width, SortMemberPath = path });
        }

        private static Button MakeButton(string text, string tooltip, RoutedEventHandler click)
        {
            var b = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += click;
            return b;
        }

        internal static Brush BrushFor(AdvisorSeverity severity)
        {
            switch (severity)
            {
                case AdvisorSeverity.High: return HighBrush;
                case AdvisorSeverity.Medium: return MediumBrush;
                default: return InfoBrush;
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>Row of the findings list.</summary>
        public sealed class FindingRow
        {
            public FindingRow(AdvisorFinding finding)
            {
                Finding = finding;
            }

            public AdvisorFinding Finding { get; }
            public string SeverityLabel => "● " + Finding.Severity;
            public int SeverityOrder => (int)Finding.Severity;
            public Brush SeverityBrush => BrushFor(Finding.Severity);
            public string RuleId => Finding.RuleId;
            public string Category => Finding.Category;
            public string Title => Finding.Title;
            public string Object => Finding.Object;
            public string Location => Finding.Location;
            public int StatementIndex => Finding.StatementIndex;
        }
    }
}
