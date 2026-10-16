import { useEffect, type ReactNode } from "react";
import { useForm } from "react-hook-form";
import { useQuery } from "@tanstack/react-query";
import { isAxiosError } from "axios";
import { RefreshCw, Search, Star } from "lucide-react";

import { Route } from "@/routes/_dashboard/gex";
import { getGexTable, type GexNode } from "@/api/options";
import { apiErrorMessages } from "@/api/errors";

import { Button } from "@/components/ui/button";
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";

import GexHeatmap from "@/components/gex/GexHeatmap";
import {
    DAY_OPTIONS,
    DEFAULT_DAYS,
    DEFAULT_RANGE,
    RANGE_OPTIONS,
    type GexView,
} from "@/components/gex/gex-search";
import {
    formatAsOf,
    formatCompact,
    formatDollars,
    formatExpiration,
    formatStrike,
    VIRIDIS_GRADIENT,
} from "@/components/gex/gex-format";

type SymbolFormValues = {
    symbol: string;
};

function easternDate(date: Date) {
    return date.toLocaleDateString("en-CA", {
        timeZone: "America/New_York",
    });
}

function NodeSummary({
    label,
    node,
    icon,
}: {
    label: string;
    node: GexNode | null;
    icon?: ReactNode;
}) {
    return (
        <Card size="sm">
            <CardHeader>
                <CardTitle className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                    {icon}
                    {label}
                </CardTitle>
            </CardHeader>

            <CardContent>
                {node ? (
                    <>
                        <p className="text-lg font-semibold tabular-nums">
                            {formatStrike(node.strike)}
                            <span className="ml-1.5 text-xs font-normal text-muted-foreground">
                                {formatExpiration(node.expiration)}
                            </span>
                        </p>
                        <p
                            className="text-xs text-muted-foreground tabular-nums"
                            title={formatDollars(node.value)}
                        >
                            {formatCompact(node.value)}
                        </p>
                    </>
                ) : (
                    <p className="text-lg font-semibold">—</p>
                )}
            </CardContent>
        </Card>
    );
}

