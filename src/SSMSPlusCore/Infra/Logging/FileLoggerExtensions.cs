using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace SSMSPlusCore.Infra.Logging
{
    static public class FileLoggerExtensions
    {

        static public ILoggingBuilder AddFileLogger(this ILoggingBuilder builder, Func<FileLoggerOptions> configure)
        {
            builder.Services.AddSingleton<ILoggerProvider, FileCSVLoggerProvider>(services => new FileCSVLoggerProvider(configure()));

           // builder.AddFilter<FileLoggerProvider>(null, LogLevel.Trace);

            return builder;
        }
    }
}
