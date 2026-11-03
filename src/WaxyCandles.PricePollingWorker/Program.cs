using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Telemetry;
using WaxyCandles.PricePollingWorker;
using WaxyCandles.Infrastructure.Health;
using WaxyCandles.PricePollingWorker.Health;


var builder =
    Host.CreateApplicationBuilder(args);

builder.AddWaxyCandlesTelemetry("price-polling-worker");


builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    .AddOptions<PricePollingOptions>()
    .Bind(builder.Configuration.GetSection(PricePollingOptions.SectionName))
    .Validate(
        x => x.Interval >= TimeSpan.FromSeconds(5),
        "PricePolling:Interval must be at least 00:00:05.")
    .ValidateOnStart();

builder.Services
    .AddHostedService<PricePollingWorker>();

// Liveness: a heartbeat file, kept fresh while polls succeed (checked only
// while the market is open).
builder.AddWorkerHealth("price-polling-worker")
    .AddPricePollingCheck(builder.Configuration);


var host = builder.Build();

host.Run();
