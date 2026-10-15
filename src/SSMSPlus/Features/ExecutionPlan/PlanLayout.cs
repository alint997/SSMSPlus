namespace SSMSPlus.Features.ExecutionPlan
{
    using System;
    using System.Collections.Generic;

    public sealed class PlanNodeBox
    {
        public PlanNode Node { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double CenterX => X + Width / 2;
        public double Bottom => Y + Height;
    }

    public sealed class PlanEdge
    {
        public PlanNodeBox Parent { get; set; }
        public PlanNodeBox Child { get; set; }

        /// <summary>Rows flowing from the child to the parent (actual when available).</summary>
        public double Rows { get; set; }
    }

    /// <summary>
    /// Top-down tree layout (root at the top, tables at the bottom), like DB2 Visual Explain:
    /// leaves are placed left to right and every parent is centered above its children.
    /// </summary>
    public sealed class PlanLayout
    {
        public const double OperatorWidth = 170;
        public const double OperatorHeight = 62;
        public const double ObjectWidth = 150;
        public const double ObjectHeight = 44;
        public const double HorizontalGap = 18;
        public const double VerticalGap = 34;
        public const double Margin = 20;

        public List<PlanNodeBox> Nodes { get; } = new List<PlanNodeBox>();
        public List<PlanEdge> Edges { get; } = new List<PlanEdge>();
        public double Width { get; private set; }
        public double Height { get; private set; }

        public static PlanLayout Compute(PlanNode root)
        {
            var layout = new PlanLayout();
            if (root == null)
                return layout;

            double nextX = Margin;
            layout.Place(root, 0, ref nextX);
            layout.Width = Math.Max(layout.Width, nextX - HorizontalGap + Margin);
            return layout;
        }

        private PlanNodeBox Place(PlanNode node, int depth, ref double nextX)
        {
            var box = new PlanNodeBox
            {
                Node = node,
                Width = node.IsObject ? ObjectWidth : OperatorWidth,
                Height = node.IsObject ? ObjectHeight : OperatorHeight,
                Y = Margin + depth * (OperatorHeight + VerticalGap),
            };
            Nodes.Add(box);

            if (node.Children.Count == 0)
            {
                box.X = nextX;
                nextX += box.Width + HorizontalGap;
            }
            else
            {
                double subtreeStart = nextX;
                int firstIndex = Nodes.Count;
                var children = new List<PlanNodeBox>();
                foreach (var child in node.Children)
                    children.Add(Place(child, depth + 1, ref nextX));

                double center = (children[0].CenterX + children[children.Count - 1].CenterX) / 2;
                box.X = center - box.Width / 2;

                // A parent wider than its children must not overlap the previous subtree:
                // shift the whole subtree to the right.
                if (box.X < subtreeStart)
                {
                    double shift = subtreeStart - box.X;
                    box.X += shift;
                    for (int i = firstIndex; i < Nodes.Count; i++)
                        Nodes[i].X += shift;
                    nextX += shift;
                }

                if (nextX < box.X + box.Width + HorizontalGap)
                    nextX = box.X + box.Width + HorizontalGap;

                foreach (var c in children)
                {
                    Edges.Add(new PlanEdge
                    {
                        Parent = box,
                        Child = c,
                        Rows = c.Node.IsObject ? -1 : (c.Node.HasActual ? c.Node.ActualRows : c.Node.EstimateRowsAllExecutions),
                    });
                }
            }

            if (box.Y + box.Height + Margin > Height)
                Height = box.Y + box.Height + Margin;
            if (box.X + box.Width + Margin > Width)
                Width = box.X + box.Width + Margin;
            return box;
        }

        /// <summary>Edge thickness from the number of rows: 1 for few rows, up to 7 for many.</summary>
        public static double EdgeThickness(double rows)
        {
            if (rows < 0)
                return 1;
            return 1 + Math.Min(6, Math.Log10(Math.Max(1, rows)));
        }
    }
}
