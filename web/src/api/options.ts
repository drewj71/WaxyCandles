import { api } from "./client";

export interface GexNode {
    strike: number;
    /** yyyy-MM-dd */
    expiration: string;
    value: number;
}

export interface GexLevels {
    king: GexNode | null;
    floor: GexNode | null;
    ceiling: GexNode | null;
    gatekeeper: GexNode | null;
}

export interface GexRow {
    strike: number;
    /** One value per expiration column; null where no contract has open interest. */
    gex: (number | null)[];
    vex: (number | null)[];
    netGex: number;
    netVex: number;
}

/**
 * Dealer exposure by strike (rows, highest first) and expiration (columns).
 * GEX is dollars of delta per 1% move, VEX per 1 point of IV.
 */
export interface GexTable {
    symbol: string;
    underlyingPrice: number;
    /** When the provider took the chain snapshot; may be a previous session's. */
    asOf: string;
    /** yyyy-MM-dd, earliest first. */
    expirations: string[];
    rows: GexRow[];
    gexLevels: GexLevels;
    vexLevels: GexLevels;
    totalGex: number;
    totalVex: number;
    contractCount: number;
    skippedContractCount: number;
}

export async function getGexTable(
    symbol: string,
    days: number,
    strikeRange: number,
): Promise<GexTable> {
    const response =
        await api.get<GexTable>(
            `/stocks/${encodeURIComponent(symbol)}/gex`,
            {
                params: {
                    days,
                    strikeRange,
                },
            },
        );

    return response.data;
}
