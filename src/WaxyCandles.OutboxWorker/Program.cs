using WaxyCandles.Infrastructure;
using WaxyCandles.OutboxWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(
    builder.Configuration);

builder.Services.AddHostedService<OutboxPublisherWorker>();

var host = builder.Build();

host.Run();