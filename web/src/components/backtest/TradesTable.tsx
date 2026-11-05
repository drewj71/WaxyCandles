import { useMemo, useState } from "react";
import { ArrowDown, ArrowUp, ArrowUpDown } from "lucide-react";

import type { BacktestTrade } from "@/api/backtests";
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from "@/components/ui/table";
import { formatDate, formatMoney, formatPercent } from "./backtest-format";

type SortKey = "entryDate" | "exitDate" | "entryPrice" | "exitPrice" | "shares" | "profitLoss" | "returnPercent";

const columns: { key: SortKey; label: string; numeric: boolean }[] = [
    { key: "entryDate", label: "Entry", numeric: false },
    { key: "entryPrice", label: "Entry price", numeric: true },
    { key: "exitDate", label: "Exit", numeric: false },
    { key: "exitPrice", label: "Exit price", numeric: true },
    { key: "shares", label: "Shares", numeric: true },
    { key: "profitLoss", label: "P&L", numeric: true },
    { key: "returnPercent", label: "Return", numeric: true },
];

function TradesTable({ trades }: { trades: BacktestTrade[] }) {
    const [sortKey, setSortKey] = useState<SortKey>("entryDate");
    const [ascending, setAscending] = useState(true);

    const sorted = useMemo(() => {
        const copy = [...trades];

        copy.sort((a, b) => {
            const x = a[sortKey];
            const y = b[sortKey];
            // Dates are yyyy-MM-dd, so string order is date order.
            const order = typeof x === "number" && typeof y === "number"
                ? x - y
                : String(x).localeCompare(String(y));

            return ascending ? order : -order;
        });

        return copy;
    }, [trades, sortKey, ascending]);

    function sortBy(key: SortKey) {
        if (key === sortKey) {
            setAscending(!ascending);
        } else {
            setSortKey(key);
            setAscending(true);
        }
    }

    if (trades.length === 0) {
        return (
            <p className="text-sm text-muted-foreground">
                The strategy made no trades in this range.
            </p>
        );
    }

    return (
        <Table>
            <TableHeader>
                <TableRow>
                    {columns.map((column) => {
                        const active = column.key === sortKey;
                        const Icon = active ? (ascending ? ArrowUp : ArrowDown) : ArrowUpDown;

                        return (
                            <TableHead
                                key={column.key}
                                className={column.numeric ? "text-right" : undefined}
                                aria-sort={active ? (ascending ? "ascending" : "descending") : "none"}
                            >
                                <button
                                    type="button"
                                    onClick={() => sortBy(column.key)}
                                    className="inline-flex items-center gap-1 hover:text-foreground"
                                >
                                    {column.label}
                                    <Icon className={active ? "size-3.5" : "size-3.5 opacity-40"} />
                                </button>
                            </TableHead>
                        );
                    })}
                </TableRow>
            </TableHeader>

            <TableBody>
                {sorted.map((trade) => (
                    <TableRow key={`${trade.entryDate}-${trade.exitDate}`}>
                        <TableCell>{formatDate(trade.entryDate)}</TableCell>
                        <TableCell className="text-right tabular-nums">{formatMoney(trade.entryPrice)}</TableCell>
                        <TableCell>
                            {formatDate(trade.exitDate)}
                            {trade.isOpen && (
                                <span className="ml-1.5 rounded-sm bg-muted px-1.5 py-0.5 text-xs text-muted-foreground">
                                    open
                                </span>
                            )}
                        </TableCell>
                        <TableCell className="text-right tabular-nums">{formatMoney(trade.exitPrice)}</TableCell>
                        <TableCell className="text-right tabular-nums">{trade.shares.toFixed(4)}</TableCell>
                        <TableCell
                            className={`text-right tabular-nums ${trade.profitLoss >= 0 ? "text-green-500" : "text-red-500"}`}
                        >
                            {formatMoney(trade.profitLoss)}
                        </TableCell>
                        <TableCell
                            className={`text-right tabular-nums ${trade.returnPercent >= 0 ? "text-green-500" : "text-red-500"}`}
                        >
                            {formatPercent(trade.returnPercent)}
                        </TableCell>
                    </TableRow>
                ))}
            </TableBody>
        </Table>
    );
}

export default TradesTable;
