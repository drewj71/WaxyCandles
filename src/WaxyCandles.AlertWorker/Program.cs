using WaxyCandles.AlertWorker;
using WaxyCandles.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(
    builder.Configuration);

builder.Services.AddHostedService<StockPriceConsumer>();

var host = builder.Build();

host.Run();