import { useMemo } from "react";
import { useForm, useWatch } from "react-hook-form";
import { useMutation } from "@tanstack/react-query";
import { Play } from "lucide-react";

import {
    runBacktest,
    type BacktestMetrics,
    type BacktestRequest,
    type BacktestResponse,
    type BacktestStrategyType,
    type MovingAverageType,
} from "@/api/backtests";
import { apiErrorMessages } from "@/api/errors";

import { Button } from "@/components/ui/button";
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";

import StockCandlestickChart, {
    type CandleMarker,
} from "@/components/charts/StockCandlestickChart";
import EquityCurveChart from "@/components/backtest/EquityCurveChart";
import TradesTable from "@/components/backtest/TradesTable";
import {
    formatDate,
    formatMoney,
    formatNumber,
    formatPercent,
    isoDate,
} from "@/components/backtest/backtest-format";

const selectClassName =
    "w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-ring";

type BacktestFormValues = {
    symbol: string;
    from: string;
    to: string;
    strategy: BacktestStrategyType;
    fastPeriod: number;
    slowPeriod: number;
    movingAverageType: MovingAverageType;
    rsiPeriod: number;
    oversold: number;
    overbought: number;
    entryPeriod: number;
    exitPeriod: number;
    initialCapital: number;
    commissionPerTrade: number;
    commissionPercent: number;
    slippageBps: number;
};

const STRATEGY_LABELS: Record<BacktestStrategyType, string> = {
    MovingAverageCrossover: "Moving-average crossover",
    RsiMeanReversion: "RSI mean reversion",
    Breakout: "Breakout (Donchian)",
};

function defaultValues(): BacktestFormValues {
    const today = new Date();
    const fiveYearsAgo = new Date(today);
    fiveYearsAgo.setFullYear(today.getFullYear() - 5);

    return {
        symbol: "",
        from: isoDate(fiveYearsAgo),
        to: isoDate(today),
        strategy: "MovingAverageCrossover",
        fastPeriod: 50,
        slowPeriod: 200,
        movingAverageType: "Sma",
        rsiPeriod: 14,
        oversold: 30,
        overbought: 70,
        entryPeriod: 20,
        exitPeriod: 10,
        initialCapital: 10_000,
        commissionPerTrade: 0,
        commissionPercent: 0,
        slippageBps: 0,
    };
}

/** Only the chosen strategy's parameters are sent. */
function toRequest(values: BacktestFormValues): BacktestRequest {
    return {
        symbol: values.symbol.trim().toUpperCase(),
        from: values.from,
        to: values.to,
        strategy: values.strategy,
        movingAverageCrossover: values.strategy === "MovingAverageCrossover"
            ? {
                fastPeriod: values.fastPeriod,
                slowPeriod: values.slowPeriod,
                movingAverageType: values.movingAverageType,
            }
            : undefined,
        rsiMeanReversion: values.strategy === "RsiMeanReversion"
            ? {
                period: values.rsiPeriod,
                oversold: values.oversold,
                overbought: values.overbought,
            }
            : undefined,
        breakout: values.strategy === "Breakout"
            ? {
                entryPeriod: values.entryPeriod,
                exitPeriod: values.exitPeriod,
            }
            : undefined,
        initialCapital: values.initialCapital,
        commissionPerTrade: values.commissionPerTrade,
        commissionPercent: values.commissionPercent,
        slippageBps: values.slippageBps,
    };
}

function Field({
    label,
    hint,
    children,
}: {
    label: string;
    hint?: string;
    children: React.ReactNode;
}) {
    return (
        <label className="block space-y-1.5">
            <span className="block text-sm font-medium">{label}</span>
            {children}
            {hint && <span className="block text-xs text-muted-foreground">{hint}</span>}
        </label>
    );
}

type MetricRow = {
    label: string;
    value: (m: BacktestMetrics) => string;
    hint?: string;
};

const METRICS: MetricRow[] = [
    { label: "Total return", value: (m) => formatPercent(m.totalReturnPercent) },
    { label: "CAGR", value: (m) => formatPercent(m.cagrPercent) },
    {
        label: "Max drawdown",
        value: (m) => (m.maxDrawdownPercent === 0 ? "0.00%" : `−${m.maxDrawdownPercent.toFixed(2)}%`),
    },
    {
        label: "Sharpe",
        value: (m) => formatNumber(m.sharpeRatio),
        hint: "Daily returns × √252, risk-free 0",
    },
    { label: "Final equity", value: (m) => formatMoney(m.finalEquity) },
    { label: "Exposure", value: (m) => formatPercent(m.exposurePercent, false) },
    { label: "Win rate", value: (m) => formatPercent(m.winRatePercent, false), hint: "Closed trades" },
    { label: "Avg trade", value: (m) => formatPercent(m.averageTradeReturnPercent), hint: "Closed trades" },
];

