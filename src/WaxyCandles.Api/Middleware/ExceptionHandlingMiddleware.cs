using System.Globalization;
using System.Net;
using System.Text.Json;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.MarketData.Exceptions;

namespace WaxyCandles.Api.Middleware;

public class ExceptionHandlingMiddleware
{
    // nginx's "client closed request". Never sent (the client is gone); it
    // only keeps the request log from showing a 500 for a disconnect.
    public const int ClientClosedRequest = 499;

    public const string MarketDataTimeoutMessage =
        "The market data provider timed out. Try again shortly.";

    public const string MarketDataRateLimitedMessage =
        "The market data provider is unavailable: its request limit has been reached. Try again later.";

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
        // The caller disconnected (RequestAborted cancelled the vendor call or
        // a query). Checked first, so a disconnect is never reported as a
        // vendor timeout (504) or logged as an error.
        catch (Exception ex) when (
            context.RequestAborted.IsCancellationRequested &&
            ex is OperationCanceledException or MarketDataUnavailableException)
        {
            _logger.LogDebug("Request aborted by the client.");

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
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

                // A vendor HttpClient's timeout. The typed exception comes from
                // the providers; a bare TaskCanceledException with an inner
                // TimeoutException is any other HttpClient timeout.
                MarketDataUnavailableException { Reason: MarketDataUnavailableReason.Timeout }
                    or TaskCanceledException { InnerException: TimeoutException } =>
                    (HttpStatusCode.GatewayTimeout, MarketDataTimeoutMessage),

                // The vendor's limit (e.g. Market Data's daily credits), not
                // ours: the API's own limiter answers 429 before this runs.
                MarketDataUnavailableException { Reason: MarketDataUnavailableReason.RateLimited } =>
                    (HttpStatusCode.ServiceUnavailable, MarketDataRateLimitedMessage),

                _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred.")
            };

            if (statusCode == HttpStatusCode.InternalServerError)
            {
                _logger.LogError(ex, ex.Message);
            }
            else if (statusCode is HttpStatusCode.GatewayTimeout or HttpStatusCode.ServiceUnavailable)
            {
                // Our own fixed messages; vendor URLs and bodies are never in them.
                _logger.LogWarning(
                    "Request failed with {StatusCode}: {Message}",
                    (int)statusCode,
                    ex is MarketDataUnavailableException ? ex.Message : "HTTP request timed out.");
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

            if (ex is MarketDataUnavailableException { RetryAfter: { } retryAfter })
            {
                context.Response.Headers.RetryAfter =
                    ((long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }


            // Validation failures also say which fields were wrong.
            object response = ex is RequestValidationException validation
                ? new { message, errors = validation.Errors }
                : new { message };


            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response));
        }
    }
}