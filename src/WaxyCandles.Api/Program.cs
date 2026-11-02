using WaxyCandles.Infrastructure;
using WaxyCandles.Api.Middleware;
using Microsoft.OpenApi.Models;
using System.Text.Json.Serialization;
using WaxyCandles.Api.Hubs;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Infrastructure.Telemetry;
using OpenTelemetry.Trace;
using WaxyCandles.Api.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddWaxyCandlesTelemetry(
    "api",
    tracing => tracing.AddAspNetCoreInstrumentation());

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiAuthentication(builder.Configuration);
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter());
    });

// Live notifications: the hub, the IRealtimeNotifier that pushes through it, and
// the consumer that turns AlertTriggered messages into stored, pushed
// notifications. It runs here because this is where clients are connected.
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter());
    });
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();
builder.Services.AddHostedService<AlertTriggeredConsumer>();

// The web app's origin(s). Required: with cookie auth the browser only sends
// credentials cross-origin to an exact allowed origin. Development gets the
// Vite dev server from launchSettings.json; containers get Cors__AllowedOrigins__0.
const string allowedOriginsKey = "Cors:AllowedOrigins";

var allowedOrigins = (builder.Configuration
        .GetSection(allowedOriginsKey)
        .Get<string[]>() ?? [])
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .Select(x => x.Trim())
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddApiRateLimiting(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition(
        "Bearer",
        new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "Enter 'Bearer' followed by your JWT token."
        });


    options.AddSecurityRequirement(
        new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference =
                        new OpenApiReference
                        {
                            Type =
                                ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                },
                Array.Empty<string>()
            }
        });
});

var app = builder.Build();

// Checked after Build(): EF's design-time tooling (the Dockerfile's migrations
// bundle) starts this host only up to Build() and has no CORS settings.
if (allowedOrigins.Length == 0)
{
    throw new InvalidOperationException(
        $"{allowedOriginsKey} is not set. Set it to the web app's origin, e.g. " +
        "Cors__AllowedOrigins__0=http://localhost:5173.");
}

if (allowedOrigins.Contains("*"))
{
    throw new InvalidOperationException(
        $"{allowedOriginsKey} can't be \"*\": credentialed CORS needs exact origins.");
}

app.UseMiddleware<ExceptionHandlingMiddleware>();

// The API description is for local development only.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Recommended order: CORS after routing and before auth (preflights are
// answered without credentials), and the rate limiter after auth so its
// per-user partitions can see the signed-in user, and after routing so
// [EnableRateLimiting] on endpoints applies.
app.UseRouting();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<NotificationsHub>(NotificationsHub.Path);

app.Run();