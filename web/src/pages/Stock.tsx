import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ArrowLeft, Star } from "lucide-react";
import { Route } from "@/routes/_dashboard/stocks/$symbol";
import { getStockDetails } from "@/api/stocks";

import { Button } from "@/components/ui/button";
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";

import StockCandlestickChart from "@/components/charts/StockCandlestickChart";

function Stock() {
    const { symbol } = Route.useParams();

    const [timeframe, setTimeframe] =
        useState("1D");

    const intervalMap: Record<string, string> = {
        "1D": "OneDay",
        "1H": "OneHour",
        "30m": "ThirtyMinutes",
        "15m": "FifteenMinutes",
        "5m": "FiveMinutes",
    };

    const stockQuery = useQuery({
        queryKey: [
            "stock",
            symbol,
            timeframe,
        ],

        queryFn: () =>
            getStockDetails(
                symbol.toUpperCase(),
                intervalMap[timeframe],
            ),

        enabled: !!symbol,
    });

    const loadingTimeframe =
        stockQuery.isFetching &&
        !stockQuery.isLoading;

    if (stockQuery.isLoading) {
        return (
            <div className="flex min-h-64 items-center justify-center">
                <p className="text-sm text-muted-foreground">
                    Loading stock...
                </p>
            </div>
        );
    }

    if (stockQuery.error || !stockQuery.data || !stockQuery.data.quote) {
        return (
            <div className="space-y-4">
                <Button
                    variant="ghost"
                    render={<Link to="/watchlist" />}
                >
                    <ArrowLeft />
                    Back to Watchlist
                </Button>

                <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
                    Unable to load current market data for {symbol?.toUpperCase()}.
                </div>
            </div>
        );
    }

    const stock = stockQuery.data;

    if (!stock.quote) {
        return null;
    }

    const { quote } = stock;

    const isPositive =
        quote.change >= 0;

    return (
        <div className="mx-auto max-w-7xl space-y-6">
            {/* Header */}
            <div className="flex items-start justify-between gap-4">
                <div className="space-y-1">
                    <Button
                        variant="ghost"
                        size="sm"
                        className="-ml-3 mb-2"
                        render={<Link to="/watchlist" />}
                    >
                        <ArrowLeft />
                        Watchlist
                    </Button>

                    <div className="flex items-center gap-3">
                        <h1 className="text-3xl font-semibold tracking-tight">
                            {stock.symbol}
                        </h1>

                        <Button variant="outline" size="icon">
                            <Star />
                            <span className="sr-only">
                                Add to watchlist
                            </span>
                        </Button>
                    </div>

                    <p className="text-sm text-muted-foreground">
                        Market data
                    </p>
                </div>

                <div className="text-right">
                    <div className="text-3xl font-semibold">
                        ${quote.price.toFixed(2)}
                    </div>

                    <div
                        className={
                            isPositive
                                ? "text-sm text-green-600 dark:text-green-400"
                                : "text-sm text-red-600 dark:text-red-400"
                        }
                    >
                        {isPositive ? "+" : ""}
                        {quote.change.toFixed(2)}
                        {" ("}
                        {isPositive ? "+" : ""}
                        {quote.changePercent.toFixed(2)}
                        {"%)"}
                    </div>
                </div>
            </div>

            {/* Timeframe buttons */}
            <div className="flex gap-1">
                {["1D", "1H", "30m", "15m", "5m"].map(
                    (option) => (
                        <Button
                            key={option}
                            variant={
                                timeframe === option
                                    ? "secondary"
                                    : "ghost"
                            }
                            size="sm"
                            onClick={() =>
                                setTimeframe(option)
                            }
                        >
                            {option}
                        </Button>
                    ),
                )}
            </div>

            {/* Price Chart */}
            <Card>
                <CardContent>
                    {loadingTimeframe ? (
                        <div className="flex h-[450px] items-center justify-center">
                            <p className="text-sm text-muted-foreground">
                                Loading {timeframe} data...
                            </p>
                        </div>
                    ) : stock.historicalCandles.length > 0 ? (
                        <StockCandlestickChart
                            candles={stock.historicalCandles}
                            timeframe={timeframe}
                        />
                    ) : (
                        <div className="flex h-[450px] items-center justify-center">
                            <p className="text-sm text-muted-foreground">
                                No historical data available.
                            </p>
                        </div>
                    )}
                </CardContent>
            </Card>

            {/* Statistics */}
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                <Card>
                    <CardHeader>
                        <CardTitle className="text-sm font-medium">
                            Previous Close
                        </CardTitle>
                    </CardHeader>

                    <CardContent>
                        <p className="text-xl font-semibold">
                            ${quote.previousClose.toFixed(2)}
                        </p>
                    </CardContent>
                </Card>

                <Card>
                    <CardHeader>
                        <CardTitle className="text-sm font-medium">
                            Open
                        </CardTitle>
                    </CardHeader>

                    <CardContent>
                        <p className="text-xl font-semibold">
                            {stock.historicalCandles.length > 0
                                ? `$${stock.historicalCandles.at(-1)!.open.toFixed(2)}`
                                : "—"}
                        </p>
                    </CardContent>
                </Card>

                <Card>
                    <CardHeader>
                        <CardTitle className="text-sm font-medium">
                            High
                        </CardTitle>
                    </CardHeader>

                    <CardContent>
                        <p className="text-xl font-semibold">
                            {stock.historicalCandles.length > 0
                                ? `$${Math.max(
                                    ...stock.historicalCandles.map(
                                        (candle) =>
                                            candle.high,
                                    ),
                                ).toFixed(2)}`
                                : "—"}
                        </p>
                    </CardContent>
                </Card>

                <Card>
                    <CardHeader>
                        <CardTitle className="text-sm font-medium">
                            Volume
                        </CardTitle>
                    </CardHeader>

                    <CardContent>
                        <p className="text-xl font-semibold">
                            {quote.volume.toLocaleString()}
                        </p>
                    </CardContent>
                </Card>
            </div>
        </div>
    );
}

export default Stock;