import { api } from "@/api/client";

/** A newly stored price, pushed over the prices hub. */
export interface LivePrice {
    symbol: string;
    price: number;
    /** Versus the previous close, as the vendor reported it. */
    change: number;
    changePercent: number;
    previousClose: number;
    /** When the provider observed the price (ISO). */
    timestamp: string;
}

// The SignalR hub lives on the API's origin, outside /api.
export const pricesHubUrl = new URL(
    "/hubs/prices",
    api.defaults.baseURL,
).toString();

/** Client method the hub calls for each new price. */
export const priceUpdatedEvent = "priceUpdated";

/** Hub methods. */
export const subscribeMethod = "Subscribe";
export const unsubscribeMethod = "Unsubscribe";

/** The server's per-connection limit (LivePrices:MaxSubscriptionsPerConnection default). */
export const maxLiveSymbols = 100;
