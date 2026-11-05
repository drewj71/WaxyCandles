import { api } from "./client";

export type BacktestStrategyType =
    | "MovingAverageCrossover"
    | "RsiMeanReversion"
    | "Breakout";

export type MovingAverageType = "Sma" | "Ema";

export interface BacktestRequest {
    symbol: string;
    /** yyyy-MM-dd */
    from: string;
    /** yyyy-MM-dd; a future date means "up to now". */
    to: string;
    strategy: BacktestStrategyType;
    movingAverageCrossover?: {
        fastPeriod: number;
        slowPeriod: number;
        movingAverageType: MovingAverageType;
    };
    rsiMeanReversion?: {
        period: number;
        oversold: number;
        overbought: number;
    };
    breakout?: {
        entryPeriod: number;
        exitPeriod: number;
    };
    initialCapital: number;
    commissionPerTrade: number;
    /** Percent of each fill's value (0.1 = 0.1%). */
    commissionPercent: number;
    slippageBps: number;
}

/** Percentages are percent values (12.5 = 12.5%). */
export interface BacktestMetrics {
    finalEquity: number;
    totalReturnPercent: number;
    cagrPercent: number | null;
    /** Positive: 25 means equity fell 25% from a peak. */
    maxDrawdownPercent: number;
    maxDrawdownPeakDate: string | null;
    maxDrawdownTroughDate: string | null;
    sharpeRatio: number | null;
    /** Closed trades only. */
    tradeCount: number;
    winRatePercent: number | null;
    averageTradeReturnPercent: number | null;
    exposurePercent: number;
    hasOpenPosition: boolean;
}

export interface BacktestTrade {
    entryDate: string;
    entryPrice: number;
    /** The last bar's date for an open trade. */
    exitDate: string;
    /** The last close for an open trade. */
    exitPrice: number;
    shares: number;
    profitLoss: number;
    returnPercent: number;
    isOpen: boolean;
}

export interface BacktestEquityPoint {
    date: string;
    strategy: number;
    benchmark: number;
}

export interface BacktestCandle {
    date: string;
    open: number;
    high: number;
    low: number;
    close: number;
    volume: number;
}

export interface BacktestResponse {
    symbol: string;
    strategy: BacktestStrategyType;
    requestedFrom: string;
    requestedTo: string;
    from: string;
    to: string;
    barCount: number;
    warmupBars: number;
    dataImported: boolean;
    costs: {
        initialCapital: number;
        commissionPerTrade: number;
        commissionPercent: number;
        slippageBps: number;
    };
    metrics: BacktestMetrics;
    benchmark: BacktestMetrics;
    equityCurve: BacktestEquityPoint[];
    trades: BacktestTrade[];
    candles: BacktestCandle[];
}

export async function runBacktest(
    request: BacktestRequest,
): Promise<BacktestResponse> {
    const response =
        await api.post<BacktestResponse>(
            "/backtests",
            request,
        );

    return response.data;
}
