import { api } from "./client";

/** One of the user's named watchlists, symbols in list order. */
export interface WatchlistSummary {
    id: string;
    name: string;
    sortOrder: number;
    createdAt: string;
    symbols: string[];
}

export interface SparklinePoint {
    timestamp: string;
    close: number;
}

/** A quote-table row, from stored data only (never a vendor call). */
export interface WatchlistQuote {
    symbol: string;
    // Null until the price poller stores a first price for the symbol.
    price: number | null;
    change: number | null;
    changePercent: number | null;
    previousClose: number | null;
    priceTimestamp: string | null;
    /** Range of the stored (about once a minute) prices of the latest session. */
    dayLow: number | null;
    dayHigh: number | null;
    /** Volume of the latest stored daily candle, and its date. */
    volume: number | null;
    volumeDate: string | null;
    /** Daily closes for the last 30 days, oldest first; empty when none stored. */
    sparkline: SparklinePoint[];
}

export const watchlistsQueryKey = ["watchlists"] as const;

export const watchlistQuotesQueryKey = (id: string) =>
    ["watchlists", id, "quotes"] as const;

/** Limits enforced by the API (WatchlistRules); mirrored for the UI. */
export const maxListsPerUser = 20;
export const maxSymbolsPerList = 50;
export const maxListNameLength = 50;

export async function getWatchlists(): Promise<WatchlistSummary[]> {
    const response = await api.get<WatchlistSummary[]>("/watchlists");
    return response.data;
}

export async function getWatchlistQuotes(
    id: string,
): Promise<WatchlistQuote[]> {
    const response = await api.get<WatchlistQuote[]>(
        `/watchlists/${id}/quotes`,
    );
    return response.data;
}

export async function createWatchlist(
    name: string,
): Promise<WatchlistSummary> {
    const response = await api.post<WatchlistSummary>("/watchlists", { name });
    return response.data;
}

export async function renameWatchlist(
    id: string,
    name: string,
): Promise<WatchlistSummary> {
    const response = await api.put<WatchlistSummary>(`/watchlists/${id}`, {
        name,
    });
    return response.data;
}

export async function deleteWatchlist(id: string): Promise<void> {
    await api.delete(`/watchlists/${id}`);
}

export async function reorderWatchlists(ids: string[]): Promise<void> {
    await api.put("/watchlists/order", { ids });
}

export async function addWatchlistSymbol(
    id: string,
    symbol: string,
): Promise<WatchlistSummary> {
    const response = await api.post<WatchlistSummary>(
        `/watchlists/${id}/symbols`,
        { symbol },
    );
    return response.data;
}

export async function removeWatchlistSymbol(
    id: string,
    symbol: string,
): Promise<void> {
    await api.delete(
        `/watchlists/${id}/symbols/${encodeURIComponent(symbol)}`,
    );
}

export async function reorderWatchlistSymbols(
    id: string,
    symbols: string[],
): Promise<WatchlistSummary> {
    const response = await api.put<WatchlistSummary>(
        `/watchlists/${id}/symbols/order`,
        { symbols },
    );
    return response.data;
}
