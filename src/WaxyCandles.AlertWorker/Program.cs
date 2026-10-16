using WaxyCandles.AlertWorker;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

builder.AddWaxyCandlesTelemetry("alert-worker");

builder.Services.AddInfrastructure(
    builder.Configuration);

builder.Services.AddHostedService<StockPriceConsumer>();

var host = builder.Build();

host.Run();