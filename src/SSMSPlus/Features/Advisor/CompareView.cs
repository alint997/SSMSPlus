namespace SSMSPlus.Features.Advisor
{
    using Microsoft.VisualStudio.PlatformUI;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Documents;
    using System.Windows.Input;
    using System.Windows.Media;

    /// <summary>
    /// "Compare" tab of the Execution Plan Tree window. Every plan shown in the window is kept (last 20, this
    /// session only), so nothing has to be prepared before a change: "Before" is by default the previous plan of
    /// the same query window and "After" the latest plan. Choosing a plan in "Before" (or "Set as baseline")
    /// pins it as the baseline until "Clear baseline". The result is shown first (better / worse and why), then
    /// the details per statement. Built in code, themed; Ctrl + mouse wheel zooms.
    /// </summary>
    internal sealed class CompareView : UserControl
    {
        private const int MaxHistory = 20;

        private static readonly Brush BetterBrush = Frozen(Color.FromRgb(0x3F, 0xA3, 0x4D));
        private static readonly Brush WorseBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x3B));
        private static readonly Brush MixedBrush = Frozen(Color.FromRgb(0xE8, 0x9A, 0x1C));
        private static readonly Brush NeutralBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
        private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x3B, 0x8E, 0xEA));
        private static double _zoom = 1;
        private static bool _detailsOpen;

        private readonly List<PlanSnapshot> _history = new List<PlanSnapshot>();
        private readonly ComboBox _before = new ComboBox();
        private readonly ComboBox _after = new ComboBox();
        private readonly Button _swap;
        private readonly Button _clearBaseline;
        private readonly Button _copy;
        private readonly Button _clearHistory;
        private readonly StackPanel _body = new StackPanel();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private PlanSnapshot _pinned;      // "Before" chosen by the user; null = automatic
        private PlanSnapshot _afterChoice; // "After" chosen by the user; null = latest plan
        private bool _filling;
        private int _next = 1;

        public CompareView()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var root = new DockPanel { LastChildFill = true };
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            root.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            Content = root;

            var bar = new WrapPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(bar, Dock.Top);
            bar.Children.Add(BarLabel("Before:"));
            _before.MinWidth = 260;
            _before.MaxWidth = 560;
            _before.Margin = new Thickness(0, 2, 10, 2);
            _before.ToolTip = "The plan to compare against. \"Automatic\" takes the previous plan of the same query window. Choosing a plan pins it as the baseline until \"Clear baseline\".";
            _before.SelectionChanged += (s, e) =>
            {
                if (_filling)
                    return;
                _pinned = (_before.SelectedItem as Choice)?.Snapshot;
                PinnedChanged?.Invoke();
                Update();
            };
            bar.Children.Add(_before);

            bar.Children.Add(BarLabel("After:"));
            _after.MinWidth = 260;
            _after.MaxWidth = 560;
            _after.Margin = new Thickness(0, 2, 10, 2);
            _after.ToolTip = "The plan to evaluate. \"Latest plan\" follows each new plan (Refresh / F5).";
            _after.SelectionChanged += (s, e) =>
            {
                if (_filling)
                    return;
                _afterChoice = (_after.SelectedItem as Choice)?.Snapshot;
                Update();
            };
            bar.Children.Add(_after);

            _swap = BarButton("Swap", "Swap Before and After", (s, e) => Swap());
            bar.Children.Add(_swap);
            _clearBaseline = BarButton("Clear baseline", "Unpin the baseline: Before goes back to the previous plan of the same query window", (s, e) =>
            {
                _pinned = null;
                PinnedChanged?.Invoke();
                Update();
            });
            bar.Children.Add(_clearBaseline);
            _copy = BarButton("Copy as text", "Copy the comparison to the clipboard as text", (s, e) => Copy());
            bar.Children.Add(_copy);
            _clearHistory = BarButton("Clear history", "Forget the plans kept for comparison (the latest one stays)", (s, e) => ClearHistory());
            bar.Children.Add(_clearHistory);
            root.Children.Add(bar);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            scroll.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _body.Margin = new Thickness(12, 6, 12, 12);
            _body.LayoutTransform = _scale;
            scroll.Content = _body;
            scroll.PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
                    e.Handled = true;
                }
            };
            root.Children.Add(scroll);

            SetZoom(_zoom);
            Update();
        }

        /// <summary>The baseline was pinned, replaced or cleared.</summary>
        public event Action PinnedChanged;

        /// <summary>New header for the tab ("Compare", "Compare: better", ...).</summary>
        public event Action<string> HeaderChanged;

        /// <summary>The pinned baseline, or null when Before is automatic.</summary>
        public PlanSnapshot Pinned => _pinned;

        /// <summary>The comparison shown, as text (report); null when there is none.</summary>
        public string ComparisonText => Comparison?.Text;

        /// <summary>The comparison shown (report); null when there is none.</summary>
        public ComparisonResult Comparison { get; private set; }

        /// <summary>Keeps a new plan (it becomes After), or refreshes one already kept (its advisor result changed).</summary>
        public void AddOrUpdate(PlanSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            if (!_history.Contains(snapshot))
            {
                snapshot.Number = _next++;
                _history.Add(snapshot);
                while (_history.Count > MaxHistory)
                {
                    var oldest = _history.FirstOrDefault(h => h != _pinned);
                    if (oldest == null)
                        break;
                    _history.Remove(oldest);
                }
                _afterChoice = null;
            }

            Update();
        }

        /// <summary>Pins a plan as the baseline (toolbar "Set as baseline").</summary>
        public void Pin(PlanSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            if (!_history.Contains(snapshot))
                AddOrUpdate(snapshot);
            _pinned = snapshot;
            PinnedChanged?.Invoke();
            Update();
        }

        // ------------------------------------------------------------------ selection

        private PlanSnapshot ResolveAfter()
        {
            return _afterChoice != null && _history.Contains(_afterChoice) ? _afterChoice : _history.LastOrDefault();
        }

        private PlanSnapshot ResolveBefore(PlanSnapshot after)
        {
            if (_pinned != null && _history.Contains(_pinned))
                return _pinned;
            return AutoBefore(after);
        }

        /// <summary>The previous plan of the same query window and kind, else of the same window, else the previous one.</summary>
        private PlanSnapshot AutoBefore(PlanSnapshot after)
        {
            if (after == null)
                return null;
            int index = _history.IndexOf(after);
            if (index <= 0)
                return null;

            var older = _history.Take(index).Reverse().ToList();
            string source = Window(after);
            return older.FirstOrDefault(h => Window(h) == source && h.IsActual == after.IsActual)
                ?? older.FirstOrDefault(h => Window(h) == source)
                ?? older.First();
        }

        /// <summary>Where a plan comes from: query window name, server and database (PlanDocument.Source).</summary>
        private static string Window(PlanSnapshot s) => s.Document?.Source ?? string.Empty;

        private void Swap()
        {
            var after = ResolveAfter();
            var before = ResolveBefore(after);
            if (before == null || after == null || before == after)
                return;
            _pinned = after;
            _afterChoice = before;
            PinnedChanged?.Invoke();
            Update();
        }

        private void ClearHistory()
        {
            var latest = _history.LastOrDefault();
            _history.Clear();
            if (latest != null)
                _history.Add(latest);
            _pinned = null;
            _afterChoice = null;
            PinnedChanged?.Invoke();
            Update();
        }

        private void Copy()
        {
            if (string.IsNullOrEmpty(ComparisonText))
                return;
            try
            {
                Clipboard.SetDataObject(ComparisonText, true);
            }
            catch
            {
                // Clipboard busy: nothing else to do.
            }
        }

        private void FillCombos(PlanSnapshot after)
        {
            _filling = true;
            try
            {
                var auto = AutoBefore(after);
                var beforeItems = new List<Choice> { new Choice(null, "Automatic: previous plan" + (auto != null ? " (#" + auto.Number + ")" : string.Empty)) };
                beforeItems.AddRange(Enumerable.Reverse(_history).Select(h => new Choice(h, h.Label + (h == _pinned ? "   [baseline]" : string.Empty))));
                _before.ItemsSource = beforeItems;
                _before.SelectedItem = beforeItems.FirstOrDefault(c => c.Snapshot == _pinned) ?? beforeItems[0];

                var latest = _history.LastOrDefault();
                var afterItems = new List<Choice> { new Choice(null, "Latest plan" + (latest != null ? " (#" + latest.Number + ")" : string.Empty)) };
                afterItems.AddRange(Enumerable.Reverse(_history).Select(h => new Choice(h, h.Label)));
                _after.ItemsSource = afterItems;
                _after.SelectedItem = afterItems.FirstOrDefault(c => c.Snapshot != null && c.Snapshot == _afterChoice) ?? afterItems[0];
            }
            finally
            {
                _filling = false;
            }
        }

        // ------------------------------------------------------------------ display

        private void Update()
        {
            var after = ResolveAfter();
            var before = after == null ? null : ResolveBefore(after);
            FillCombos(after);
            _body.Children.Clear();
            Comparison = null;

            _clearBaseline.Visibility = _pinned != null ? Visibility.Visible : Visibility.Collapsed;
            _swap.IsEnabled = before != null && after != null && before != after;
            _clearHistory.IsEnabled = _history.Count > 1;

            if (after == null)
            {
                Message("No plan yet.",
                    "Get a plan (Estimated plan / Actual plan), then change the query, the indexes or the statistics and press Refresh (F5). " +
                    "Every plan is kept here and the new one is compared with the previous one automatically.");
                Done("Compare");
                return;
            }

            if (before == null || before == after)
            {
                if (_pinned != null && _pinned == after)
                    Message("Baseline #" + after.Number + " is set.",
                        "Change the query, the indexes or the statistics and press Refresh (F5): the new plan is compared with #" + after.Number + ".");
                else
                    Message("Only one plan so far (#" + after.Number + ").",
                        "Change the query, the indexes or the statistics and press Refresh (F5): this plan is kept and compared with the new one. " +
                        "To always compare with this plan, click \"Set as baseline\".");
                Done("Compare");
                return;
            }

            try
            {
                var result = PlanComparer.Compare(before, after);
                Comparison = result;
                Render(result);
                Done("Compare: " + PlanComparer.Verdict(result.Trend));
            }
            catch (Exception ex)
            {
                Message("The comparison failed.", ex.Message);
                Done("Compare");
            }
        }

        private void Done(string header)
        {
            _copy.IsEnabled = !string.IsNullOrEmpty(ComparisonText);
            HeaderChanged?.Invoke(header);
        }

        private void Message(string title, string text)
        {
            _body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap });
            _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left });
        }

        private void Render(ComparisonResult r)
        {
            // 1. The result in plain words.
            var brush = TrendBrush(r.Trend);
            var banner = new StackPanel();
            banner.Children.Add(new TextBlock { Text = r.Overall.ToUpperInvariant(), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = brush });
            if (!string.IsNullOrEmpty(r.Summary))
                banner.Children.Add(new TextBlock { Text = r.Summary, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) });
            banner.Children.Add(Small("Before:  " + r.Before.Label, new Thickness(0, 4, 0, 0)));
            banner.Children.Add(Small("After:     " + r.After.Label, new Thickness(0)));
            _body.Children.Add(new Border
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(12, 4, 8, 6),
                Margin = new Thickness(0, 4, 0, 12),
                Child = banner,
            });

            // 2. Chart: the key measures before / after as bars.
            if (r.Kpis.Count > 0)
            {
                var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                foreach (var k in r.Kpis)
                    cards.Children.Add(KpiCard(k));
                _body.Children.Add(cards);
            }

            // 3. What changed, in a few lines.
            if (r.Good.Count + r.Bad.Count + r.Notes.Count > 0)
            {
                _body.Children.Add(SubHeader("What changed"));
                foreach (var g in r.Good)
                    _body.Children.Add(Marked("✓", BetterBrush, g));
                foreach (var b in r.Bad)
                    _body.Children.Add(Marked("✗", WorseBrush, b));
                foreach (var n in r.Notes)
                    _body.Children.Add(Marked("i", NeutralBrush, n, italic: true));
            }

            // 4. Details per statement, on request.
            var details = new StackPanel { Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var s in r.Statements)
                details.Children.Add(StatementPanel(s, r.Statements.Count > 1));
            var toggle = new Button
            {
                Content = _detailsOpen ? "Hide details" : "Show details per statement",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(10, 3, 10, 3),
                ToolTip = "Measures, tables read differently and advisor findings of each statement",
            };
            toggle.Click += (s, e) =>
            {
                _detailsOpen = !_detailsOpen;
                details.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = _detailsOpen ? "Hide details" : "Show details per statement";
            };
            _body.Children.Add(toggle);
            _body.Children.Add(details);
        }

        /// <summary>A measure as a small chart: two bars (before grey, after colored) on the same scale and the change.</summary>
        private static UIElement KpiCard(KpiRow k)
        {
            const double BarWidth = 120;
            var trendBrush = k.Trend == 0 ? NeutralBrush : TrendBrush(k.Trend);
            var panel = new StackPanel();

            var title = new TextBlock { Text = k.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(title);
            if (k.IsPrimary)
                panel.Children.Add(new TextBlock { Text = "decides the result", FontSize = 11, Opacity = 0.7, FontStyle = FontStyles.Italic });

            string change = string.IsNullOrEmpty(k.Change) ? "-" : k.Change;
            panel.Children.Add(new TextBlock
            {
                Text = change + (k.Trend > 0 ? "  better" : k.Trend < 0 ? "  worse" : string.Empty),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = trendBrush,
                Margin = new Thickness(0, 2, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });

            double max = Math.Max(k.Before ?? 0, k.After ?? 0);
            panel.Children.Add(Bar("Before", k.Before, k.BeforeText, max, BarWidth, NeutralBrush));
            panel.Children.Add(Bar("After", k.After, k.AfterText, max, BarWidth, k.Trend == 0 ? AccentBrush : trendBrush));

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 10, 10),
                Width = 260,
                Child = panel,
            };
            card.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            if (k.IsPrimary)
                card.BorderThickness = new Thickness(2);
            return card;
        }

        private static UIElement Bar(string label, double? value, string text, double max, double width, Brush brush)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = label, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);

            double w = max > 0 && value.HasValue ? Math.Max(2, width * value.Value / max) : 2;
            var rect = new System.Windows.Shapes.Rectangle { Width = w, Height = 12, Fill = brush, VerticalAlignment = VerticalAlignment.Center, RadiusX = 2, RadiusY = 2 };
            Grid.SetColumn(rect, 1);
            row.Children.Add(rect);

            var t = new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 2);
            row.Children.Add(t);
            return row;
        }

        private UIElement StatementPanel(StatementComparison s, bool several)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 14) };
            var header = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
            header.Inlines.Add(new Run("Statement " + s.Index) { FontWeight = FontWeights.Bold });
            if (s.IsNew)
                header.Inlines.Add(new Run("  new in After (not compared)") { Foreground = NeutralBrush });
            else if (s.IsRemoved)
                header.Inlines.Add(new Run("  only in Before (not compared)") { Foreground = NeutralBrush });
            else
                header.Inlines.Add(new Run("  " + PlanComparer.Verdict(s.Trend)) { Foreground = TrendBrush(s.Trend), FontWeight = FontWeights.SemiBold });
            panel.Children.Add(header);
            panel.Children.Add(Small(PlanComparer.Short(s.Text, 200) + (s.TextChanged ? "   (text changed)" : string.Empty), new Thickness(0, 0, 0, 6)));
            if (s.IsNew || s.IsRemoved)
                return panel;

            // Measures table.
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            for (int c = 0; c < 4; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddRow(grid, 0, "", "Before", "After", "Change", null, true);
            int row = 1;
            foreach (var m in s.Measures)
            {
                string change = m.Change + (m.Trend > 0 ? "  better" : m.Trend < 0 ? "  worse" : string.Empty);
                AddRow(grid, row++, m.Label, m.Before, m.After, change, m.Trend == 0 ? null : TrendBrush(m.Trend), false);
            }
            panel.Children.Add(grid);

            // Tables read differently.
            if (s.Access.Count > 0)
            {
                panel.Children.Add(SubHeader("Tables read differently"));
                foreach (var c in s.Access)
                {
                    var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
                    line.Inlines.Add(new Run((c.Trend > 0 ? "✓ " : c.Trend < 0 ? "✗ " : "~ ")) { Foreground = c.Trend == 0 ? NeutralBrush : TrendBrush(c.Trend), FontWeight = FontWeights.Bold });
                    line.Inlines.Add(new Run(c.Table) { FontWeight = FontWeights.SemiBold });
                    line.Inlines.Add(new Run(":  " + c.Before + "  →  " + c.After));
                    panel.Children.Add(line);
                    if (!string.IsNullOrEmpty(c.Meaning))
                        panel.Children.Add(Small(c.Meaning, new Thickness(18, 0, 0, 2)));
                }
            }

            // Advisor findings.
            panel.Children.Add(SubHeader("Advisor findings: " + s.FindingsBefore + "  →  " + s.FindingsAfter));
            AddFindings(panel, "Fixed", s.Fixed, BetterBrush);
            AddFindings(panel, "New", s.New, WorseBrush);
            AddFindings(panel, "Still open", s.Open, NeutralBrush);
            if (s.Fixed.Count == 0 && s.New.Count == 0 && s.Open.Count == 0)
                panel.Children.Add(Small("None in either plan.", new Thickness(0)));

            if (several)
                panel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 0), Opacity = 0.4 });
            return panel;
        }

        private static void AddFindings(StackPanel panel, string title, List<AdvisorFinding> list, Brush brush)
        {
            if (list.Count == 0)
                return;
            panel.Children.Add(new TextBlock { Text = title + " (" + list.Count + ")", Foreground = brush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
            foreach (var f in list.Take(10))
                panel.Children.Add(new TextBlock { Text = PlanComparer.FindingText(f), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 0, 0) });
            if (list.Count > 10)
                panel.Children.Add(new TextBlock { Text = "... and " + (list.Count - 10) + " more", Margin = new Thickness(14, 0, 0, 0), Opacity = 0.7 });
        }

        private static void AddRow(Grid grid, int row, string label, string before, string after, string change, Brush changeBrush, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = new[] { label, before, after, change };
            for (int c = 0; c < 4; c++)
            {
                var t = new TextBlock
                {
                    Text = cells[c] ?? string.Empty,
                    Margin = new Thickness(c == 0 ? 0 : 18, 1, 0, 1),
                    TextAlignment = c == 1 || c == 2 ? TextAlignment.Right : TextAlignment.Left,
                    HorizontalAlignment = c == 1 || c == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                };
                if (header)
                {
                    t.FontWeight = FontWeights.SemiBold;
                    t.Opacity = 0.75;
                }
                if (c == 3 && changeBrush != null)
                {
                    t.Foreground = changeBrush;
                    t.FontWeight = FontWeights.SemiBold;
                }
                Grid.SetRow(t, row);
                Grid.SetColumn(t, c);
                grid.Children.Add(t);
            }
        }

        private static TextBlock Marked(string mark, Brush brush, string text, bool italic = false)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            t.Inlines.Add(new Run(mark + "  ") { Foreground = brush, FontWeight = FontWeights.Bold });
            t.Inlines.Add(new Run(text) { FontStyle = italic ? FontStyles.Italic : FontStyles.Normal });
            return t;
        }

        private static TextBlock SubHeader(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap };
        }

        private static TextBlock Small(string text, Thickness margin)
        {
            return new TextBlock { Text = text, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = margin };
        }

        private static TextBlock BarLabel(string text)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        }

        private static Button BarButton(string text, string tooltip, RoutedEventHandler click)
        {
            var b = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
            ToolTipService.SetShowOnDisabled(b, true);
            b.Click += click;
            return b;
        }

        private static Brush TrendBrush(int trend) => trend == 2 ? MixedBrush : trend > 0 ? BetterBrush : trend < 0 ? WorseBrush : NeutralBrush;

        private void SetZoom(double zoom)
        {
            _zoom = Math.Max(0.6, Math.Min(3, zoom));
            _scale.ScaleX = _zoom;
            _scale.ScaleY = _zoom;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private sealed class Choice
        {
            public Choice(PlanSnapshot snapshot, string text)
            {
                Snapshot = snapshot;
                Text = text;
            }

            public PlanSnapshot Snapshot { get; }

            public string Text { get; }

            public override string ToString() => Text;
        }
    }
}
