using System;
using System.Threading.Tasks;

namespace SSMSPlusCore.Ui.Utils
{
    public static class TaskExtentions
    {
        public static async void FireAndForgetSafeAsync(this Task task, ErrorHandler handler = null)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                handler?.Invoke(ex);
            }
        }
    }
}
