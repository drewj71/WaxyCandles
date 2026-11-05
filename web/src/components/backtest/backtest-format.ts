/** A yyyy-MM-dd trading date, shown as that calendar date in any time zone. */
export function formatDate(
    date: string,
    options: Intl.DateTimeFormatOptions = { year: "numeric", month: "short", day: "numeric" },
): string {
    return new Date(`${date}T00:00:00Z`).toLocaleDateString("en-US", {
        ...options,
        timeZone: "UTC",
    });
}

export function formatMoney(value: number, compact = false): string {
    return value.toLocaleString("en-US", {
        style: "currency",
        currency: "USD",
        notation: compact ? "compact" : "standard",
        maximumFractionDigits: compact ? 1 : 2,
    });
}

export function formatPercent(value: number | null, signed = true): string {
    if (value === null) {
        return "—";
    }

    const sign = signed && value > 0 ? "+" : "";
    return `${sign}${value.toFixed(2)}%`;
}

export function formatNumber(value: number | null, digits = 2): string {
    return value === null ? "—" : value.toFixed(digits);
}

/** Today's (local) date as yyyy-MM-dd, for date inputs. */
export function isoDate(date: Date): string {
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}
