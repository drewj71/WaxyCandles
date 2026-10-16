using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Telemetry;
using WaxyCandles.OutboxWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddWaxyCandlesTelemetry("outbox-worker");

builder.Services.AddInfrastructure(
    builder.Configuration);

builder.Services.AddHostedService<OutboxPublisherWorker>();

var host = builder.Build();

host.Run();