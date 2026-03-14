using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Helpers
{
    public static class TaskExtensions
    {
        public static void FireAndForgetSafe(this Task task, ILogger? logger = null, string? errorContext = null)
        {
            _ = FireAndForgetSafeAsync(task, logger, errorContext);
        }

        private static async Task FireAndForgetSafeAsync(Task task, ILogger? logger, string? errorContext)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.LogError(ex, "Unexpected error in fire-and-forget task: {Context}", errorContext ?? "Unknown Context");
                }
            }
        }
    }
}
