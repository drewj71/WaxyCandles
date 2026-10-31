<#
.SYNOPSIS
    Injects one price into the docker-compose.app.yml stack, as the price
    polling worker would, so alerts can be tested without a live feed.

.DESCRIPTION
    Writes a StockPrice row and its StockPriceUpdatedMessage outbox row in one
    transaction, stamped with the current time. From there the real pipeline
    runs: outbox worker -> stock-price-updated -> alert worker -> alert-triggered
    -> API -> notification + SignalR push.

    For local testing only. The fake price becomes the symbol's latest stored
    price, so the poller skips real quotes older than it.

.EXAMPLE
    ./scripts/fake-price.ps1 SOFI 16.90
    ./scripts/fake-price.ps1 SOFI 12.00 -DryRun
#>
param(
    [Parameter(Mandatory)] [string] $Symbol,
    [Parameter(Mandatory)] [decimal] $Price,
    # Runs the inserts and rolls them back, to check the script without effect.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$Symbol = $Symbol.Trim().ToUpperInvariant()
if ($Symbol -notmatch '^[A-Z][A-Z.\-]{0,9}$') {
    throw "Not a ticker symbol: '$Symbol'"
}
if ($Price -le 0) {
    throw "Price must be positive."
}

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$priceText = $Price.ToString($inv)
$timestamp = [DateTimeOffset]::UtcNow.ToString('o', $inv)
$messageId = [Guid]::NewGuid()
$payload = "{""MessageId"":""$messageId"",""Symbol"":""$Symbol"",""Price"":$priceText,""Timestamp"":""$timestamp""}"
$end = if ($DryRun) { 'ROLLBACK' } else { 'COMMIT' }

$sql = @"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
INSERT INTO StockPrices (Id, Symbol, Price, Change, ChangePercent, Timestamp)
VALUES (NEWID(), '$Symbol', $priceText, 0, 0, '$timestamp');
INSERT INTO OutboxMessages (Id, Type, Payload, CreatedAt)
VALUES (NEWID(), 'StockPriceUpdatedMessage', '$payload', SYSDATETIMEOFFSET());
$end;
SELECT '$end' AS Result, '$Symbol' AS Symbol, $priceText AS Price, '$timestamp' AS Timestamp;
"@

# Written as a file without a BOM: Windows PowerShell adds one when piping to a
# native program, and sqlcmd reads it as part of the first statement.
$file = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($file, $sql, (New-Object System.Text.UTF8Encoding $false))
    docker cp $file waxycandles-sqlserver:/tmp/fake-price.sql | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "docker cp failed; is the docker-compose.app.yml stack running?"
    }

    # The sa password stays inside the container; it is never read out here.
    docker exec waxycandles-sqlserver sh -c '/opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d WaxyCandles -W -i /tmp/fake-price.sql'
    $status = $LASTEXITCODE

    # docker cp leaves the file owned by root.
    docker exec -u root waxycandles-sqlserver rm -f /tmp/fake-price.sql

    if ($status -ne 0) {
        throw "sqlcmd failed (exit $status)."
    }
}
finally {
    Remove-Item $file -ErrorAction SilentlyContinue
}
