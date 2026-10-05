import { api } from "./client";

export interface WatchlistStock {
    symbol: string;
    price: number;
    change: number;
    changePercent: number;
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