import type { StockCandle } from "@/api/stocks";

const intradayMs: Record<string, number> = {
    OneHour: 60 * 60_000,
    ThirtyMinutes: 30 * 60_000,
    FifteenMinutes: 15 * 60_000,
    FiveMinutes: 5 * 60_000,
};

const easternDate = new Intl.DateTimeFormat("en-CA", {
    timeZone: "America/New_York",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
});

/**
 * The candles with a live price applied to the forming (last) candle: it
 * extends that candle's high/low/close if the price falls inside it, or
 * starts the next candle if it is past it. Daily candles are stored at UTC
 * midnight of the trading date (US/Eastern); intraday ones continue the
 * existing candles' grid. A price older than the last candle changes nothing.
 * Volume isn't known from a price, so a new candle starts at 0.
 */
export function applyPriceToCandles(
    candles: StockCandle[],
    price: number,
    timestamp: string,
    interval: string,
): StockCandle[] {
    const last = candles.at(-1);

    if (!last) {
        return candles;
    }

    const at = Date.parse(timestamp);
    const lastAt = Date.parse(last.timestamp);

    let formingTimestamp: string | null = null;

    if (interval === "OneDay") {
        const day = easternDate.format(at);
        const lastDay = new Date(lastAt).toISOString().slice(0, 10);

        if (day < lastDay) {
            return candles;
        }

        if (day > lastDay) {
            formingTimestamp = `${day}T00:00:00Z`;
        }
    } else {
        const step = intradayMs[interval];

        if (!step || at < lastAt) {
            return candles;
        }

        if (at >= lastAt + step) {
            formingTimestamp = new Date(
                lastAt + Math.floor((at - lastAt) / step) * step,
            ).toISOString();
        }
    }

    if (formingTimestamp !== null) {
        return [
            ...candles,
            {
                timestamp: formingTimestamp,
                open: price,
                high: price,
                low: price,
                close: price,
                volume: 0,
            },
        ];
    }

    return [
        ...candles.slice(0, -1),
        {
            ...last,
            high: Math.max(last.high, price),
            low: Math.min(last.low, price),
            close: price,
        },
    ];
}
