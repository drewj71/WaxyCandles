import { useState } from "react";
import { Link } from "@tanstack/react-router";
import {
    ArrowDown,
    ArrowUp,
    ArrowUpDown,
    ChevronDown,
    ChevronUp,
    Trash2,
} from "lucide-react";
import { cn } from "cn";

import type { WatchlistQuote } from "@/api/watchlists";
import Sparkline from "@/components/charts/Sparkline";
import { Button } from "@/components/ui/button";
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from "@/components/ui/table";

export type SortKey =
    | "manual"
    | "symbol"
    | "price"
    | "change"
    | "changePercent"
    | "range"
    | "volume";

type SortState = { key: SortKey; descending: boolean };

/** "+1.23" / "-0.45", or "—" when there's no price yet. */
function formatSigned(value: number | null, suffix = ""): string {
    if (value === null) {
        return "—";
    }

    return `${value >= 0 ? "+" : ""}${value.toFixed(2)}${suffix}`;
}

function formatVolume(value: number | null): string {
    if (value === null) {
        return "—";
    }

    return new Intl.NumberFormat("en-US", {
        notation: "compact",
        maximumFractionDigits: 1,
    }).format(value);
}

function sortValue(row: WatchlistQuote, key: SortKey): number | string | null {
    switch (key) {
        case "symbol":
            return row.symbol;
        case "price":
            return row.price;
        case "change":
            return row.change;
        case "changePercent":
            return row.changePercent;
        case "range":
            return row.dayHigh !== null && row.dayLow !== null
                ? row.dayHigh - row.dayLow
                : null;
        case "volume":
            return row.volume;
        default:
            return null;
    }
}

function sortRows(rows: WatchlistQuote[], sort: SortState): WatchlistQuote[] {
    if (sort.key === "manual") {
        return rows;
    }

    // Rows without a value always sort last.
    return [...rows].sort((a, b) => {
        const av = sortValue(a, sort.key);
        const bv = sortValue(b, sort.key);

        if (av === null || bv === null) {
            return av === bv ? 0 : av === null ? 1 : -1;
        }

        const order = typeof av === "string"
            ? av.localeCompare(bv as string)
            : av - (bv as number);

        return sort.descending ? -order : order;
    });
}

type WatchlistQuoteTableProps = {
    rows: WatchlistQuote[];
    onRemove: (symbol: string) => void;
    onMove: (symbol: string, offset: -1 | 1) => void;
    busy: boolean;
};

/**
 * A watchlist's quotes. Sortable by any column; "List order" (the default)
 * shows the saved order and the up/down buttons that change it.
 */
