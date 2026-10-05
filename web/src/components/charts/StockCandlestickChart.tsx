import { useEffect, useRef, useState } from "react";
import {
    createChart,
    CandlestickSeries,
    HistogramSeries,
    type IChartApi,
    type ISeriesApi,
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

    const [hoveredCandle, setHoveredCandle] =
        useState<Candle | null>(null);

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

            const candle = candles.find(
                (candle) =>
                    Math.floor(
                        new Date(candle.timestamp).getTime() / 1000,
                    ) === Number(param.time),
            );

            setHoveredCandle(candle ?? null);
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

        if (timeframe !== "1D") {
            seriesRef.current.setData([]);
            volumeSeriesRef.current.setData([]);
            return;
        }

        const chartData = candles.map((candle) => ({
            time: Math.floor(
                new Date(candle.timestamp).getTime() /
                1000,
            ) as any,

            open: candle.open,
            high: candle.high,
            low: candle.low,
            close: candle.close,
        }));

        const volumeData = candles.map((candle) => ({
            time: Math.floor(
                new Date(candle.timestamp).getTime() /
                1000,
            ) as any,

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
    }, [candles, timeframe]);

    return (
        <div
            ref={chartContainerRef}
            className="relative h-[450px] w-full"
        >
            {hoveredCandle && (
                <div className="pointer-events-none absolute left-3 top-3 z-10 rounded-md border bg-background/90 px-3 py-2 text-xs shadow-sm">
                    <div className="mb-1 font-medium">
                        {new Date(
                            hoveredCandle.timestamp,
                        ).toLocaleDateString()}
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