import { api } from "./client";

export interface StockQuote {
    symbol: string;
    price: number;
    previousClose: number;
    change: number;
    changePercent: number;
    volume: number;
    latestTradingDay: string | null;
}

export interface StockCandle {
    timestamp: string;
    open: number;
    high: number;
    low: number;
    close: number;
    volume: number;
}

export interface StockDetails {
    symbol: string;
    quote: StockQuote | null;
    historicalCandles: StockCandle[];
}

export async function getStockDetails(
    symbol: string,
    interval: string = "OneDay",
): Promise<StockDetails> {
    const response =
        await api.get<StockDetails>(
            `/stocks/${encodeURIComponent(symbol)}`,
            {
                params: {
                    interval,
                },
            },
        );

    return response.data;
}