import { useEffect, useState } from "react";

import {
    createAlert,
    getAlerts,
    type Alert,
} from "@/api/alerts";
import { Button } from "@/components/ui/button";

function Alerts() {
    const [alerts, setAlerts] =
        useState<Alert[]>([]);

    const [loading, setLoading] =
        useState(true);

    const [error, setError] =
        useState("");

    const [showCreateForm, setShowCreateForm] =
        useState(false);

    const [symbol, setSymbol] =
        useState("");

    const [targetPrice, setTargetPrice] =
        useState("");

    const [direction, setDirection] =
        useState<"Above" | "Below">("Above");

    const [creating, setCreating] =
        useState(false);

    useEffect(() => {
        async function loadAlerts() {
            try {
                setLoading(true);
                setError("");

                const data =
                    await getAlerts();

                setAlerts(data);
            } catch (err) {
                console.error(err);

                setError(
                    "Failed to load alerts.",
                );
            } finally {
                setLoading(false);
            }
        }

        loadAlerts();
    }, []);

    if (loading) {
        return (
            <div className="flex min-h-64 items-center justify-center">
                <p className="text-sm text-muted-foreground">
                    Loading alerts...
                </p>
            </div>
        );
    }

    if (error) {
        return (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
                {error}
            </div>
        );
    }

    return (
        <div className="mx-auto max-w-7xl space-y-6">
            <div className="flex items-start justify-between gap-4">
                <div>
                    <h1 className="text-3xl font-semibold tracking-tight">
                        Alerts
                    </h1>

                    <p className="text-sm text-muted-foreground">
                        Manage your price alerts.
                    </p>
                </div>

                <Button
                    onClick={() =>
                        setShowCreateForm(!showCreateForm)
                    }
                >
                    Create Alert
                </Button>
            </div>

            {showCreateForm && (
                <div className="rounded-lg border p-6">
                    <h2 className="mb-4 text-lg font-semibold">
                        Create Alert
                    </h2>

                    <div className="grid gap-4 md:grid-cols-3">
                        <div>
                            <label className="mb-2 block text-sm font-medium">
                                Symbol
                            </label>

                            <input
                                value={symbol}
                                onChange={(e) =>
                                    setSymbol(e.target.value.toUpperCase())
                                }
                                placeholder="AAPL"
                                className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-ring"
                            />
                        </div>

                        <div>
                            <label className="mb-2 block text-sm font-medium">
                                Target Price
                            </label>

                            <input
                                type="number"
                                step="0.01"
                                value={targetPrice}
                                onChange={(e) =>
                                    setTargetPrice(e.target.value)
                                }
                                placeholder="250.00"
                                className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-ring"
                            />
                        </div>

                        <div>
                            <label className="mb-2 block text-sm font-medium">
                                Direction
                            </label>

                            <select
                                value={direction}
                                onChange={(e) =>
                                    setDirection(
                                        e.target.value as "Above" | "Below",
                                    )
                                }
                                className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-ring"
                            >
                                <option value="Above">
                                    Above
                                </option>

                                <option value="Below">
                                    Below
                                </option>
                            </select>
                        </div>
                    </div>

                    <div className="mt-4 flex justify-end">
                        <Button
                            disabled={creating}
                            onClick={async () => {
                                try {
                                    setCreating(true);
                                    setError("");

                                    const created = await createAlert({
                                        symbol: symbol.trim(),
                                        targetPrice: Number(targetPrice),
                                        direction,
                                    });

                                    setAlerts((current) => [
                                        created,
                                        ...current,
                                    ]);

                                    setSymbol("");
                                    setTargetPrice("");
                                    setDirection("Above");
                                    setShowCreateForm(false);
                                } catch (err) {
                                    console.error(err);

                                    setError(
                                        "Failed to create alert.",
                                    );
                                } finally {
                                    setCreating(false);
                                }
                            }}
                        >
                            {creating ? "Creating..." : "Create Alert"}
                        </Button>
                    </div>
                </div>
            )}

            <div className="rounded-lg border p-6">
                {alerts.length === 0 ? (
                    <p className="text-sm text-muted-foreground">
                        You don't have any alerts yet.
                    </p>
                ) : (
                    <div className="space-y-3">
                        {alerts.map((alert) => (
                            <div
                                key={alert.id}
                                className="flex items-center justify-between rounded-md border p-4"
                            >
                                <div>
                                    <div className="font-medium">
                                        {alert.symbol}
                                    </div>

                                    <div className="text-sm text-muted-foreground">
                                        {alert.direction} $
                                        {alert.targetPrice.toFixed(2)}
                                    </div>
                                </div>

                                <div className="text-sm">
                                    {alert.isTriggered
                                        ? "Triggered"
                                        : "Active"}
                                </div>
                            </div>
                        ))}
                    </div>
                )}
            </div>
        </div>
    );
}

export default Alerts;