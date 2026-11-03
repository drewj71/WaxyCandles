using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Retention;
using WaxyCandles.Infrastructure.Telemetry;
using WaxyCandles.OutboxWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddWaxyCandlesTelemetry("outbox-worker");

builder.Services.AddInfrastructure(
    builder.Configuration);

builder.Services.AddHostedService<OutboxPublisherWorker>();

// Deletes processed outbox rows, old idempotency claims and old prices.
builder.Services.AddDataRetention(builder.Configuration);
builder.Services.AddHostedService<DataRetentionWorker>();

var host = builder.Build();

host.Run();