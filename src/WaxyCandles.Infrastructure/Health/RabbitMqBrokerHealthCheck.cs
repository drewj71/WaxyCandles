using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace WaxyCandles.Infrastructure.Health;

/// <summary>
/// Whether the RabbitMQ broker accepts a connection, by opening (and closing) a
/// short-lived one of its own.
/// <para>
/// It deliberately doesn't resolve the app's <c>RabbitMqConnection</c>
/// singleton: that connects synchronously in its constructor with the client's
/// ~30 s timeout and isn't cached by DI when it throws, so every probe during an
/// outage would block a thread for the full timeout. It reads the same
/// <c>RabbitMQ:*</c> settings as <c>RabbitMqConnection</c>; keep the two in step.
/// </para>
/// </summary>
public sealed class RabbitMqBrokerHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <summary>Connect timeout for the probe connection.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await CreateFactory().CreateConnectionAsync(
                "waxycandles-health-check",
                cancellationToken);

            return connection.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ broker is reachable.")
                : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ connection did not open.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The description is shown on an anonymous endpoint, so it names
            // only the failure type; HealthCheckService logs the exception.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                $"RabbitMQ broker is unreachable ({ex.GetType().Name}).",
                ex);
        }
    }

    private ConnectionFactory CreateFactory()
    {
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMQ:Host"] ?? "localhost",
            Port = configuration.GetValue<int?>("RabbitMQ:Port") ?? AmqpTcpEndpoint.UseDefaultPort,
            RequestedConnectionTimeout = ConnectTimeout,
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false
        };

        // Same optional overrides as RabbitMqConnection; the password is a
        // secret and is never logged or reported.
        if (configuration["RabbitMQ:Username"] is { Length: > 0 } username)
        {
            factory.UserName = username;
        }

        if (configuration["RabbitMQ:Password"] is { Length: > 0 } password)
        {
            factory.Password = password;
        }

        if (configuration["RabbitMQ:VirtualHost"] is { Length: > 0 } virtualHost)
        {
            factory.VirtualHost = virtualHost;
        }

        return factory;
    }
}