function MetricCards({ result }: { result: BacktestResponse }) {
    const { metrics, benchmark } = result;

    return (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {METRICS.map((row) => (
                <Card key={row.label} size="sm">
                    <CardHeader>
                        <CardTitle className="text-xs font-medium text-muted-foreground" title={row.hint}>
                            {row.label}
                        </CardTitle>
                    </CardHeader>
                    <CardContent className="space-y-1">
                        <p className="text-lg font-semibold tabular-nums">{row.value(metrics)}</p>
                        <p className="text-xs text-muted-foreground tabular-nums">
                            Buy &amp; hold {row.value(benchmark)}
                        </p>
                    </CardContent>
                </Card>
            ))}

            <Card size="sm">
                <CardHeader>
                    <CardTitle className="text-xs font-medium text-muted-foreground">Trades</CardTitle>
                </CardHeader>
                <CardContent className="space-y-1">
                    <p className="text-lg font-semibold tabular-nums">
                        {metrics.tradeCount}
                        {metrics.hasOpenPosition && (
                            <span className="ml-1.5 text-xs font-normal text-muted-foreground">+ 1 open</span>
                        )}
                    </p>
                    <p className="text-xs text-muted-foreground">Closed round trips</p>
                </CardContent>
            </Card>

            {metrics.maxDrawdownPeakDate && metrics.maxDrawdownTroughDate && (
                <Card size="sm" className="sm:col-span-2 lg:col-span-3">
                    <CardHeader>
                        <CardTitle className="text-xs font-medium text-muted-foreground">
                            Worst drawdown
                        </CardTitle>
                    </CardHeader>
                    <CardContent>
                        <p className="text-sm">
                            Peak {formatDate(metrics.maxDrawdownPeakDate)} → trough{" "}
                            {formatDate(metrics.maxDrawdownTroughDate)}
                        </p>
                    </CardContent>
                </Card>
            )}
        </div>
    );
}

