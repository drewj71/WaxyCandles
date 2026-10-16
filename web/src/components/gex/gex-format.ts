const compact = new Intl.NumberFormat("en-US", {
    notation: "compact",
    maximumFractionDigits: 1,
});

const dollars = new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: 0,
});

/** 1234567 → "1.2M", -350000 → "-350K". */
export function formatCompact(value: number): string {
    return compact.format(value);
}

export function formatDollars(value: number): string {
    return dollars.format(value);
}

export function formatStrike(strike: number): string {
    return Number.isInteger(strike)
        ? strike.toString()
        : strike.toFixed(2).replace(/0$/, "");
}

/** "2026-10-16" → "Oct 16", read as a calendar date (no time-zone shift). */
export function formatExpiration(date: string): string {
    return new Date(`${date}T00:00:00Z`).toLocaleDateString("en-US", {
        month: "short",
        day: "numeric",
        timeZone: "UTC",
    });
}

/** Calendar days from the snapshot's US/Eastern date to the expiration. */
export function daysToExpiration(date: string, asOf: string): number {
    const asOfDate = new Date(asOf).toLocaleDateString("en-CA", {
        timeZone: "America/New_York",
    });

    return Math.round(
        (Date.parse(`${date}T00:00:00Z`) - Date.parse(`${asOfDate}T00:00:00Z`)) /
            86_400_000,
    );
}

export function formatAsOf(asOf: string): string {
    return new Date(asOf).toLocaleString("en-US", {
        timeZone: "America/New_York",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
        timeZoneName: "short",
    });
}

// Matplotlib's viridis, as nine evenly spaced stops: purple (lowest) to yellow (highest).
const VIRIDIS = [
    [68, 1, 84],
    [72, 40, 120],
    [62, 73, 137],
    [49, 104, 142],
    [38, 130, 142],
    [31, 158, 137],
    [53, 183, 121],
    [110, 206, 88],
    [253, 231, 37],
] as const;

/** Viridis colour at t in [0, 1], and whether text on it should be dark. */
export function viridis(t: number): { background: string; darkText: boolean } {
    const clamped = Math.min(1, Math.max(0, t));
    const scaled = clamped * (VIRIDIS.length - 1);
    const index = Math.min(VIRIDIS.length - 2, Math.floor(scaled));
    const fraction = scaled - index;

    const [r, g, b] = VIRIDIS[index].map((channel, i) =>
        Math.round(channel + (VIRIDIS[index + 1][i] - channel) * fraction),
    );

    return {
        background: `rgb(${r} ${g} ${b})`,
        darkText: clamped > 0.6,
    };
}

export const VIRIDIS_GRADIENT = `linear-gradient(to right, ${VIRIDIS.map(
    ([r, g, b]) => `rgb(${r} ${g} ${b})`,
).join(", ")})`;
