using System.Text.Json;

using Microsoft.AspNetCore.Diagnostics;

namespace JMW.Discovery.Server.Infrastructure;

public static partial class ErrorEnvelopeMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task HandleAsync(HttpContext context, ILogger logger)
    {
        IExceptionHandlerFeature? exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        if (exceptionFeature is not null)
        {
            Log.UnhandledException(logger, context.Request.Method, context.Request.Path, exceptionFeature.Error);
        }

        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        var body = new
        {
            error = new
            {
                code = "internal_error",
                message = "An unexpected error occurred.",
            },
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
        internal static partial void UnhandledException(ILogger logger, string method, string path, Exception ex);
    }
}