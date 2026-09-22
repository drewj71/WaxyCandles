using WaxyCandles.Infrastructure;
using WaxyCandles.PricePollingWorker;


var builder =
    Host.CreateApplicationBuilder(args);


builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    .AddHostedService<PricePollingWorker>();


var host = builder.Build();

host.Run();