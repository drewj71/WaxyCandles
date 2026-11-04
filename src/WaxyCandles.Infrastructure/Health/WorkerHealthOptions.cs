namespace WaxyCandles.Infrastructure.Health;

/// <summary>"Health" configuration section of a worker. Every value is optional.</summary>
public sealed class WorkerHealthOptions
{
    public const string SectionName = "Health";

    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The liveness file. Default <c>&lt;temp&gt;/waxycandles-&lt;service&gt;.healthy</c>
    /// (<c>/tmp/...</c> in the containers), one per worker so several on one
    /// machine don't share it.
    /// </summary>
    public string HeartbeatFile { get; set; } = "";

    /// <summary>How often the checks run and the file is rewritten (1 s to 5 min).</summary>
    public TimeSpan Period { get; set; } = DefaultPeriod;

    /// <summary>
    /// OutboxWorker only: how long the publish loop may go without completing an
    /// iteration (it normally does one every 5 s). Default 2 minutes.
    /// </summary>
    public TimeSpan OutboxLoopMaxAge { get; set; } = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DefaultConsumerMaxProcessingTime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// AlertWorker only: how long one delivery may stay in processing (the
    /// claim, alert evaluation and commit, with their SQL retries) before the
    /// consumer counts as stuck. Default 5 minutes; more than zero, at most 1 day.
    /// The ack and retry/dead-letter publishes aren't counted, so an idle queue
    /// or a broker outage never trips it.
    /// <para>
    /// It must exceed the worst case of one delivery's SQL retries, or a long
    /// database outage would mark the worker unhealthy instead of the message
    /// being retried through the queue. That worst case is about
    /// (<c>Database:MaxRetryCount</c> + 1) × the SQL command timeout (30 s by
    /// default) + <c>Database:MaxRetryCount</c> × <c>Database:MaxRetryDelay</c>:
    /// about 2.3 minutes with the defaults (3 retries, 5 s), but about 10.5
    /// minutes at their maximums (10 retries, 30 s). Raise this when raising those.
    /// </para>
    /// </summary>
    public TimeSpan ConsumerMaxProcessingTime { get; set; } = DefaultConsumerMaxProcessingTime;

    public static string DefaultHeartbeatFile(string serviceName) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"waxycandles-{serviceName}.healthy");
}
