using System;

namespace SSMSPlusCore.Ui.Utils
{
    public delegate void ErrorHandler(Exception ex);

    public interface IErrorHandler
    {
        void HandleError(Exception ex);
    }
}
