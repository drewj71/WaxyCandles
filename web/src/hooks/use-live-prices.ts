import { useEffect, useSyncExternalStore } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";

import type { LivePrice } from "@/api/prices";
import type { StockDetails } from "@/api/stocks";
import type { WatchlistQuote } from "@/api/watchlists";
import { applyPriceToCandles } from "@/lib/live-candles";
import { livePrices } from "@/lib/live-prices-connection";

const timeframeIntervals: Record<string, string> = {
    "1D": "OneDay",
    "1H": "OneHour",
    "30m": "ThirtyMinutes",
    "15m": "FifteenMinutes",
    "5m": "FiveMinutes",
};

function isNewer(timestamp: string, than: string | null | undefined) {
    return !than || Date.parse(timestamp) > Date.parse(than);
}

/** Writes a pushed price into every cached query that shows that symbol. */
function applyLivePrice(queryClient: QueryClient, update: LivePrice) {
    const symbol = update.symbol.toUpperCase();

    // Watchlist quote tables: ["watchlists", id, "quotes"].
    queryClient.setQueriesData<WatchlistQuote[]>(
        { predicate: (query) => query.queryKey[0] === "watchlists" && query.queryKey[2] === "quotes" },
        (rows) =>
            rows?.map((row) =>
                row.symbol === symbol && isNewer(update.timestamp, row.priceTimestamp)
                    ? {
                        ...row,
                        price: update.price,
                        change: update.change,
                        changePercent: update.changePercent,
                        previousClose: update.previousClose,
                        priceTimestamp: update.timestamp,
                        dayHigh: row.dayHigh === null ? update.price : Math.max(row.dayHigh, update.price),
                        dayLow: row.dayLow === null ? update.price : Math.min(row.dayLow, update.price),
                    }
                    : row,
            ),
    );

    // The stock page: ["stock", symbol, timeframe]; the timeframe picks how
    // the forming candle is updated.
    for (const query of queryClient.getQueryCache().findAll({
        predicate: (q) => q.queryKey[0] === "stock" && String(q.queryKey[1]).toUpperCase() === symbol,
    })) {
        const details = query.state.data as StockDetails | undefined;

        if (!details || (details.quote && !isNewer(update.timestamp, details.quote.quotedAt))) {
            continue;
        }

        const interval = timeframeIntervals[String(query.queryKey[2])] ?? "OneDay";

        queryClient.setQueryData<StockDetails>(query.queryKey, {
            ...details,
            quote: {
                symbol,
                volume: details.quote?.volume ?? 0,
                latestTradingDay: details.quote?.latestTradingDay ?? null,
                price: update.price,
                change: update.change,
                changePercent: update.changePercent,
                previousClose: update.previousClose,
                quotedAt: update.timestamp,
            },
            historicalCandles: applyPriceToCandles(
                details.historicalCandles,
                update.price,
                update.timestamp,
                interval,
            ),
        });
    }
}

// The cache writer is registered once, however many components watch.
let cacheWriters = 0;
let detachCacheWriter: (() => void) | null = null;

/**
 * Keeps the given symbols live while the component is mounted: subscribes on
 * the shared prices connection and writes each pushed price into the cached
 * watchlist and stock queries. Returns the connection state.
 */
export function useLivePrices(symbols: readonly string[]) {
    const queryClient = useQueryClient();
    const key = [...new Set(symbols.map((s) => s.toUpperCase()))].sort().join(",");

    useEffect(() => {
        if (cacheWriters++ === 0) {
            detachCacheWriter = livePrices.onPrice((update) =>
                applyLivePrice(queryClient, update),
            );
        }

        return () => {
            if (--cacheWriters === 0) {
                detachCacheWriter?.();
                detachCacheWriter = null;
            }
        };
    }, [queryClient]);

    useEffect(() => {
        if (!key) {
            return;
        }

        return livePrices.watch(key.split(","));
    }, [key]);

    return useLiveConnectionState();
}

export function useLiveConnectionState() {
    return useSyncExternalStore(livePrices.onStateChange, livePrices.getState);
}
