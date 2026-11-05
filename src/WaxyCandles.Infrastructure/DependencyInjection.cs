using WaxyCandles.Application.Backtesting;
using WaxyCandles.Infrastructure.Backtesting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using WaxyCandles.Infrastructure.Authentication;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Infrastructure.MarketData.Providers;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.MarketData.Calendars;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Watchlists;
using WaxyCandles.Application.Watchlists.Interfaces;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Infrastructure.Options;
using WaxyCandles.Infrastructure.Resilience;

namespace WaxyCandles.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Read now, so a bad value stops startup naming the key.
        var retry = DatabaseRetrySettings.From(configuration);
        var timeouts = HttpClientTimeouts.From(configuration);

        services.AddDbContext<AppDbContext>(options =>
        {
            // Transient SQL errors (deadlocks, dropped connections, failover)
            // are retried in-process. So every explicit transaction must run
            // inside Database.CreateExecutionStrategy().ExecuteAsync, with a
            // delegate that is safe to run again from the start.
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql => sql.EnableRetryOnFailure(
                    retry.MaxRetryCount,
                    retry.MaxRetryDelay,
                    errorNumbersToAdd: null));
        });

        services.AddScoped<IMarketDataService, MarketDataService>();
        services.AddScoped<IAlertEvaluationService, AlertEvaluationService>();
        services.AddSingleton<RabbitMqConnection>();
        services.AddScoped<IHistoricalMarketDataService, HistoricalMarketDataService>();
        services.AddScoped<IWatchlistService, WatchlistService>();
        services.AddScoped<INotificationService, NotificationService>();

        services.AddMarketDataProvider(configuration, timeouts);

        // Market Data is the only options vendor so far; it needs
        // MarketDataApp:ApiToken only when a chain is requested. Vendor clients
        // get a timeout and nothing more: no retries (see HttpClientTimeouts).
        services.AddHttpClient<
            IOptionsDataProvider,
            MarketDataAppOptionsDataProvider>(
            MarketDataAppApi.OptionsHttpClientName,
            client => client.Timeout = timeouts.MarketDataAppOptions);
        services.AddMemoryCache();
        services.Configure<GexOptions>(
            configuration.GetSection(GexOptions.SectionName));
        services.AddScoped<IGexService, GexService>();
        services.AddScoped<IBacktestService, BacktestService>();
        services.AddScoped<IGexLevelBoardSource, GexLevelBoardSource>();
        services.AddScoped<IGexLevelAlertRefresher, GexLevelAlertRefresher>();

        return services;
    }

    public const string AlphaVantageHttpClientName = "AlphaVantage";

    // MarketData:Provider picks the vendor; unset means Alpha Vantage, which
    // keeps workers that never fetch market data free of provider config.
    private static IServiceCollection AddMarketDataProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        HttpClientTimeouts timeouts)
    {
        var provider = configuration["MarketData:Provider"];

        services.TryAddSingleton(TimeProvider.System);

        switch (provider)
        {
            case null or "" or "AlphaVantage":
                services.AddHttpClient<
                    IMarketDataProvider,
                    AlphaVantageMarketDataProvider>(
                    AlphaVantageHttpClientName,
                    client => client.Timeout = timeouts.AlphaVantage);
                services.AddSingleton<
                    IMarketCalendar,
                    WeekdayMarketCalendar>();
                break;

            case "MarketDataApp":
                services.AddHttpClient<
                    IMarketDataProvider,
                    MarketDataAppMarketDataProvider>(
                    MarketDataAppApi.HttpClientName,
                    client => client.Timeout = timeouts.MarketDataApp);
                // Singleton: its cache must outlive each poll. It takes
                // IHttpClientFactory rather than a long-lived HttpClient, and
                // creates the same named client, so it gets the same timeout.
                services.AddSingleton<
                    IMarketCalendar,
                    MarketDataAppMarketCalendar>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown MarketData:Provider \"{provider}\". " +
                    "Expected \"AlphaVantage\" or \"MarketDataApp\".");
        }

        return services;
    }

    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = true;

                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 8;

                // Brute-force protection for login (AuthService counts
                // failures). Short on purpose: anyone who knows an email can
                // trigger a lockout, so a long one would let them keep a user
                // out. The per-IP rate limit on the auth endpoints does the rest.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        var jwt = configuration.GetSection("Jwt");

        // Read now, so a missing or weak secret stops startup with a clear
        // message instead of failing on the first request.
        var signingKey = JwtSigningKey.From(configuration);

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme =
                    JwtBearerDefaults.AuthenticationScheme;

                options.DefaultChallengeScheme =
                    JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters =
                    new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
            
                        ValidIssuer = jwt["Issuer"],
                        ValidAudience = jwt["Audience"],
            
                        IssuerSigningKey = signingKey
                    };
            
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Cookies.TryGetValue(
                                "waxy_access_token",
                                out var accessToken))
                        {
                            context.Token = accessToken;
                        }
            
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAlertService, AlertService>();

        return services;
    }
}