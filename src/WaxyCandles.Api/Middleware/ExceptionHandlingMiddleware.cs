using System.Net;
using System.Text.Json;

namespace WaxyCandles.Api.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }


    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode =
                (int)HttpStatusCode.InternalServerError;


            var response = new
            {
                message = "An unexpected error occurred."
            };


            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response));
        }
    }
}