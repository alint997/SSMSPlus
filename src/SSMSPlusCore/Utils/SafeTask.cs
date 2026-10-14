using System;
using System.Threading.Tasks;

namespace SSMSPlusCore.Utils
{
    public class SafeTask
    {
        public static Task Run(Func<Task> function, Action<Exception> ErrorHandler)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await function();
                }
                catch (Exception ex)
                {
                    ErrorHandler(ex);
                }
            });
        }

        public static async Task RunSafe(Func<Task> function, Action<Exception> ErrorHandler)
        {
            try
            {
                await function();
            }
            catch (Exception ex)
            {
                ErrorHandler(ex);
            }

        }
    }
}
