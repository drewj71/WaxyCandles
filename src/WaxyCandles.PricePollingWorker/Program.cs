using WaxyCandles.Infrastructure;
using WaxyCandles.PricePollingWorker;


var builder =
    Host.CreateApplicationBuilder(args);


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


var host = builder.Build();

host.Run();
