namespace SSMSPlus.Features.ExecutionPlan
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Shapes;

    /// <summary>
    /// Draws a plan as a top-down tree in the style of DB2 Visual Explain: octagons for operators colored by
    /// family, an ellipse for the statement, rectangles for tables and hexagons for indexes; every node shows its
    /// cost and row counts, and edges get thicker with the number of rows flowing through them.
    /// </summary>
    internal sealed class PlanTreeCanvas : Canvas
    {
        private static readonly Brush NodeText = Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E));
        private static readonly Brush NodeSubText = Frozen(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush NormalStroke = Frozen(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly Brush HotStroke = Frozen(Color.FromRgb(0xC0, 0x00, 0x00));
        private static readonly Brush WarmStroke = Frozen(Color.FromRgb(0xE0, 0x7A, 0x00));
        private static readonly Brush SelectedStroke = Frozen(Color.FromRgb(0x16, 0xA3, 0x16));
        private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));

        private readonly Dictionary<PlanNode, FrameworkElement> _visuals = new Dictionary<PlanNode, FrameworkElement>();
        private readonly Dictionary<PlanNode, Shape> _shapes = new Dictionary<PlanNode, Shape>();
        private PlanNode _selected;

        public event EventHandler<PlanNode> SelectedNodeChanged;

        public PlanNode SelectedNode => _selected;

        public static Brush FillFor(PlanNodeCategory category)
        {
            switch (category)
            {
                case PlanNodeCategory.Root: return Frozen(Color.FromRgb(0xEE, 0xF0, 0xFA));
                case PlanNodeCategory.Scan: return Frozen(Color.FromRgb(0xF7, 0xB3, 0xB3));
                case PlanNodeCategory.Seek: return Frozen(Color.FromRgb(0xFF, 0xF1, 0xAE));
                case PlanNodeCategory.Lookup: return Frozen(Color.FromRgb(0xFF, 0xE0, 0x8A));
                case PlanNodeCategory.Join: return Frozen(Color.FromRgb(0xF8, 0xC9, 0xC0));
                case PlanNodeCategory.Sort: return Frozen(Color.FromRgb(0xFF, 0xC8, 0x8C));
                case PlanNodeCategory.Aggregate: return Frozen(Color.FromRgb(0xFF, 0xD9, 0xA8));
                case PlanNodeCategory.Spool: return Frozen(Color.FromRgb(0xD6, 0xC2, 0xEC));
                case PlanNodeCategory.Parallelism: return Frozen(Color.FromRgb(0xBF, 0xE4, 0xDE));
                case PlanNodeCategory.Table: return Frozen(Color.FromRgb(0xF4, 0xCF, 0xE6));
                case PlanNodeCategory.Index: return Frozen(Color.FromRgb(0xD5, 0xEA, 0xC0));
                default: return Frozen(Color.FromRgb(0xDD, 0xE5, 0xEE));
            }
        }

        public static string CategoryLegend(PlanNodeCategory category)
        {
            switch (category)
            {
                case PlanNodeCategory.Scan: return "Scan";
                case PlanNodeCategory.Seek: return "Seek";
                case PlanNodeCategory.Lookup: return "Lookup";
                case PlanNodeCategory.Join: return "Join";
                case PlanNodeCategory.Sort: return "Sort";
                case PlanNodeCategory.Aggregate: return "Aggregate";
                case PlanNodeCategory.Spool: return "Spool / temp";
                case PlanNodeCategory.Parallelism: return "Parallelism";
                case PlanNodeCategory.Table: return "Table";
                case PlanNodeCategory.Index: return "Index";
                case PlanNodeCategory.Root: return "Statement";
                default: return "Other";
            }
        }

        public void ShowPlan(PlanStatement statement)
        {
            Children.Clear();
            _visuals.Clear();
            _shapes.Clear();
            _selected = null;

            if (statement?.Root == null)
            {
                Width = 0;
                Height = 0;
                return;
            }

            var layout = PlanLayout.Compute(statement.Root);
            Width = layout.Width;
            Height = layout.Height;

            // Each child gets its own entry point on the bottom of its parent (ordered like the children), and the
            // horizontal runs of children on the same side are on different levels, so lines do not overlap or cross.
            foreach (var group in layout.Edges.GroupBy(e => e.Parent))
            {
                var edges = group.OrderBy(e => e.Child.CenterX).ToList();
                var parent = group.Key;
                int n = edges.Count;
                double spacing = n > 1 ? Math.Min(22, parent.Width * 0.6 / (n - 1)) : 0;
                double firstPort = parent.CenterX - spacing * (n - 1) / 2;
                for (int i = 0; i < n; i++)
                {
                    double port = firstPort + spacing * i;
                    double cx = edges[i].Child.CenterX;

                    // Children further out on each side run higher (closer to the parent) than the inner ones,
                    // so a horizontal run never crosses the vertical line of a sibling.
                    int level;
                    if (cx < port - 0.5)
                        level = Enumerable.Range(i + 1, n - i - 1).Count(j => edges[j].Child.CenterX < firstPort + spacing * j - 0.5);
                    else if (cx > port + 0.5)
                        level = Enumerable.Range(0, i).Count(j => edges[j].Child.CenterX > firstPort + spacing * j + 0.5);
                    else
                        level = 0;
                    Children.Add(CreateEdge(edges[i], port, level));
                }
            }

            foreach (var box in layout.Nodes)
            {
                var visual = CreateNode(box);
                SetLeft(visual, box.X);
                SetTop(visual, box.Y);
                Children.Add(visual);
                _visuals[box.Node] = visual;
            }
        }

        public void Select(PlanNode node, bool bringIntoView)
        {
            if (_selected != null && _shapes.TryGetValue(_selected, out var previous))
                ApplyStroke(previous, _selected, false);

            _selected = node;
            if (node != null && _shapes.TryGetValue(node, out var shape))
            {
                ApplyStroke(shape, node, true);
                if (bringIntoView && _visuals.TryGetValue(node, out var visual))
                    visual.BringIntoView(new Rect(-60, -60, visual.Width + 120, visual.Height + 120));
            }

            SelectedNodeChanged?.Invoke(this, node);
        }

        private static void ApplyStroke(Shape shape, PlanNode node, bool selected)
        {
            if (selected)
            {
                shape.Stroke = SelectedStroke;
                shape.StrokeThickness = 3;
                shape.StrokeDashArray = new DoubleCollection { 2, 1 };
                return;
            }

            shape.StrokeDashArray = null;
            if (!node.IsObject && !node.IsRoot && node.CostPercent >= 25)
            {
                shape.Stroke = HotStroke;
                shape.StrokeThickness = 3;
            }
            else if (!node.IsObject && !node.IsRoot && node.CostPercent >= 10)
            {
                shape.Stroke = WarmStroke;
                shape.StrokeThickness = 2;
            }
            else
            {
                shape.Stroke = NormalStroke;
                shape.StrokeThickness = 1;
            }
        }

        private FrameworkElement CreateNode(PlanNodeBox box)
        {
            var node = box.Node;
            var grid = new Grid { Width = box.Width, Height = box.Height, Cursor = Cursors.Hand, Background = Brushes.Transparent };

            var shape = CreateShape(node.Category, box.Width, box.Height);
            shape.Fill = FillFor(node.Category);
            ApplyStroke(shape, node, false);
            grid.Children.Add(shape);
            _shapes[node] = shape;

            double side = node.IsObject ? 16 : 10;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(node.HasWarnings ? side + 8 : side, 2, node.HasWarnings ? side + 12 : side, 2) };
            grid.Children.Add(text);

            string title = node.Title ?? string.Empty;
            if (!node.IsObject && !node.IsRoot)
                title = string.Format(CultureInfo.InvariantCulture, "({0}) {1}", node.NodeId, title);

            text.Children.Add(TextLine(title, FontWeights.Bold, 12, NodeText));

            if (node.IsObject)
            {
                var sub = node.Category == PlanNodeCategory.Index
                    ? (string.IsNullOrEmpty(node.IndexKind) ? "index" : node.IndexKind.ToLowerInvariant() + " index")
                    : PlanNode.Clean(node.ObjectSchema);
                text.Children.Add(TextLine(sub, FontWeights.Normal, 10.5, NodeSubText));
            }
            else
            {
                string cost = node.IsRoot
                    ? "cost " + FormatNumber(node.SubtreeCost)
                    : string.Format(CultureInfo.CurrentCulture, "cost {0}  ({1:0.#}%)", FormatNumber(node.SubtreeCost), node.CostPercent);
                text.Children.Add(TextLine(cost, FontWeights.Normal, 11, NodeText));

                string rows = node.HasActual
                    ? string.Format(CultureInfo.CurrentCulture, "rows {0} → {1}", FormatCount(node.EstimateRowsAllExecutions), FormatCount(node.ActualRows))
                    : "rows " + FormatCount(node.EstimateRowsAllExecutions);
                text.Children.Add(TextLine(rows, FontWeights.Normal, 11, node.BadRowEstimate ? WarningBrush : NodeSubText));
            }

            if (node.HasWarnings)
            {
                var warning = new TextBlock
                {
                    Text = "⚠",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = WarningBrush,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, node.IsRoot ? 24 : 8, 0),
                };
                grid.Children.Add(warning);
            }

            grid.ToolTip = BuildToolTip(node);
            ToolTipService.SetInitialShowDelay(grid, 400);
            ToolTipService.SetShowDuration(grid, 60000);

            grid.MouseLeftButtonDown += (s, e) =>
            {
                Select(node, false);
                e.Handled = true;
            };

            return grid;
        }

        private static object BuildToolTip(PlanNode node)
        {
            var panel = new StackPanel { MaxWidth = 520 };
            panel.Children.Add(new TextBlock { Text = node.IsObject ? node.Title : node.PhysicalOp, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            foreach (var p in node.GetProperties().Take(18))
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
                tb.Inlines.Add(new System.Windows.Documents.Run(p.Name + ": ") { FontWeight = FontWeights.SemiBold });
                var value = p.Value.Length > 300 ? p.Value.Substring(0, 300) + "..." : p.Value;
                tb.Inlines.Add(new System.Windows.Documents.Run(value));
                panel.Children.Add(tb);
            }
            return panel;
        }

        private static TextBlock TextLine(string text, FontWeight weight, double size, Brush foreground)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = weight,
                FontSize = size,
                Foreground = foreground,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        private static Shape CreateShape(PlanNodeCategory category, double w, double h)
        {
            switch (category)
            {
                case PlanNodeCategory.Root:
                    return new Ellipse { Width = w, Height = h };
                case PlanNodeCategory.Table:
                    return new Rectangle { Width = w, Height = h };
                case PlanNodeCategory.Index:
                    return new Polygon
                    {
                        Points = new PointCollection { new Point(0, h / 2), new Point(14, 0), new Point(w - 14, 0), new Point(w, h / 2), new Point(w - 14, h), new Point(14, h) },
                    };
                default:
                    const double c = 10;
                    return new Polygon
                    {
                        Points = new PointCollection
                        {
                            new Point(c, 0), new Point(w - c, 0), new Point(w, c), new Point(w, h - c),
                            new Point(w - c, h), new Point(c, h), new Point(0, h - c), new Point(0, c),
                        },
                    };
            }
        }

        /// <summary>
        /// Orthogonal edge from the top of the child to its port on the bottom of the parent, with a separate
        /// arrow head (data flows up into the parent).
        /// </summary>
        private UIElement CreateEdge(PlanEdge edge, double px, int level)
        {
            double thickness = PlanLayout.EdgeThickness(edge.Rows);
            double py = edge.Parent.Bottom;
            double cx = edge.Child.CenterX;
            double cy = edge.Child.Y;

            double arrowHeight = 6 + thickness;
            double arrowHalfWidth = 3.5 + thickness * 0.8;
            double top = py + arrowHeight;

            // Horizontal run between the arrow and the child, moved up 5 px per level.
            double run = top + (cy - top) / 2 - level * 5;
            run = Math.Max(top + thickness + 2, Math.Min(cy - thickness - 2, run));

            var line = new PathFigure { StartPoint = new Point(cx, cy) };
            if (Math.Abs(cx - px) < 0.5)
            {
                line.Segments.Add(new LineSegment(new Point(px, top), true));
            }
            else
            {
                line.Segments.Add(new LineSegment(new Point(cx, run), true));
                line.Segments.Add(new LineSegment(new Point(px, run), true));
                line.Segments.Add(new LineSegment(new Point(px, top), true));
            }

            var arrow = new PathFigure { StartPoint = new Point(px, py), IsFilled = true, IsClosed = true };
            arrow.Segments.Add(new LineSegment(new Point(px - arrowHalfWidth, top + 0.5), true));
            arrow.Segments.Add(new LineSegment(new Point(px + arrowHalfWidth, top + 0.5), true));

            var group = new Canvas { IsHitTestVisible = true, Opacity = 0.8 };
            var linePath = new Path
            {
                Data = new PathGeometry(new[] { line }),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Miter,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
                SnapsToDevicePixels = true,
            };
            linePath.SetResourceReference(Shape.StrokeProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);

            var arrowPath = new Path { Data = new PathGeometry(new[] { arrow }), SnapsToDevicePixels = true };
            arrowPath.SetResourceReference(Shape.FillProperty, Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);

            group.Children.Add(linePath);
            group.Children.Add(arrowPath);

            if (edge.Rows >= 0)
                group.ToolTip = string.Format(CultureInfo.CurrentCulture, "{0} rows", FormatCount(edge.Rows));

            return group;
        }

        public static string FormatNumber(double value)
        {
            if (value >= 1000)
                return value.ToString("N0", CultureInfo.CurrentCulture);
            if (value >= 1)
                return value.ToString("0.###", CultureInfo.CurrentCulture);
            return value.ToString("0.#####", CultureInfo.CurrentCulture);
        }

        public static string FormatCount(double value)
        {
            if (value >= 10_000_000)
                return (value / 1_000_000).ToString("0.#", CultureInfo.CurrentCulture) + "M";
            if (value >= 100_000)
                return (value / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "K";
            return Math.Round(value, value < 10 ? 2 : 0).ToString("#,0.##", CultureInfo.CurrentCulture);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
