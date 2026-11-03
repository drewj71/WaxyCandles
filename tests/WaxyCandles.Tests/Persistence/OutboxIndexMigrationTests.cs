using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Persistence;

/// <summary>
/// The OutboxIndexesAndRetention migration, checked against the schema the
/// real migrations produce on SQL Server.
/// </summary>
public class OutboxIndexMigrationTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task Outbox_type_is_nvarchar_200()
    {
        var (typeName, maxLengthBytes) = await QuerySingleAsync(
            """
            SELECT t.name, c.max_length
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.OutboxMessages') AND c.name = 'Type'
            """,
            r => (r.GetString(0), (int)r.GetInt16(1)));

        Assert.Equal("nvarchar", typeName);
        Assert.Equal(400, maxLengthBytes); // 200 characters, 2 bytes each
    }

    [Fact]
    public async Task Pending_outbox_index_is_on_created_at_filtered_to_pending_and_includes_type()
    {
        var filter = await IndexFilterAsync("OutboxMessages", "IX_OutboxMessages_Pending");
        Assert.Equal("([ProcessedAt] IS NULL)", filter);

        var columns = await IndexColumnsAsync("OutboxMessages", "IX_OutboxMessages_Pending");
        Assert.Equal([("CreatedAt", false), ("Type", true)], columns);
    }

    [Fact]
    public async Task Processed_outbox_index_is_on_processed_at_filtered_to_processed()
    {
        var filter = await IndexFilterAsync("OutboxMessages", "IX_OutboxMessages_Processed");
        Assert.Equal("([ProcessedAt] IS NOT NULL)", filter);

        var columns = await IndexColumnsAsync("OutboxMessages", "IX_OutboxMessages_Processed");
        Assert.Equal([("ProcessedAt", false)], columns);
    }

    [Fact]
    public async Task Processed_messages_are_indexed_by_processed_at()
    {
        var filter = await IndexFilterAsync("ProcessedMessages", "IX_ProcessedMessages_ProcessedAt");
        Assert.Null(filter);

        var columns = await IndexColumnsAsync("ProcessedMessages", "IX_ProcessedMessages_ProcessedAt");
        Assert.Equal([("ProcessedAt", false)], columns);
    }

    [Fact]
    public async Task Publisher_queries_use_the_pending_index()
    {
        // Mostly processed rows, as in production, so a scan is clearly worse.
        var t0 = new DateTimeOffset(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

        await using (var db = NewDbContext())
        {
            for (var i = 0; i < 2000; i++)
            {
                db.OutboxMessages.Add(Outbox(t0.AddSeconds(i), processedAt: t0.AddSeconds(i + 1)));
            }

            for (var i = 0; i < 5; i++)
            {
                db.OutboxMessages.Add(Outbox(t0.AddDays(1).AddSeconds(i), processedAt: null));
            }

            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE STATISTICS [OutboxMessages] WITH FULLSCAN");
        }

        // The same queries OutboxPublisherWorker.PublishBatchAsync builds.
        await using var queryDb = NewDbContext();
        var routedTypes = MessageRoutes.ByMessageType.Keys.ToList();

        var batchSql = queryDb.OutboxMessages
            .Where(x => x.ProcessedAt == null && routedTypes.Contains(x.Type))
            .OrderBy(x => x.CreatedAt)
            .Take(20)
            .ToQueryString();

        var countSql = queryDb.OutboxMessages
            .Where(x => x.ProcessedAt == null && !routedTypes.Contains(x.Type))
            .Select(x => x.Id)
            .ToQueryString()
            .Replace("SELECT [o].[Id]", "SELECT COUNT(*)");

        Assert.Contains("[IX_OutboxMessages_Pending]", await EstimatedPlanAsync(batchSql));
        Assert.Contains("[IX_OutboxMessages_Pending]", await EstimatedPlanAsync(countSql));
    }

    private static OutboxMessage Outbox(DateTimeOffset createdAt, DateTimeOffset? processedAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = StockPriceUpdatedMessage.MessageType,
        Payload = "{}",
        CreatedAt = createdAt,
        ProcessedAt = processedAt
    };

    private async Task<string> EstimatedPlanAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var on = new SqlCommand("SET SHOWPLAN_XML ON", connection))
        {
            await on.ExecuteNonQueryAsync();
        }

        var plans = new List<string>();

        await using (var command = new SqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync())
                {
                    plans.Add(reader.GetString(0));
                }
            }
            while (await reader.NextResultAsync());
        }

        return string.Join(Environment.NewLine, plans);
    }

    private Task<string?> IndexFilterAsync(string table, string index) =>
        QuerySingleAsync(
            $"""
            SELECT filter_definition FROM sys.indexes
            WHERE object_id = OBJECT_ID('dbo.{table}') AND name = '{index}'
            """,
            r => r.IsDBNull(0) ? null : r.GetString(0));

    private async Task<List<(string Column, bool Included)>> IndexColumnsAsync(string table, string index)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            $"""
            SELECT c.name, ic.is_included_column
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.{table}') AND i.name = '{index}'
            ORDER BY ic.is_included_column, ic.key_ordinal, c.name
            """,
            connection);

        var columns = new List<(string, bool)>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            columns.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        Assert.NotEmpty(columns); // the index exists
        return columns;
    }

    private async Task<T> QuerySingleAsync<T>(string sql, Func<SqlDataReader, T> read)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"No row for: {sql}");
        var value = read(reader);
        Assert.False(await reader.ReadAsync(), $"More than one row for: {sql}");

        return value;
    }
}
