using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WaxyCandles.Api.Middleware;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.MarketData.Exceptions;

namespace WaxyCandles.Tests.Api;

public class ExceptionHandlingMiddlewareTests
{
    [Theory]
    [InlineData(typeof(Exception))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(KeyNotFoundException))]
    public async Task Any_exception_becomes_a_generic_500(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(
            exceptionType,
            "Sensitive detail: connection string or stack info")!;

        var context = await InvokeAsync(_ => throw exception);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        var body = await ReadBodyAsync(context);
        Assert.Equal("An unexpected error occurred.", body.GetProperty("message").GetString());
        Assert.DoesNotContain("Sensitive detail", body.GetRawText());
    }

    public static TheoryData<Exception, int> ClientErrors => new()
    {
        { new InvalidCredentialsException(), StatusCodes.Status401Unauthorized },
        { new InvalidRefreshTokenException("Refresh token expired or revoked."), StatusCodes.Status401Unauthorized },
        { new UserAlreadyExistsException(), StatusCodes.Status409Conflict },
        { new RegistrationFailedException("Passwords do not match."), StatusCodes.Status400BadRequest },
    };

    [Theory]
    [MemberData(nameof(ClientErrors))]
    public async Task Auth_exceptions_map_to_client_errors_with_their_message(
        Exception exception,
        int expectedStatus)
    {
        var context = await InvokeAsync(_ => throw exception);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        var body = await ReadBodyAsync(context);
        Assert.Equal(exception.Message, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Validation_errors_become_400_with_the_field_errors()
    {
        var exception = new RequestValidationException(new Dictionary<string, string[]>
        {
            ["targetPrice"] = ["Target price must be greater than 0."]
        });

        var context = await InvokeAsync(_ => throw exception);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        var body = await ReadBodyAsync(context);
        Assert.Equal(exception.Message, body.GetProperty("message").GetString());
        Assert.Equal(
            "Target price must be greater than 0.",
            body.GetProperty("errors").GetProperty("targetPrice")[0].GetString());
    }

    [Fact]
    public async Task Vendor_timeout_becomes_504_without_retry_after()
    {
        var context = await InvokeAsync(_ => throw new MarketDataUnavailableException(
            MarketDataUnavailableReason.Timeout,
            "Market Data",
            innerException: new TaskCanceledException("t", new TimeoutException())));

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));

        var body = await ReadBodyAsync(context);
        Assert.Equal(
            ExceptionHandlingMiddleware.MarketDataTimeoutMessage,
            body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Bare_http_client_timeout_also_becomes_504()
    {
        var context = await InvokeAsync(_ =>
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing.",
                new TimeoutException()));

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
    }

    [Fact]
    public async Task Vendor_rate_limit_becomes_503_with_retry_after_rounded_up()
    {
        var context = await InvokeAsync(_ => throw new MarketDataUnavailableException(
            MarketDataUnavailableReason.RateLimited,
            "Market Data",
            TimeSpan.FromSeconds(89.2)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("90", context.Response.Headers.RetryAfter.ToString());

        var body = await ReadBodyAsync(context);
        Assert.Equal(
            ExceptionHandlingMiddleware.MarketDataRateLimitedMessage,
            body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Vendor_rate_limit_without_a_hint_has_no_retry_after()
    {
        var context = await InvokeAsync(_ => throw new MarketDataUnavailableException(
            MarketDataUnavailableReason.RateLimited,
            "Alpha Vantage"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
    }

    public static TheoryData<Exception> AbortExceptions => new()
    {
        new TaskCanceledException(),
        new OperationCanceledException(),
        // A timeout racing the disconnect is still a disconnect.
        new TaskCanceledException("t", new TimeoutException()),
        new MarketDataUnavailableException(MarketDataUnavailableReason.Timeout, "Market Data"),
    };

    [Theory]
    [MemberData(nameof(AbortExceptions))]
    public async Task Client_abort_is_not_a_504_and_writes_nothing(Exception exception)
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        var context = await InvokeAsync(_ => throw exception, aborted.Token);

        Assert.Equal(ExceptionHandlingMiddleware.ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task Cancellation_without_a_client_abort_or_timeout_is_still_a_500()
    {
        var context = await InvokeAsync(_ => throw new TaskCanceledException());

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task Successful_requests_pass_through_untouched()
    {
        var context = await InvokeAsync(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            await ctx.Response.WriteAsync("created");
        });

        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        Assert.Equal("created", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static async Task<DefaultHttpContext> InvokeAsync(
        RequestDelegate next,
        CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext { RequestAborted = requestAborted };
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            next,
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        return context;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
