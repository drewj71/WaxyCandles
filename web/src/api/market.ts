import { api } from "./client";

/** US regular-session status, by the same calendar the price poller uses. */
export interface MarketStatus {
    isOpen: boolean;
    /** Today's close if open, else the next trading day's open. */
    nextChange: string;
}

export async function getMarketStatus(): Promise<MarketStatus> {
    const response =
        await api.get<MarketStatus>("/market/status");

    return response.data;
}
