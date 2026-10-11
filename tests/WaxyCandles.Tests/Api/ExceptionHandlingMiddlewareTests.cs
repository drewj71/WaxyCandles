using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WaxyCandles.Api.Middleware;

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

    private static async Task<DefaultHttpContext> InvokeAsync(RequestDelegate next)
    {
        var context = new DefaultHttpContext();
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