function WatchlistQuoteTable({
    rows,
    onRemove,
    onMove,
    busy,
}: WatchlistQuoteTableProps) {
    const [sort, setSort] = useState<SortState>({
        key: "manual",
        descending: false,
    });

    const sorted = sortRows(rows, sort);
    const manual = sort.key === "manual";

    function toggleSort(key: SortKey) {
        setSort((current) =>
            current.key !== key
                ? { key, descending: key !== "symbol" }
                : current.descending === (key !== "symbol")
                    ? { key, descending: !current.descending }
                    : { key: "manual", descending: false },
        );
    }

    function sortHeader(
        label: string,
        sortKey: SortKey,
        align: "left" | "right" = "right",
    ) {
        const active = sort.key === sortKey;
        const Icon = !active ? ArrowUpDown : sort.descending ? ArrowDown : ArrowUp;

        return (
            <TableHead
                key={sortKey}
                className={align === "right" ? "text-right" : undefined}
                aria-sort={active ? (sort.descending ? "descending" : "ascending") : "none"}
            >
                <button
                    type="button"
                    onClick={() => toggleSort(sortKey)}
                    className={cn(
                        "inline-flex items-center gap-1 hover:text-foreground",
                        active && "text-foreground",
                    )}
                >
                    {label}
                    <Icon className={cn("size-3", !active && "opacity-40")} />
                </button>
            </TableHead>
        );
    }

    return (
        <div className="overflow-x-auto">
            <div className="flex justify-end px-4 pb-2 text-xs text-muted-foreground">
                {manual ? (
                    "List order"
                ) : (
                    <button
                        type="button"
                        className="underline-offset-2 hover:underline"
                        onClick={() => setSort({ key: "manual", descending: false })}
                    >
                        Back to list order
                    </button>
                )}
            </div>

            <Table>
                <TableHeader>
                    <TableRow>
                        {sortHeader("Symbol", "symbol", "left")}
                        {sortHeader("Last", "price")}
                        {sortHeader("Change", "change")}
                        {sortHeader("Change %", "changePercent")}
                        {sortHeader("Day range", "range")}
                        {sortHeader("Volume", "volume")}
                        <TableHead>30 days</TableHead>
                        <TableHead className="w-28" />
                    </TableRow>
                </TableHeader>

                <TableBody>
                    {sorted.map((row, index) => {
                        const tone =
                            row.change === null
                                ? ""
                                : row.change >= 0
                                    ? "text-green-600 dark:text-green-400"
                                    : "text-red-600 dark:text-red-400";

                        return (
                            <TableRow key={row.symbol}>
                                <TableCell className="font-semibold">
                                    <Link
                                        to="/stocks/$symbol"
                                        params={{ symbol: row.symbol }}
                                        className="hover:underline"
                                    >
                                        {row.symbol}
                                    </Link>
                                </TableCell>

                                <TableCell className="text-right tabular-nums">
                                    {row.price === null ? "—" : `$${row.price.toFixed(2)}`}
                                </TableCell>

                                <TableCell className={cn("text-right tabular-nums", tone)}>
                                    {formatSigned(row.change)}
                                </TableCell>

                                <TableCell className={cn("text-right tabular-nums", tone)}>
                                    {formatSigned(row.changePercent, "%")}
                                </TableCell>

                                <TableCell
                                    className="text-right tabular-nums"
                                    title="Range of the prices stored this session (sampled about once a minute)"
                                >
                                    {row.dayLow === null || row.dayHigh === null
                                        ? "—"
                                        : `${row.dayLow.toFixed(2)} – ${row.dayHigh.toFixed(2)}`}
                                </TableCell>

                                <TableCell
                                    className="text-right tabular-nums"
                                    title={
                                        row.volumeDate
                                            ? `Daily candle of ${new Date(row.volumeDate).toLocaleDateString("en-US", { timeZone: "UTC" })}`
                                            : "No stored daily candle"
                                    }
                                >
                                    {formatVolume(row.volume)}
                                </TableCell>

                                <TableCell>
                                    <Sparkline points={row.sparkline} />
                                </TableCell>

                                <TableCell>
                                    <div className="flex justify-end">
                                        {manual && (
                                            <>
                                                <Button
                                                    variant="ghost"
                                                    size="icon-xs"
                                                    disabled={busy || index === 0}
                                                    onClick={() => onMove(row.symbol, -1)}
                                                >
                                                    <ChevronUp />
                                                    <span className="sr-only">Move {row.symbol} up</span>
                                                </Button>
                                                <Button
                                                    variant="ghost"
                                                    size="icon-xs"
                                                    disabled={busy || index === sorted.length - 1}
                                                    onClick={() => onMove(row.symbol, 1)}
                                                >
                                                    <ChevronDown />
                                                    <span className="sr-only">Move {row.symbol} down</span>
                                                </Button>
                                            </>
                                        )}
                                        <Button
                                            variant="ghost"
                                            size="icon-xs"
                                            disabled={busy}
                                            onClick={() => onRemove(row.symbol)}
                                        >
                                            <Trash2 />
                                            <span className="sr-only">Remove {row.symbol}</span>
                                        </Button>
                                    </div>
                                </TableCell>
                            </TableRow>
                        );
                    })}
                </TableBody>
            </Table>
        </div>
    );
}

export default WatchlistQuoteTable;
