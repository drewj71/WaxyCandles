using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <summary>
    /// Daily candles used to be stored as midnight in the importing machine's
    /// local time zone. They are now stored as UTC midnight of the trading date.
    /// Rewrites existing daily rows to match, so re-imports dedup against them.
    /// Intraday candles are left as they are.
    /// </summary>
    public partial class NormalizeDailyCandleTimestampsToUtc : Migration
    {
        // CandleInterval.OneDay. Hardcoded so this migration keeps its meaning
        // even if the enum changes later.
        private const int OneDay = 4;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CAST(datetimeoffset AS date) takes the date in the stored offset,
            // i.e. the trading date the row was originally parsed from.

            // The same trading day may already exist more than once (imported
            // from machines in different time zones, or re-imported as UTC).
            // Keep one row per day, preferring one already at UTC, so the update
            // below can't violate the (Symbol, Interval, Timestamp) unique index.
            migrationBuilder.Sql($"""
                WITH Ranked AS (
                    SELECT ROW_NUMBER() OVER (
                        PARTITION BY Symbol, CAST(Timestamp AS date)
                        ORDER BY
                            CASE WHEN DATEPART(TZOFFSET, Timestamp) = 0 THEN 0 ELSE 1 END,
                            Id) AS RowNumber
                    FROM StockCandles
                    WHERE Interval = {OneDay}
                )
                DELETE FROM Ranked
                WHERE RowNumber > 1;
                """);

            migrationBuilder.Sql($"""
                UPDATE StockCandles
                SET Timestamp = TODATETIMEOFFSET(
                    CAST(CAST(Timestamp AS date) AS datetime2), 0)
                WHERE Interval = {OneDay}
                    AND Timestamp <> TODATETIMEOFFSET(
                        CAST(CAST(Timestamp AS date) AS datetime2), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: the original values depended on whichever machine
            // imported them, and the duplicates removed in Up are gone. UTC
            // midnight remains a valid value for the previous code to read.
        }
    }
}
