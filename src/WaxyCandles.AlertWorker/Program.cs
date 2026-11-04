using WaxyCandles.AlertWorker;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Health;
using WaxyCandles.Infrastructure.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

builder.AddWaxyCandlesTelemetry("alert-worker");

builder.Services.AddInfrastructure(
    builder.Configuration);

builder.Services.AddHostedService<StockPriceConsumer>();

// Liveness: a heartbeat file, kept fresh while the consumer loop runs and no
// delivery has been processing for longer than Health:ConsumerMaxProcessingTime.
builder.AddWorkerHealth("alert-worker")
    .AddConsumerStallCheck();

var host = builder.Build();

host.Run();