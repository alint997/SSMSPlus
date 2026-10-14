using Microsoft.Extensions.Logging;

namespace SSMSPlusCore.Infra.Logging
{
    public class FileLoggerOptions
    {
        public string Folder { get; set; }

        public int RetainPolicyFileCount { get; set; } = 10;

        public LogLevel LogLevel { get; set; } = LogLevel.Information;
    }
}
