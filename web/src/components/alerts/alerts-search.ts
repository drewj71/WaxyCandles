import { gexLevelKinds, type GexLevelKind } from "@/api/alerts";

/**
 * The Alerts page's URL search params. A GEX page level card links here with
 * these set, to open the create form pre-filled with that level and board.
 */
export interface AlertsSearch {
    symbol?: string;
    level?: GexLevelKind;
    days?: number;
    range?: number;
}

function positiveNumber(value: unknown): number | undefined {
    const number = Number(value);

    return Number.isFinite(number) && number > 0
        ? number
        : undefined;
}

export function parseAlertsSearch(
    search: Record<string, unknown>,
): AlertsSearch {
    return {
        symbol:
            typeof search.symbol === "string" && search.symbol.trim()
                ? search.symbol.trim().toUpperCase()
                : undefined,
        level: gexLevelKinds.find((kind) => kind === search.level),
        days: positiveNumber(search.days),
        range: positiveNumber(search.range),
    };
}
