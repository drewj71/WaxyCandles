import { useEffect, useRef } from "react";

import type { GexNode, GexTable } from "@/api/options";
import type { GexView } from "@/components/gex/gex-search";
import {
    daysToExpiration,
    formatCompact,
    formatDollars,
    formatExpiration,
    formatStrike,
    viridis,
} from "@/components/gex/gex-format";

interface GexHeatmapProps {
    table: GexTable;
    view: GexView;
}

type Mark = "king" | "floor" | "ceiling" | "gatekeeper";

const MARK_BADGES: Record<Exclude<Mark, "king">, { letter: string; title: string }> = {
    floor: { letter: "F", title: "Floor: largest node below the price" },
    ceiling: { letter: "C", title: "Ceiling: largest node above the price" },
    gatekeeper: { letter: "G", title: "Gatekeeper: largest node between the price and the King" },
};

function nodeKey(strike: number, expiration: string) {
    return `${strike}|${expiration}`;
}

/**
 * Strikes down the side (highest first), expirations across the top, each cell
 * the net dealer exposure there, coloured on viridis from the board's lowest
 * value (purple) to its highest (yellow). Zero has no fixed colour, so read the
 * number, not the colour.
 */
function GexHeatmap({ table, view }: GexHeatmapProps) {
    const containerRef = useRef<HTMLDivElement>(null);
    const priceRowRef = useRef<HTMLTableRowElement>(null);

    const values = table.rows.map((row) =>
        view === "gex" ? row.gex : row.vex,
    );

    const filled = values.flat().filter((v): v is number => v !== null);
    const min = filled.length > 0 ? Math.min(...filled) : 0;
    const max = filled.length > 0 ? Math.max(...filled) : 0;
    const span = max - min;

    const levels = view === "gex" ? table.gexLevels : table.vexLevels;

    const marks = new Map<string, Mark[]>();
    const addMark = (node: GexNode | null, mark: Mark) => {
        if (!node) {
            return;
        }

        const key = nodeKey(node.strike, node.expiration);
        marks.set(key, [...(marks.get(key) ?? []), mark]);
    };
    addMark(levels.king, "king");
    addMark(levels.floor, "floor");
    addMark(levels.ceiling, "ceiling");
    addMark(levels.gatekeeper, "gatekeeper");

    // The strike nearest the current price.
    const priceStrike = table.rows.reduce<number | null>(
        (nearest, row) =>
            nearest === null ||
            Math.abs(row.strike - table.underlyingPrice) <
                Math.abs(nearest - table.underlyingPrice)
                ? row.strike
                : nearest,
        null,
    );

    // Open centred on the price, as the interesting nodes are near it.
    useEffect(() => {
        const container = containerRef.current;
        const row = priceRowRef.current;

        if (container && row) {
            container.scrollTop =
                row.offsetTop - container.clientHeight / 2 + row.clientHeight / 2;
        }
    }, [table]);

    return (
        <div
            ref={containerRef}
            className="max-h-[70vh] overflow-auto rounded-lg border bg-background"
        >
            <table className="w-full border-separate border-spacing-0 text-xs tabular-nums">
                <caption className="sr-only">
                    {view === "gex" ? "Gamma" : "Vanna"} exposure for {table.symbol} by strike and expiration
                </caption>

                <thead>
                    <tr>
                        <th
                            scope="col"
                            className="sticky top-0 left-0 z-30 border-b border-r bg-background px-3 py-2 text-left font-medium"
                        >
                            Strike
                        </th>

                        {table.expirations.map((expiration) => (
                            <th
                                key={expiration}
                                scope="col"
                                className="sticky top-0 z-20 min-w-20 border-b bg-background px-2 py-2 text-center font-medium whitespace-nowrap"
                            >
                                <div>{formatExpiration(expiration)}</div>
                                <div className="text-[10px] font-normal text-muted-foreground">
                                    {daysToExpiration(expiration, table.asOf)}d
                                </div>
                            </th>
                        ))}

                        <th
                            scope="col"
                            className="sticky top-0 z-20 min-w-20 border-b border-l bg-background px-2 py-2 text-right font-medium"
                        >
                            Net
                        </th>
                    </tr>
                </thead>

                <tbody>
                    {table.rows.map((row, rowIndex) => {
                        const isPriceRow = row.strike === priceStrike;
                        const net = view === "gex" ? row.netGex : row.netVex;

                        return (
                            <tr
                                key={row.strike}
                                ref={isPriceRow ? priceRowRef : undefined}
                            >
                                <th
                                    scope="row"
                                    className={
                                        isPriceRow
                                            ? "sticky left-0 z-10 border-r bg-primary px-3 py-1.5 text-left font-semibold whitespace-nowrap text-primary-foreground"
                                            : "sticky left-0 z-10 border-r bg-background px-3 py-1.5 text-left font-medium whitespace-nowrap"
                                    }
                                    title={
                                        isPriceRow
                                            ? `Nearest strike to the price, ${table.underlyingPrice.toFixed(2)}`
                                            : undefined
                                    }
                                >
                                    {isPriceRow && <span aria-hidden>▶ </span>}
                                    {formatStrike(row.strike)}
                                </th>

                                {values[rowIndex].map((value, columnIndex) => {
                                    const expiration = table.expirations[columnIndex];

                                    if (value === null) {
                                        return (
                                            <td
                                                key={expiration}
                                                className="bg-muted/40 px-2 py-1.5 text-center text-muted-foreground/50"
                                            >
                                                ·
                                            </td>
                                        );
                                    }

                                    const cellMarks = marks.get(nodeKey(row.strike, expiration)) ?? [];
                                    const isKing = cellMarks.includes("king");
                                    const colour = viridis(span > 0 ? (value - min) / span : 0.5);

                                    return (
                                        <td
                                            key={expiration}
                                            className={
                                                isKing
                                                    ? "relative px-2 py-1.5 text-center font-bold whitespace-nowrap outline-2 -outline-offset-2 outline-white"
                                                    : "relative px-2 py-1.5 text-center whitespace-nowrap"
                                            }
                                            style={{
                                                backgroundColor: colour.background,
                                                color: colour.darkText ? "#111" : "#fff",
                                            }}
                                            title={`${formatStrike(row.strike)} ${formatExpiration(expiration)}: ${formatDollars(value)}`}
                                        >
                                            {isKing && <span aria-label="King node">★ </span>}
                                            {formatCompact(value)}

                                            {cellMarks
                                                .filter((mark): mark is Exclude<Mark, "king"> => mark !== "king")
                                                .map((mark) => (
                                                    <sup
                                                        key={mark}
                                                        className="ml-0.5 text-[9px] font-semibold"
                                                        title={MARK_BADGES[mark].title}
                                                    >
                                                        {MARK_BADGES[mark].letter}
                                                    </sup>
                                                ))}
                                        </td>
                                    );
                                })}

                                <td
                                    className={
                                        net >= 0
                                            ? "border-l px-2 py-1.5 text-right font-medium text-green-600 dark:text-green-400"
                                            : "border-l px-2 py-1.5 text-right font-medium text-red-600 dark:text-red-400"
                                    }
                                    title={formatDollars(net)}
                                >
                                    {formatCompact(net)}
                                </td>
                            </tr>
                        );
                    })}
                </tbody>
            </table>
        </div>
    );
}

export default GexHeatmap;
