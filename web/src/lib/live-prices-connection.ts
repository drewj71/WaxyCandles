import {
    HubConnectionBuilder,
    HubConnectionState,
    LogLevel,
    type HubConnection,
} from "@microsoft/signalr";

import { refreshSession } from "@/api/client";
import {
    priceUpdatedEvent,
    pricesHubUrl,
    subscribeMethod,
    unsubscribeMethod,
    type LivePrice,
} from "@/api/prices";

export type LiveConnectionState = "idle" | "connecting" | "connected";

const maxRetryDelayMs = 30_000;

// After the last component stops watching: navigating between price pages
// shouldn't close and reopen the connection.
const idleCloseDelayMs = 10_000;

function retryDelay(attempt: number) {
    return Math.min(maxRetryDelayMs, 1000 * 2 ** attempt);
}

/**
 * One SignalR connection to the prices hub per tab, shared by every component
 * that shows live prices. Components add and release symbols (counted, so two
 * views of one symbol subscribe once); the hub's subscriptions live only as
 * long as the server-side connection, so every (re)connect subscribes again.
 */
class LivePricesConnection {
    private connection: HubConnection | null = null;
    private readonly counts = new Map<string, number>();
    private readonly priceListeners = new Set<(price: LivePrice) => void>();
    private readonly stateListeners = new Set<() => void>();
    private state: LiveConnectionState = "idle";
    private startTimer: ReturnType<typeof setTimeout> | undefined;
    private closeTimer: ReturnType<typeof setTimeout> | undefined;

    getState = () => this.state;

    onStateChange = (listener: () => void) => {
        this.stateListeners.add(listener);
        return () => {
            this.stateListeners.delete(listener);
        };
    };

    onPrice(listener: (price: LivePrice) => void) {
        this.priceListeners.add(listener);
        return () => {
            this.priceListeners.delete(listener);
        };
    }

    /** Watches the symbols until the returned function is called. */
    watch(symbols: readonly string[]): () => void {
        const normalized = [
            ...new Set(symbols.map((s) => s.trim().toUpperCase()).filter(Boolean)),
        ];

        for (const symbol of normalized) {
            const count = this.counts.get(symbol) ?? 0;
            this.counts.set(symbol, count + 1);

            if (count === 0) {
                void this.invoke(subscribeMethod, symbol);
            }
        }

        this.ensureOpen();

        return () => {
            for (const symbol of normalized) {
                const count = (this.counts.get(symbol) ?? 1) - 1;

                if (count <= 0) {
                    this.counts.delete(symbol);
                    void this.invoke(unsubscribeMethod, symbol);
                } else {
                    this.counts.set(symbol, count);
                }
            }

            if (this.counts.size === 0) {
                this.scheduleClose();
            }
        };
    }

    private setState(state: LiveConnectionState) {
        if (this.state !== state) {
            this.state = state;
            this.stateListeners.forEach((listener) => listener());
        }
    }

    private async invoke(method: string, symbol: string) {
        if (this.connection?.state !== HubConnectionState.Connected) {
            // Sent on connect instead.
            return;
        }

        try {
            await this.connection.invoke(method, symbol);
        } catch (error) {
            // An invalid symbol or the per-connection limit: that symbol just
            // isn't live; the page still shows its stored price.
            console.warn(`Live prices: ${method} ${symbol} failed.`, error);
        }
    }

    private async subscribeAll() {
        await Promise.all(
            [...this.counts.keys()].map((symbol) =>
                this.invoke(subscribeMethod, symbol),
            ),
        );
    }

    private ensureOpen() {
        clearTimeout(this.closeTimer);
        this.closeTimer = undefined;

        if (this.connection || this.counts.size === 0) {
            return;
        }

        const connection = new HubConnectionBuilder()
            .withUrl(pricesHubUrl, { withCredentials: true })
            .withAutomaticReconnect({
                nextRetryDelayInMilliseconds: (context) =>
                    retryDelay(context.previousRetryCount),
            })
            .configureLogging(LogLevel.Warning)
            .build();

        connection.on(priceUpdatedEvent, (price: LivePrice) => {
            this.priceListeners.forEach((listener) => listener(price));
        });

        // A new server-side connection: its subscriptions start empty.
        connection.onreconnected(() => {
            this.setState("connected");
            void this.subscribeAll();
        });

        connection.onreconnecting(() => {
            this.setState("connecting");
            // A reconnect negotiates again and needs a valid access cookie.
            void refreshSession().catch(() => undefined);
        });

        connection.onclose(() => {
            if (this.connection === connection) {
                this.connection = null;
                this.setState("idle");
                // Closed for good while still wanted (reconnects gave up).
                this.ensureOpen();
            }
        });

        this.connection = connection;
        this.setState("connecting");

        const start = async (attempt: number) => {
            if (this.connection !== connection) {
                return;
            }

            try {
                await connection.start();
                this.setState("connected");
                await this.subscribeAll();
            } catch (error) {
                if (this.connection !== connection) {
                    return;
                }

                console.warn("Live prices connection failed; retrying.", error);
                void refreshSession().catch(() => undefined);

                this.startTimer = setTimeout(
                    () => void start(attempt + 1),
                    retryDelay(attempt),
                );
            }
        };

        void start(0);
    }

    private scheduleClose() {
        clearTimeout(this.closeTimer);

        this.closeTimer = setTimeout(() => {
            if (this.counts.size > 0 || !this.connection) {
                return;
            }

            const connection = this.connection;
            this.connection = null;
            clearTimeout(this.startTimer);
            this.setState("idle");
            void connection.stop();
        }, idleCloseDelayMs);
    }
}

export const livePrices = new LivePricesConnection();
