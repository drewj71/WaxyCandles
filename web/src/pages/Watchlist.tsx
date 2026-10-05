import {
    useEffect,
    useState,
    type KeyboardEvent,
} from "react";
import { Plus, Star, Trash2 } from "lucide-react";

import {
    addToWatchlist,
    getWatchlistQuotes,
    removeFromWatchlist,
    type WatchlistStock,
} from "@/api/watchlist";

import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";

import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from "@/components/ui/table";
import { Link } from "react-router-dom";

function Watchlist() {
    const [stocks, setStocks] = useState<
        WatchlistStock[]
    >([]);

    const [symbol, setSymbol] = useState("");
    const [loading, setLoading] = useState(true);
    const [adding, setAdding] = useState(false);
    const [error, setError] = useState("");

    async function loadWatchlist() {
        try {
            setError("");

            const data = await getWatchlistQuotes();

            setStocks(data);
        } catch (err) {
            console.error(err);
            setError(
                "Failed to load your watchlist.",
            );
        } finally {
            setLoading(false);
        }
    }

    useEffect(() => {
        loadWatchlist();
    }, []);

    async function handleAdd() {
        const normalizedSymbol =
            symbol.trim().toUpperCase();

        if (!normalizedSymbol) {
            return;
        }

        try {
            setAdding(true);
            setError("");

            await addToWatchlist(
                normalizedSymbol,
            );

            setSymbol("");

            await loadWatchlist();
        } catch (err) {
            console.error(err);
            setError(
                "Failed to add symbol to watchlist.",
            );
        } finally {
            setAdding(false);
        }
    }

    async function handleRemove(
        stockSymbol: string,
    ) {
        try {
            setError("");

            await removeFromWatchlist(
                stockSymbol,
            );

            setStocks((current) =>
                current.filter(
                    (stock) =>
                        stock.symbol !== stockSymbol,
                ),
            );
        } catch (err) {
            console.error(err);
            setError(
                "Failed to remove symbol.",
            );
        }
    }

    function handleKeyDown(
        event: KeyboardEvent<HTMLInputElement>,
    ) {
        if (event.key === "Enter") {
            handleAdd();
        }
    }

    return (
        <div className="mx-auto max-w-7xl space-y-6">
            <div>
                <h1 className="text-3xl font-semibold tracking-tight">
                    Watchlist
                </h1>

                <p className="mt-1 text-muted-foreground">
                    Track the stocks you're watching.
                </p>
            </div>

            <Card>
                <CardHeader>
                    <CardTitle className="text-base">
                        Add Symbol
                    </CardTitle>
                </CardHeader>

                <CardContent>
                    <div className="flex max-w-md gap-2">
                        <Input
                            placeholder="Enter symbol (e.g. AAPL)"
                            value={symbol}
                            onChange={(event) =>
                                setSymbol(event.target.value)
                            }
                            onKeyDown={handleKeyDown}
                            disabled={adding}
                        />

                        <Button
                            onClick={handleAdd}
                            disabled={
                                adding ||
                                !symbol.trim()
                            }
                        >
                            <Plus />
                            {adding ? "Adding..." : "Add"}
                        </Button>
                    </div>
                </CardContent>
            </Card>

            {error && (
                <div className="rounded-lg border border-destructive/50 bg-destructive/10 px-4 py-3 text-sm text-destructive">
                    {error}
                </div>
            )}

            <Card>
                <CardHeader>
                    <CardTitle className="flex items-center gap-2 text-base">
                        <Star className="size-4" />
                        Your Stocks
                    </CardTitle>
                </CardHeader>

                <CardContent className="p-0">
                    {loading ? (
                        <div className="flex h-32 items-center justify-center">
                            <p className="text-sm text-muted-foreground">
                                Loading watchlist...
                            </p>
                        </div>
                    ) : stocks.length === 0 ? (
                        <div className="flex h-48 flex-col items-center justify-center px-6 text-center">
                            <Star className="size-8 text-muted-foreground" />

                            <p className="mt-3 font-medium">
                                Your watchlist is empty
                            </p>

                            <p className="mt-1 text-sm text-muted-foreground">
                                Add a stock above to start tracking it.
                            </p>
                        </div>
                    ) : (
                        <div className="overflow-x-auto">
                            <Table>
                                <TableHeader>
                                    <TableRow>
                                        <TableHead>
                                            Symbol
                                        </TableHead>

                                        <TableHead className="text-right">
                                            Price
                                        </TableHead>

                                        <TableHead className="text-right">
                                            Change
                                        </TableHead>

                                        <TableHead className="text-right">
                                            Change %
                                        </TableHead>

                                        <TableHead className="w-12" />
                                    </TableRow>
                                </TableHeader>

                                <TableBody>
                                    {stocks.map((stock) => (
                                        <TableRow
                                            key={stock.symbol}
                                        >
                                            <TableCell className="font-semibold">
                                                <Link
                                                    to={`/stocks/${stock.symbol}`}
                                                    className="hover:underline"
                                                >
                                                    {stock.symbol}
                                                </Link>
                                            </TableCell>

                                            <TableCell className="text-right">
                                                ${stock.price.toFixed(2)}
                                            </TableCell>

                                            <TableCell className="text-right">
                                                {stock.change >= 0
                                                    ? "+"
                                                    : ""}
                                                {stock.change.toFixed(2)}
                                            </TableCell>

                                            <TableCell className="text-right">
                                                {stock.changePercent >= 0
                                                    ? "+"
                                                    : ""}
                                                {stock.changePercent.toFixed(
                                                    2,
                                                )}
                                                %
                                            </TableCell>

                                            <TableCell>
                                                <Button
                                                    variant="ghost"
                                                    size="icon"
                                                    onClick={() =>
                                                        handleRemove(
                                                            stock.symbol,
                                                        )
                                                    }
                                                >
                                                    <Trash2 />
                                                    <span className="sr-only">
                                                        Remove{" "}
                                                        {stock.symbol}
                                                    </span>
                                                </Button>
                                            </TableCell>
                                        </TableRow>
                                    ))}
                                </TableBody>
                            </Table>
                        </div>
                    )}
                </CardContent>
            </Card>
        </div>
    );
}

export default Watchlist;