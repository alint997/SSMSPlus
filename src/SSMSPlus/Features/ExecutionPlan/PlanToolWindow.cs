namespace SSMSPlus.Features.ExecutionPlan
{
    using Microsoft.VisualStudio.Shell;
    using System.Runtime.InteropServices;

    /// <summary>Document-style tool window hosting the DB2-style execution plan viewer.</summary>
    [Guid("5d0b8f43-2c7e-4f0a-9a57-3b9c6f1e2a71")]
    public sealed class PlanToolWindow : ToolWindowPane
    {
        public PlanToolWindow()
            : base(null)
        {
            Caption = "Execution Plan Tree";
            Content = new PlanViewerControl();
        }

        internal PlanViewerControl Viewer => (PlanViewerControl)Content;
    }
}
