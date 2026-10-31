import { api } from "./client";

export interface WatchlistStock {
    symbol: string;
    // Null until the price poller stores a first price for the symbol.
    price: number | null;
    change: number | null;
    changePercent: number | null;
}

export async function getWatchlist(): Promise<string[]> {
    const response = await api.get<string[]>("/watchlist");

    return response.data;
}

export async function getWatchlistQuotes(): Promise<
    WatchlistStock[]
> {
    const response =
        await api.get<WatchlistStock[]>(
            "/watchlist/quotes",
        );

    return response.data;
}

export async function addToWatchlist(
    symbol: string,
): Promise<void> {
    await api.post(
        `/watchlist/${encodeURIComponent(symbol)}`,
    );
}

export async function removeFromWatchlist(
    symbol: string,
): Promise<void> {
    await api.delete(
        `/watchlist/${encodeURIComponent(symbol)}`,
    );
}