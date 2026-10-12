using System.Net;
using System.Text.Json;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Application.Common.Exceptions;

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
            // Known client errors return their own (user-facing) message;
            // anything else is a generic 500 that doesn't leak details.
            var (statusCode, message) = ex switch
            {
                InvalidCredentialsException or InvalidRefreshTokenException =>
                    (HttpStatusCode.Unauthorized, ex.Message),

                UserAlreadyExistsException =>
                    (HttpStatusCode.Conflict, ex.Message),

                RegistrationFailedException or RequestValidationException =>
                    (HttpStatusCode.BadRequest, ex.Message),

                _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred.")
            };

            if (statusCode == HttpStatusCode.InternalServerError)
            {
                _logger.LogError(ex, ex.Message);
            }
            else
            {
                _logger.LogInformation(
                    "Request failed with {StatusCode}: {Message}",
                    (int)statusCode,
                    ex.Message);
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)statusCode;


            // Validation failures also say which fields were wrong.
            object response = ex is RequestValidationException validation
                ? new { message, errors = validation.Errors }
                : new { message };


            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response));
        }
    }
}