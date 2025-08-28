using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace RedMist.Backend.Shared.Utilities;

public static class LoggerExtensions
{
    public static void LogMethodEntry(this ILogger logger, [CallerMemberName] string methodName = "")
    {
        logger.LogInformation("{MethodName} called", methodName);
    }

    public static void LogMethodInfo(this ILogger logger, string message, [CallerMemberName] string methodName = "")
    {
        logger.LogInformation("{MethodName}: {Message}", methodName, message);
    }
}
