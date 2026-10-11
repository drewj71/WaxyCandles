import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import {
    createAlert,
    deleteAlert,
    getAlerts,
    updateAlert,
} from "@/api/alerts";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";

function Alerts() {
    const [showCreateForm, setShowCreateForm] =
        useState(false);

    const [editingAlertId, setEditingAlertId] =
        useState<string | null>(null);

    const queryClient = useQueryClient();

    const alertsQuery = useQuery({
        queryKey: ["alerts"],
        queryFn: getAlerts,
    });

    const alerts = alertsQuery.data ?? [];

    type CreateAlertFormValues = {
        symbol: string;
        targetPrice: string;
        direction: "Above" | "Below";
    };

    const {
        register,
        handleSubmit,
        reset,
        formState: { errors },
    } = useForm<CreateAlertFormValues>({
        defaultValues: {
            symbol: "",
            targetPrice: "",
            direction: "Above",
        },
    });

    type EditAlertFormValues = {
        targetPrice: string;
        direction: "Above" | "Below";
    };

    const {
        register: registerEdit,
        handleSubmit: handleEditSubmit,
        reset: resetEdit,
    } = useForm<EditAlertFormValues>({
        defaultValues: {
            targetPrice: "",
            direction: "Above",
        },
    });

    const createMutation = useMutation({
        mutationFn: (data: CreateAlertFormValues) =>
            createAlert({
                symbol: data.symbol.trim().toUpperCase(),
                targetPrice: Number(data.targetPrice),
                direction: data.direction,
            }),
        onSuccess: async () => {
            reset();
            setShowCreateForm(false);

            await queryClient.invalidateQueries({
                queryKey: ["alerts"],
            });
        },
    });

    const updateMutation = useMutation({
        mutationFn: ({
            id,
            targetPrice,
            direction,
        }: {
            id: string;
            targetPrice: number;
            direction: "Above" | "Below";
        }) =>
            updateAlert(id, {
                targetPrice,
                direction,
            }),

        onSuccess: async () => {
            setEditingAlertId(null);

            await queryClient.invalidateQueries({
                queryKey: ["alerts"],
            });
        },

        onError: (error) => {
            console.error("Failed to update alert:", error);
        },
    });

    const deleteMutation = useMutation({
        mutationFn: deleteAlert,
        onSuccess: async () => {
            await queryClient.invalidateQueries({
                queryKey: ["alerts"],
            });
        },
    });

    const createError = createMutation.error
        ? "Failed to create alert."
        : "";

    if (alertsQuery.isLoading) {
        return (
            <div className="flex min-h-64 items-center justify-center">
                <p className="text-sm text-muted-foreground">
                    Loading alerts...
                </p>
            </div>
        );
    }

    if (alertsQuery.error) {
        return (
            <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
                Failed to load alerts.
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
                <form
                    onSubmit={handleSubmit((data) =>
                        createMutation.mutate(data)
                    )}
                >
                    <Card>
                        <CardHeader>
                            <CardTitle>Create Alert</CardTitle>
                        </CardHeader>

                        <CardContent>
                            <div className="grid gap-4 md:grid-cols-3">
                                {createError && (
                                    <div className="mb-4 rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
                                        {createError}
                                    </div>
                                )}
                                <div>
                                    <label className="mb-2 block text-sm font-medium">
                                        Symbol
                                    </label>

                                    <Input
                                        placeholder="AAPL"
                                        {...register("symbol", {
                                            required: "Symbol is required",
                                        })} disabled={createMutation.isPending}
                                    />
                                    {errors.symbol && (
                                        <p className="mt-1 text-sm text-destructive">
                                            {errors.symbol.message}
                                        </p>
                                    )}
                                </div>

                                <div>
                                    <label className="mb-2 block text-sm font-medium">
                                        Target Price
                                    </label>

                                    <Input
                                        type="number"
                                        step="0.01"
                                        placeholder="250.00"
                                        {...register("targetPrice", {
                                            required: "Target price is required",
                                            validate: (value) =>
                                                Number(value) > 0 ||
                                                "Target price must be greater than 0",
                                        })} disabled={createMutation.isPending}
                                    />
                                    {errors.targetPrice && (
                                        <p className="mt-1 text-sm text-destructive">
                                            {errors.targetPrice.message}
                                        </p>
                                    )}
                                </div>

                                <div>
                                    <label className="mb-2 block text-sm font-medium">
                                        Direction
                                    </label>

                                    <select
                                        {...register("direction")}
                                        disabled={createMutation.isPending}
                                        className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-ring"
                                    >
                                        <option value="Above">Above</option>
                                        <option value="Below">Below</option>
                                    </select>
                                </div>
                            </div>

                            <div className="mt-4 flex justify-end">
                                <Button
                                    type="submit"
                                    disabled={createMutation.isPending}
                                >
                                    {createMutation.isPending
                                        ? "Creating..."
                                        : "Create Alert"}
                                </Button>
                            </div>
                        </CardContent>
                    </Card>
                </form>
            )}

            <div className="rounded-lg border p-6">
                {alerts.length === 0 ? (
                    <p className="text-sm text-muted-foreground">
                        You don't have any alerts yet.
                    </p>
                ) : (
                    <div className="space-y-3">
                        {alerts.map((alert) =>
                            editingAlertId === alert.id ? (
                                <form
                                    key={alert.id}
                                    onSubmit={handleEditSubmit((data) => {
                                        updateMutation.mutate({
                                            id: alert.id,
                                            targetPrice: Number(data.targetPrice),
                                            direction: data.direction,
                                        });
                                    })}
                                    className="rounded-md border p-4"
                                >
                                    <div className="grid gap-4 md:grid-cols-3">
                                        <div>
                                            <label className="mb-2 block text-sm font-medium">
                                                Symbol
                                            </label>

                                            <div className="rounded-md border bg-muted/30 px-3 py-2 text-sm">
                                                {alert.symbol}
                                            </div>
                                        </div>

                                        <div>
                                            <label className="mb-2 block text-sm font-medium">
                                                Target Price
                                            </label>

                                            <Input
                                                type="number"
                                                step="0.01"
                                                {...registerEdit("targetPrice", {
                                                    required: "Target price is required",
                                                    validate: (value) =>
                                                        Number(value) > 0 ||
                                                        "Target price must be greater than 0",
                                                })}
                                                disabled={updateMutation.isPending}
                                            />
                                        </div>

                                        <div>
                                            <label className="mb-2 block text-sm font-medium">
                                                Direction
                                            </label>

                                            <select
                                                {...registerEdit("direction")}
                                                disabled={updateMutation.isPending}
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

                                    <div className="mt-4 flex justify-end gap-2">
                                        <Button
                                            type="button"
                                            variant="outline"
                                            onClick={() =>
                                                setEditingAlertId(null)
                                            }
                                            disabled={updateMutation.isPending}
                                        >
                                            Cancel
                                        </Button>

                                        <Button
                                            type="submit"
                                            disabled={updateMutation.isPending}
                                        >
                                            {updateMutation.isPending
                                                ? "Saving..."
                                                : "Save"}
                                        </Button>
                                    </div>
                                </form>
                            ) : (
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

                                    <div className="flex items-center gap-3">
                                        <div className="text-sm">
                                            {alert.isTriggered
                                                ? "Triggered"
                                                : "Active"}
                                        </div>

                                        <Button
                                            variant="outline"
                                            size="sm"
                                            onClick={() => {
                                                setEditingAlertId(alert.id);

                                                resetEdit({
                                                    targetPrice:
                                                        alert.targetPrice.toString(),
                                                    direction: alert.direction,
                                                });
                                            }}
                                        >
                                            Edit
                                        </Button>

                                        <Button
                                            variant="destructive"
                                            size="sm"
                                            disabled={deleteMutation.isPending}
                                            onClick={() =>
                                                deleteMutation.mutate(alert.id)
                                            }
                                        >
                                            Delete
                                        </Button>
                                    </div>
                                </div>
                            ),
                        )}
                    </div>
                )}
            </div>
        </div>
    );
}

export default Alerts;