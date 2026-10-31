import { useEffect, useRef, useState } from "react";
import {
    createChart,
    CandlestickSeries,
    HistogramSeries,
    type IChartApi,
    type ISeriesApi,
    type UTCTimestamp,
} from "lightweight-charts";

type Candle = {
    timestamp: string;
    open: number;
    high: number;
    low: number;
    close: number;
    volume: number;
};

type StockCandlestickChartProps = {
    candles: Candle[];
    timeframe: string;
};

const easternParts = new Intl.DateTimeFormat("en-US", {
    timeZone: "America/New_York",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hourCycle: "h23",
});

/**
 * The chart's time for a candle. lightweight-charts shows every time as UTC,
 * so an intraday candle is shifted to its Eastern wall-clock time (09:30, not
 * 13:30/14:30 UTC). Daily candles are stored at UTC midnight of the trading
 * date, which already reads as that date.
 */
function chartTime(timestamp: string, intraday: boolean): UTCTimestamp {
    const date = new Date(timestamp);

    if (!intraday) {
        return Math.floor(date.getTime() / 1000) as UTCTimestamp;
    }

    const part = (type: Intl.DateTimeFormatPartTypes) =>
        Number(
            easternParts
                .formatToParts(date)
                .find((p) => p.type === type)!.value,
        );

    return (Date.UTC(
        part("year"),
        part("month") - 1,
        part("day"),
        part("hour"),
        part("minute"),
        part("second"),
    ) / 1000) as UTCTimestamp;
}

function candleLabel(timestamp: string, intraday: boolean): string {
    const date = new Date(timestamp);

    return intraday
        ? `${date.toLocaleString("en-US", {
            timeZone: "America/New_York",
            month: "short",
            day: "numeric",
            hour: "numeric",
            minute: "2-digit",
        })} ET`
        // UTC, or a daily candle reads as the day before west of Greenwich.
        : date.toLocaleDateString("en-US", { timeZone: "UTC" });
}

function StockCandlestickChart({
    candles,
    timeframe,
}: StockCandlestickChartProps) {
    const chartContainerRef =
        useRef<HTMLDivElement>(null);

    const chartRef =
        useRef<IChartApi | null>(null);

    const seriesRef =
        useRef<ISeriesApi<"Candlestick"> | null>(null);

    const volumeSeriesRef =
        useRef<ISeriesApi<"Histogram"> | null>(null);

    // The candles currently drawn, by chart time, for the crosshair tooltip.
    // A ref so the handler registered once at mount sees the latest data.
    const candlesByTimeRef =
        useRef(new Map<number, Candle>());

    const [hoveredCandle, setHoveredCandle] =
        useState<Candle | null>(null);

    const intraday = timeframe !== "1D";

    useEffect(() => {
        if (!chartContainerRef.current) {
            return;
        }

        const chart = createChart(
            chartContainerRef.current,
            {
                width:
                    chartContainerRef.current.clientWidth,
                height: 450,

                layout: {
                    background: {
                        color: "transparent",
                    },
                    textColor: "#a1a1aa",
                },

                grid: {
                    vertLines: {
                        color: "rgba(128, 128, 128, 0.15)",
                    },
                    horzLines: {
                        color: "rgba(128, 128, 128, 0.15)",
                    },
                },

                rightPriceScale: {
                    borderVisible: false,
                },

                timeScale: {
                    borderVisible: false,
                },
            },
        );

        const candlestickSeries =
            chart.addSeries(CandlestickSeries, {
                upColor: "#22c55e",
                downColor: "#ef4444",
                borderVisible: false,
                wickUpColor: "#22c55e",
                wickDownColor: "#ef4444",
            });

        const volumeSeries =
            chart.addSeries(HistogramSeries, {
                priceFormat: {
                    type: "volume",
                },
                priceScaleId: "volume",
            });

        volumeSeries.priceScale().applyOptions({
            scaleMargins: {
                top: 0.8,
                bottom: 0,
            },
        });

        chartRef.current = chart;
        seriesRef.current = candlestickSeries;
        volumeSeriesRef.current = volumeSeries;

        const handleResize = () => {
            if (!chartContainerRef.current) {
                return;
            }

            chart.applyOptions({
                width:
                    chartContainerRef.current.clientWidth,
            });
        };

        window.addEventListener(
            "resize",
            handleResize,
        );

        chart.subscribeCrosshairMove((param) => {
            if (!param.time || !param.point) {
                setHoveredCandle(null);
                return;
            }

            setHoveredCandle(
                candlesByTimeRef.current.get(Number(param.time)) ?? null,
            );
        });

        return () => {
            window.removeEventListener(
                "resize",
                handleResize,
            );

            chart.remove();

            chartRef.current = null;
            seriesRef.current = null;
        };
    }, []);

    useEffect(() => {
        if (!seriesRef.current ||
            !volumeSeriesRef.current) {
            return;
        }

        // Intraday axes need times, not just dates.
        chartRef.current?.applyOptions({
            timeScale: {
                timeVisible: intraday,
                secondsVisible: false,
            },
        });

        const times = candles.map((candle) =>
            chartTime(candle.timestamp, intraday),
        );

        candlesByTimeRef.current = new Map(
            candles.map((candle, i) => [times[i], candle]),
        );

        const chartData = candles.map((candle, i) => ({
            time: times[i],

            open: candle.open,
            high: candle.high,
            low: candle.low,
            close: candle.close,
        }));

        const volumeData = candles.map((candle, i) => ({
            time: times[i],

            value: candle.volume,

            color:
                candle.close >= candle.open
                    ? "#22c55e"
                    : "#ef4444",
        }));

        seriesRef.current.setData(chartData);
        volumeSeriesRef.current?.setData(volumeData);

        chartRef.current
            ?.timeScale()
            .fitContent();
    }, [candles, intraday]);

    return (
        <div
            ref={chartContainerRef}
            className="relative h-[450px] w-full"
        >
            {hoveredCandle && (
                <div className="pointer-events-none absolute left-3 top-3 z-10 rounded-md border bg-background/90 px-3 py-2 text-xs shadow-sm">
                    <div className="mb-1 font-medium">
                        {candleLabel(
                            hoveredCandle.timestamp,
                            intraday,
                        )}
                    </div>

                    <div className="grid grid-cols-2 gap-x-4 gap-y-1">
                        <span className="text-muted-foreground">
                            Open
                        </span>
                        <span>
                            ${hoveredCandle.open.toFixed(2)}
                        </span>

                        <span className="text-muted-foreground">
                            High
                        </span>
                        <span>
                            ${hoveredCandle.high.toFixed(2)}
                        </span>

                        <span className="text-muted-foreground">
                            Low
                        </span>
                        <span>
                            ${hoveredCandle.low.toFixed(2)}
                        </span>

                        <span className="text-muted-foreground">
                            Close
                        </span>
                        <span>
                            ${hoveredCandle.close.toFixed(2)}
                        </span>

                        <span className="text-muted-foreground">
                            Volume
                        </span>
                        <span>
                            {hoveredCandle.volume.toLocaleString()}
                        </span>
                    </div>
                </div>
            )}
        </div>
    );
}

export default StockCandlestickChart;