function Backtest() {
    const { register, handleSubmit, control } = useForm<BacktestFormValues>({
        defaultValues: defaultValues(),
    });

    const strategy = useWatch({ control, name: "strategy" });

    const mutation = useMutation({
        mutationFn: runBacktest,
    });

    const result = mutation.data;

    const candles = useMemo(
        () =>
            (result?.candles ?? []).map((candle) => ({
                // Daily candles are dated by trading date; the chart reads UTC midnight.
                timestamp: `${candle.date}T00:00:00Z`,
                open: candle.open,
                high: candle.high,
                low: candle.low,
                close: candle.close,
                volume: candle.volume,
            })),
        [result],
    );

    const markers = useMemo<CandleMarker[]>(
        () =>
            (result?.trades ?? []).flatMap((trade) => [
                { timestamp: `${trade.entryDate}T00:00:00Z`, side: "buy" as const, text: "Buy" },
                ...(trade.isOpen
                    ? []
                    : [{ timestamp: `${trade.exitDate}T00:00:00Z`, side: "sell" as const, text: "Sell" }]),
            ]),
        [result],
    );

    const onSubmit = handleSubmit((values) => mutation.mutate(toRequest(values)));
    const number = { valueAsNumber: true, required: true } as const;

    return (
        <div className="mx-auto max-w-7xl space-y-6">
            <div className="space-y-1">
                <h1 className="text-3xl font-semibold tracking-tight">Backtest</h1>
                <p className="text-sm text-muted-foreground">
                    Test a long-only strategy on daily candles against buy and hold. Signals use each
                    day's close and fill at the next day's open, all in with fractional shares.
                </p>
            </div>

            <form onSubmit={onSubmit}>
                <Card>
                    <CardContent className="space-y-6">
                        <div className="grid gap-4 md:grid-cols-4">
                            <Field label="Symbol">
                                <Input
                                    placeholder="AAPL"
                                    autoComplete="off"
                                    className="uppercase"
                                    disabled={mutation.isPending}
                                    {...register("symbol", { required: true })}
                                />
                            </Field>
                            <Field label="From">
                                <Input type="date" disabled={mutation.isPending} {...register("from", { required: true })} />
                            </Field>
                            <Field label="To" hint="Up to 20 years">
                                <Input type="date" disabled={mutation.isPending} {...register("to", { required: true })} />
                            </Field>
                            <Field label="Strategy">
                                <select
                                    className={selectClassName}
                                    disabled={mutation.isPending}
                                    {...register("strategy")}
                                >
                                    {Object.entries(STRATEGY_LABELS).map(([value, label]) => (
                                        <option key={value} value={value}>{label}</option>
                                    ))}
                                </select>
                            </Field>
                        </div>

                        <div className="grid gap-4 md:grid-cols-4">
                            {strategy === "MovingAverageCrossover" && (
                                <>
                                    <Field label="Fast period" hint="Days">
                                        <Input type="number" min={1} max={399} {...register("fastPeriod", number)} />
                                    </Field>
                                    <Field label="Slow period" hint="Days, longer than fast">
                                        <Input type="number" min={2} max={400} {...register("slowPeriod", number)} />
                                    </Field>
                                    <Field label="Average">
                                        <select className={selectClassName} {...register("movingAverageType")}>
                                            <option value="Sma">Simple (SMA)</option>
                                            <option value="Ema">Exponential (EMA)</option>
                                        </select>
                                    </Field>
                                </>
                            )}

                            {strategy === "RsiMeanReversion" && (
                                <>
                                    <Field label="RSI period" hint="Days">
                                        <Input type="number" min={2} max={100} {...register("rsiPeriod", number)} />
                                    </Field>
                                    <Field label="Buy below" hint="Oversold">
                                        <Input type="number" step="any" min={1} max={99} {...register("oversold", number)} />
                                    </Field>
                                    <Field label="Sell above" hint="Overbought">
                                        <Input type="number" step="any" min={1} max={99} {...register("overbought", number)} />
                                    </Field>
                                </>
                            )}

                            {strategy === "Breakout" && (
                                <>
                                    <Field label="Entry channel" hint="Buy on a close above the N-day high">
                                        <Input type="number" min={1} max={250} {...register("entryPeriod", number)} />
                                    </Field>
                                    <Field label="Exit channel" hint="Sell on a close below the M-day low">
                                        <Input type="number" min={1} max={250} {...register("exitPeriod", number)} />
                                    </Field>
                                </>
                            )}
                        </div>

                        <div className="grid gap-4 md:grid-cols-4">
                            <Field label="Initial capital" hint="USD">
                                <Input type="number" step="any" min={100} {...register("initialCapital", number)} />
                            </Field>
                            <Field label="Commission per trade" hint="USD, each buy and sell">
                                <Input type="number" step="any" min={0} {...register("commissionPerTrade", number)} />
                            </Field>
                            <Field label="Commission %" hint="Of each fill's value">
                                <Input type="number" step="any" min={0} max={5} {...register("commissionPercent", number)} />
                            </Field>
                            <Field label="Slippage" hint="Basis points against each fill">
                                <Input type="number" step="any" min={0} max={500} {...register("slippageBps", number)} />
                            </Field>
                        </div>

                        <div className="flex items-center justify-between gap-4">
                            <p className="text-xs text-muted-foreground">
                                Missing history is imported from the market data provider first, which counts
                                toward the history import limit (5 a minute).
                            </p>
                            <Button type="submit" disabled={mutation.isPending}>
                                <Play />
                                {mutation.isPending ? "Running..." : "Run backtest"}
                            </Button>
                        </div>
                    </CardContent>
                </Card>
            </form>

            {mutation.error && (
                <div className="space-y-1 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">
                    {apiErrorMessages(mutation.error, "The backtest failed.").map((message) => (
                        <p key={message}>{message}</p>
                    ))}
                </div>
            )}

            {result && (
                <div className="space-y-6">
                    <div className="space-y-1">
                        <h2 className="text-xl font-semibold">
                            {result.symbol} · {STRATEGY_LABELS[result.strategy]}
                        </h2>
                        <p className="text-sm text-muted-foreground">
                            {formatDate(result.from)} – {formatDate(result.to)} · {result.barCount} daily bars
                            {result.warmupBars > 0 && ` (first ${result.warmupBars} for indicator warm-up)`}
                            {result.dataImported && " · missing history imported"}
                        </p>
                        {(result.from !== result.requestedFrom) && (
                            <p className="text-sm text-muted-foreground">
                                Data starts on {formatDate(result.from)}; {formatDate(result.requestedFrom)} was requested.
                            </p>
                        )}
                        {result.warmupBars >= result.barCount && (
                            <p className="text-sm text-amber-600 dark:text-amber-400">
                                The range has fewer bars than the strategy's warm-up, so it can't trade. Widen the range or shorten the periods.
                            </p>
                        )}
                    </div>

                    <MetricCards result={result} />

                    <Card>
                        <CardHeader>
                            <CardTitle>Equity</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <EquityCurveChart points={result.equityCurve} />
                        </CardContent>
                    </Card>

                    <Card>
                        <CardHeader>
                            <CardTitle>Trades on the chart</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <StockCandlestickChart candles={candles} timeframe="1D" markers={markers} />
                        </CardContent>
                    </Card>

                    <Card>
                        <CardHeader>
                            <CardTitle>Trades</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <TradesTable trades={result.trades} />
                        </CardContent>
                    </Card>
                </div>
            )}
        </div>
    );
}

export default Backtest;
