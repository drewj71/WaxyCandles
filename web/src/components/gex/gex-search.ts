export type GexView = "gex" | "vex";

/** The GEX page's URL search params, so a refresh or shared link reopens the same board. */
export interface GexSearch {
    symbol?: string;
    days?: number;
    range?: number;
    view?: GexView;
}

export const DAY_OPTIONS = [
    { label: "1W", days: 7 },
    { label: "1M", days: 30 },
    { label: "3M", days: 90 },
    { label: "6M", days: 180 },
] as const;

export const RANGE_OPTIONS = [5, 10, 20, 30] as const;

export const DEFAULT_DAYS = 30;
export const DEFAULT_RANGE = 10;

function positiveNumber(value: unknown): number | undefined {
    const number = Number(value);

    return Number.isFinite(number) && number > 0
        ? number
        : undefined;
}

export function parseGexSearch(
    search: Record<string, unknown>,
): GexSearch {
    const symbol =
        typeof search.symbol === "string" && search.symbol.trim()
            ? search.symbol.trim().toUpperCase()
            : undefined;

    return {
        symbol,
        days: positiveNumber(search.days),
        range: positiveNumber(search.range),
        view: search.view === "vex" ? "vex" : undefined,
    };
}