function Gex() {
    const search = Route.useSearch();
    const navigate = Route.useNavigate();

    const symbol = search.symbol;
    const days = search.days ?? DEFAULT_DAYS;
    const range = search.range ?? DEFAULT_RANGE;
    const view: GexView = search.view ?? "gex";

    const { register, handleSubmit, reset } = useForm<SymbolFormValues>({
        defaultValues: { symbol: symbol ?? "" },
    });

    // Keep the box in step with the URL (back/forward, links from other pages).
    useEffect(() => {
        reset({ symbol: symbol ?? "" });
    }, [symbol, reset]);

    const gexQuery = useQuery({
        queryKey: ["gex", symbol, days, range],
        queryFn: () => getGexTable(symbol!, days, range),
        enabled: !!symbol,
        // The API caches each board for a minute; asking sooner returns the same one.
        staleTime: 60_000,
        // A 4xx (unknown symbol, bad range) won't change on retry.
        retry: (failureCount, error) =>
            !(isAxiosError(error) && (error.response?.status ?? 500) < 500) &&
            failureCount < 2,
    });

    const updateSearch = (changes: Partial<typeof search>) =>
        navigate({
            search: (previous) => ({ ...previous, ...changes }),
        });

    const onSubmit = handleSubmit(({ symbol: entered }) => {
        const next = entered.trim().toUpperCase();

        if (next) {
            updateSearch({ symbol: next });
        }
    });

    const table = gexQuery.data;
    const levels = table && (view === "gex" ? table.gexLevels : table.vexLevels);
    const total = table && (view === "gex" ? table.totalGex : table.totalVex);
    const fromEarlierSession =
        table && easternDate(new Date(table.asOf)) !== easternDate(new Date());

    return (
        <div className="mx-auto max-w-7xl space-y-6">
            <div className="flex flex-wrap items-end justify-between gap-4">
                <div className="space-y-1">
                    <h1 className="text-3xl font-semibold tracking-tight">
                        GEX
                    </h1>
                    <p className="text-sm text-muted-foreground">
                        Dealer gamma and vanna exposure by strike and expiration.
                    </p>
                </div>

                <form
                    onSubmit={onSubmit}
                    className="flex items-end gap-2"
                >
                    <Input
                        {...register("symbol")}
                        placeholder="Symbol, e.g. SPY"
                        aria-label="Symbol"
                        autoComplete="off"
                        className="w-40 uppercase"
                    />
                    <Button type="submit">
                        <Search />
                        Load
                    </Button>
                </form>
            </div>

            <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
                <div className="flex gap-1" role="group" aria-label="View">
                    {(["gex", "vex"] as const).map((option) => (
                        <Button
                            key={option}
                            size="sm"
                            variant={view === option ? "secondary" : "ghost"}
                            aria-pressed={view === option}
                            onClick={() => updateSearch({ view: option === "gex" ? undefined : option })}
                        >
                            {option.toUpperCase()}
                        </Button>
                    ))}
                </div>

                <div className="flex items-center gap-1" role="group" aria-label="Expirations">
                    <span className="mr-1 text-xs text-muted-foreground">Expiries</span>
                    {DAY_OPTIONS.map((option) => (
                        <Button
                            key={option.label}
                            size="sm"
                            variant={days === option.days ? "secondary" : "ghost"}
                            aria-pressed={days === option.days}
                            onClick={() => updateSearch({ days: option.days })}
                        >
                            {option.label}
                        </Button>
                    ))}
                </div>

                <div className="flex items-center gap-1" role="group" aria-label="Strike range">
                    <span className="mr-1 text-xs text-muted-foreground">Strikes</span>
                    {RANGE_OPTIONS.map((option) => (
                        <Button
                            key={option}
                            size="sm"
                            variant={range === option ? "secondary" : "ghost"}
                            aria-pressed={range === option}
                            onClick={() => updateSearch({ range: option })}
                        >
                            ±{option}%
                        </Button>
                    ))}
                </div>

                <Button
                    size="sm"
                    variant="outline"
                    className="ml-auto"
                    disabled={!symbol || gexQuery.isFetching}
                    onClick={() => gexQuery.refetch()}
                >
                    <RefreshCw className={gexQuery.isFetching ? "animate-spin" : undefined} />
                    Refresh
                </Button>
            </div>

            {!symbol ? (
                <Card>
                    <CardContent className="py-12 text-center text-sm text-muted-foreground">
                        Enter a symbol to see its GEX board.
                    </CardContent>
                </Card>
            ) : gexQuery.isLoading ? (
                <Card>
                    <CardContent className="py-12 text-center text-sm text-muted-foreground">
                        Loading the {symbol} option chain...
                    </CardContent>
                </Card>
            ) : gexQuery.error || !table || !levels || total === undefined ? (
                <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
                    {apiErrorMessages(gexQuery.error, `Unable to load GEX for ${symbol}.`).map((message) => (
                        <p key={message}>{message}</p>
                    ))}
                </div>
            ) : (
                <>
                    <div className="grid gap-4 sm:grid-cols-3 lg:grid-cols-6">
                        <Card size="sm">
                            <CardHeader>
                                <CardTitle className="text-xs font-medium text-muted-foreground">
                                    {table.symbol} price
                                </CardTitle>
                            </CardHeader>
                            <CardContent>
                                <p className="text-lg font-semibold tabular-nums">
                                    ${table.underlyingPrice.toFixed(2)}
                                </p>
                                <p className="text-xs text-muted-foreground">
                                    {formatAsOf(table.asOf)}
                                </p>
                            </CardContent>
                        </Card>

                        <Card size="sm">
                            <CardHeader>
                                <CardTitle className="text-xs font-medium text-muted-foreground">
                                    Net {view.toUpperCase()}
                                </CardTitle>
                            </CardHeader>
                            <CardContent>
                                <p
                                    className={
                                        total >= 0
                                            ? "text-lg font-semibold text-green-600 tabular-nums dark:text-green-400"
                                            : "text-lg font-semibold text-red-600 tabular-nums dark:text-red-400"
                                    }
                                    title={formatDollars(total)}
                                >
                                    {formatCompact(total)}
                                </p>
                                <p className="text-xs text-muted-foreground">
                                    {view === "gex" ? "$ per 1% move" : "$ per 1 vol point"}
                                </p>
                            </CardContent>
                        </Card>

                        <NodeSummary
                            label="King"
                            node={levels.king}
                            icon={<Star className="size-3" />}
                        />
                        <NodeSummary label="Gatekeeper" node={levels.gatekeeper} />
                        <NodeSummary label="Floor" node={levels.floor} />
                        <NodeSummary label="Ceiling" node={levels.ceiling} />
                    </div>

                    {fromEarlierSession && (
                        <div className="rounded-lg border border-amber-500/50 bg-amber-500/10 p-3 text-sm text-amber-700 dark:text-amber-400">
                            This chain is a snapshot from {formatAsOf(table.asOf)}, not today's session.
                            Trial plans only get the previous session's options data.
                        </div>
                    )}

                    {table.rows.length === 0 ? (
                        <Card>
                            <CardContent className="py-12 text-center text-sm text-muted-foreground">
                                No contracts with open interest in this range. Try more expiries or a wider strike range.
                            </CardContent>
                        </Card>
                    ) : (
                        <GexHeatmap table={table} view={view} />
                    )}

                    <div className="flex flex-wrap items-center justify-between gap-4 text-xs text-muted-foreground">
                        <div className="flex items-center gap-2">
                            <span>Lowest</span>
                            <span
                                className="h-2.5 w-40 rounded-sm"
                                style={{ backgroundImage: VIRIDIS_GRADIENT }}
                                aria-hidden
                            />
                            <span>Highest</span>
                            <span className="ml-2">
                                Colour runs from the board's lowest value to its highest, so zero has no fixed colour. Read the number.
                            </span>
                        </div>

                        <p>
                            {table.contractCount.toLocaleString()} contracts
                            {table.skippedContractCount > 0 &&
                                ` (${table.skippedContractCount.toLocaleString()} skipped: no Greeks or usable price)`}
                            . Assumes dealers are long calls and short puts.
                        </p>
                    </div>
                </>
            )}
        </div>
    );
}

export default Gex;
