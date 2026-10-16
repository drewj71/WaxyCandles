namespace WaxyCandles.Application.Options.Gex;

/// <summary>
/// Dealer gamma (GEX) and vanna (VEX) exposure by strike and expiration.
/// Values are dollars: GEX is the dollar delta dealers must trade per 1% move in
/// the underlying, VEX per 1 point (0.01) rise in implied volatility. Positive
/// means dealers hedge against the move (dampening), negative with it.
/// </summary>
/// <param name="AsOf">When the provider took the chain snapshot. May be a previous session's.</param>
/// <param name="Expirations">The table's columns, earliest first.</param>
/// <param name="Rows">The table's rows, highest strike first.</param>
/// <param name="ContractCount">Contracts with open interest that went into the table.</param>
/// <param name="SkippedContractCount">Contracts with open interest left out because no gamma or IV could be had for them.</param>
public sealed record GexTable(
    string Symbol,
    decimal UnderlyingPrice,
    DateTimeOffset AsOf,
    IReadOnlyList<DateOnly> Expirations,
    IReadOnlyList<GexRow> Rows,
    GexLevels GexLevels,
    GexLevels VexLevels,
    double TotalGex,
    double TotalVex,
    int ContractCount,
    int SkippedContractCount);

/// <param name="Gex">One value per expiration column; null where no contract has open interest.</param>
/// <param name="Vex">As <paramref name="Gex"/>, for vanna.</param>
public sealed record GexRow(
    decimal Strike,
    IReadOnlyList<double?> Gex,
    IReadOnlyList<double?> Vex,
    double NetGex,
    double NetVex);

/// <summary>One cell of the table.</summary>
public sealed record GexNode(
    decimal Strike,
    DateOnly Expiration,
    double Value);

/// <summary>
/// Notable nodes, each by largest absolute value (sign doesn't matter):
/// the King across the whole table, the strongest floor (strike below the
/// price) and ceiling (above it), and the gatekeeper: the strongest node
/// strictly between the price and the King's strike.
/// </summary>
public sealed record GexLevels(
    GexNode? King,
    GexNode? Floor,
    GexNode? Ceiling,
    GexNode? Gatekeeper);
