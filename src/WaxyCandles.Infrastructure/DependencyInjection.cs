using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using WaxyCandles.Infrastructure.Authentication;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Infrastructure.MarketData.Providers;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Watchlists;
using WaxyCandles.Application.Watchlists.Interfaces;

namespace WaxyCandles.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"));
        });

        services.AddScoped<IMarketDataService, MarketDataService>();
        services.AddScoped<IAlertEvaluationService, AlertEvaluationService>();
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();
        services.AddScoped<IHistoricalMarketDataService, HistoricalMarketDataService>();
        services.AddScoped<IWatchlistService, WatchlistService>();

        services.AddHttpClient<
            IMarketDataProvider,
            AlphaVantageMarketDataProvider>();

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
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        var jwt = configuration.GetSection("Jwt");

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
            
                        IssuerSigningKey =
                            new SymmetricSecurityKey(
                                Encoding.UTF8.GetBytes(jwt["Secret"]!))
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