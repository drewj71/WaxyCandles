import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useForm, useWatch } from "react-hook-form";
import {
    createAlert,
    deleteAlert,
    getAlerts,
    toUpdateRequest,
    updateAlert,
    type Alert,
    type UpdateAlertRequest,
} from "@/api/alerts";
import { apiErrorMessages } from "@/api/errors";
import { Route } from "@/routes/_dashboard/alerts";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";
import AlertSettingsFields from "@/components/alerts/AlertSettingsFields";
import AlertStatusBadge from "@/components/alerts/AlertStatusBadge";
import {
    emptyAlertForm,
    toAlertForm,
    toAlertSettings,
    type AlertFormValues,
} from "@/components/alerts/alert-form";

const priceFormat = new Intl.NumberFormat(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 6,
});

const dateTimeFormat = new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
});

const dateFormat = new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
});

/** "Above $597.00", or for a level alert "Approaching King $600.00 (above $597.00)". */
function describeTarget(alert: Alert): string {
    const target = `$${priceFormat.format(alert.targetPrice)}`;

    if (!alert.level) {
        return `${alert.direction} ${target}`;
    }

    const level = alert.levelPrice !== null
        ? ` $${priceFormat.format(alert.levelPrice)}`
        : "";

    return `Approaching ${alert.level}${level} (${alert.direction.toLowerCase()} ${target}, within ${alert.levelProximityPercent}%)`;
}

function describe(alert: Alert): string[] {
    const parts = [
        alert.frequency === "EveryCross"
            ? `Every cross, ${alert.cooldownMinutes} min cooldown`
            : "Once",
    ];

    if (alert.level && alert.levelAsOf) {
        parts.push(`Level as of ${dateFormat.format(new Date(alert.levelAsOf))}`);
    }

    if (alert.expiresAt) {
        parts.push(
            `${alert.status === "Expired" ? "Expired" : "Expires"} ${dateTimeFormat.format(new Date(alert.expiresAt))}`,
        );
    }

    if (alert.triggerCount > 0 && alert.triggeredAt) {
        parts.push(
            `Fired ${alert.triggerCount === 1 ? "once" : `${alert.triggerCount} times`}, last ${dateTimeFormat.format(new Date(alert.triggeredAt))}`,
        );
    }

    return parts;
}

function ErrorList({ messages }: { messages: string[] }) {
    return (
        <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {messages.map((message) => (
                <p key={message}>{message}</p>
            ))}
        </div>
    );
}

function Alerts() {
    const search = Route.useSearch();
    const navigate = Route.useNavigate();

    // Opened from a GEX page level card: start with that level and board.
    const [showCreateForm, setShowCreateForm] =
        useState(!!search.level);

    const [editingAlert, setEditingAlert] =
        useState<Alert | null>(null);

    const queryClient = useQueryClient();

    const alertsQuery = useQuery({
        queryKey: ["alerts"],
        queryFn: getAlerts,
    });

    const alerts = alertsQuery.data ?? [];

    const createForm = useForm<AlertFormValues>({
        defaultValues: search.level
            ? {
                ...emptyAlertForm,
                symbol: search.symbol ?? "",
                targetType: "Level",
                level: search.level,
                levelDays: String(search.days ?? emptyAlertForm.levelDays),
                levelStrikeRange: String(search.range ?? emptyAlertForm.levelStrikeRange),
            }
            : emptyAlertForm,
    });

    const editForm = useForm<AlertFormValues>({
        defaultValues: emptyAlertForm,
    });

    // The cooldown field only applies to repeating alerts.
    const createFrequency = useWatch({
        control: createForm.control,
        name: "frequency",
    });

    const editFrequency = useWatch({
        control: editForm.control,
        name: "frequency",
    });

    // Price alerts show target and direction; level alerts the level fields.
    const createTargetType = useWatch({
        control: createForm.control,
        name: "targetType",
    });

    const editTargetType = useWatch({
        control: editForm.control,
        name: "targetType",
    });

    function refresh() {
        return queryClient.invalidateQueries({
            queryKey: ["alerts"],
        });
    }

    const createMutation = useMutation({
        mutationFn: (values: AlertFormValues) =>
            createAlert({
                symbol: values.symbol.trim().toUpperCase(),
                ...toAlertSettings(values),
            }),
        onSuccess: async () => {
            createForm.reset(emptyAlertForm);
            setShowCreateForm(false);
            // Drop any GEX pre-fill, so a reload doesn't reopen the form.
            await navigate({ search: {} });
            await refresh();
        },
    });

    const updateMutation = useMutation({
        mutationFn: ({
            id,
            request,
        }: {
            id: string;
            request: UpdateAlertRequest;
        }) => updateAlert(id, request),
        onSuccess: async () => {
            setEditingAlert(null);
            await refresh();
        },
    });

    const deleteMutation = useMutation({
        mutationFn: deleteAlert,
        onSuccess: refresh,
    });

    function startEditing(alert: Alert) {
        updateMutation.reset();
        editForm.reset(toAlertForm(alert));
        setEditingAlert(alert);
    }

    function toggleEnabled(alert: Alert) {
        updateMutation.mutate({
            id: alert.id,
            request: {
                ...toUpdateRequest(alert),
                isEnabled: !alert.isEnabled,
            },
        });
    }

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
                        Manage your price and GEX level alerts.
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
                    onSubmit={createForm.handleSubmit((values) =>
                        createMutation.mutate(values)
                    )}
                >
                    <Card>
                        <CardHeader>
                            <CardTitle>Create Alert</CardTitle>
                        </CardHeader>

                        <CardContent className="space-y-4">
                            {createMutation.error && (
                                <ErrorList
                                    messages={apiErrorMessages(
                                        createMutation.error,
                                        "Failed to create alert.",
                                    )}
                                />
                            )}

                            <div className="grid gap-4 md:grid-cols-3">
                                <div>
                                    <label className="mb-2 block text-sm font-medium">
                                        Symbol
                                    </label>

                                    <Input
                                        placeholder="AAPL"
                                        disabled={createMutation.isPending}
                                        {...createForm.register("symbol", {
                                            required: "Symbol is required",
                                        })}
                                    />

                                    {createForm.formState.errors.symbol && (
                                        <p className="mt-1 text-sm text-destructive">
                                            {createForm.formState.errors.symbol.message}
                                        </p>
                                    )}
                                </div>

                                <AlertSettingsFields
                                    register={createForm.register}
                                    errors={createForm.formState.errors}
                                    frequency={createFrequency}
                                    targetType={createTargetType}
                                    disabled={createMutation.isPending}
                                />
                            </div>

                            {createTargetType === "Level" && (
                                <p className="text-sm text-muted-foreground">
                                    Fires when the price comes within this distance of the level, from whichever side it is on.
                                    The level is re-read from the GEX board each trading day, which moves the target and re-arms the alert.
                                </p>
                            )}

                            <div className="flex justify-end">
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

            {!editingAlert && updateMutation.error && (
                <ErrorList
                    messages={apiErrorMessages(
                        updateMutation.error,
                        "Failed to update alert.",
                    )}
                />
            )}

            <div className="rounded-lg border p-6">
                {alerts.length === 0 ? (
                    <p className="text-sm text-muted-foreground">
                        You don't have any alerts yet.
                    </p>
                ) : (
                    <div className="space-y-3">
                        {alerts.map((alert) =>
                            editingAlert?.id === alert.id ? (
                                <form
                                    key={alert.id}
                                    onSubmit={editForm.handleSubmit((values) =>
                                        updateMutation.mutate({
                                            id: alert.id,
                                            request: {
                                                ...toAlertSettings(values, alert),
                                                isEnabled: alert.isEnabled,
                                            },
                                        })
                                    )}
                                    className="space-y-4 rounded-md border p-4"
                                >
                                    {updateMutation.error && (
                                        <ErrorList
                                            messages={apiErrorMessages(
                                                updateMutation.error,
                                                "Failed to update alert.",
                                            )}
                                        />
                                    )}

                                    <div className="grid gap-4 md:grid-cols-3">
                                        <div>
                                            <label className="mb-2 block text-sm font-medium">
                                                Symbol
                                            </label>

                                            <div className="rounded-md border bg-muted/30 px-3 py-2 text-sm">
                                                {alert.symbol}
                                            </div>
                                        </div>

                                        <AlertSettingsFields
                                            register={editForm.register}
                                            errors={editForm.formState.errors}
                                            frequency={editFrequency}
                                            targetType={editTargetType}
                                            disabled={updateMutation.isPending}
                                        />
                                    </div>

                                    {alert.isTriggered && (
                                        <p className="text-sm text-muted-foreground">
                                            {editTargetType === "Level"
                                                ? "Changing the level, distance, board or repeat setting re-arms this alert."
                                                : "Changing the target, direction or repeat setting re-arms this alert."}
                                        </p>
                                    )}

                                    <div className="flex justify-end gap-2">
                                        <Button
                                            type="button"
                                            variant="outline"
                                            onClick={() =>
                                                setEditingAlert(null)
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
                                    className="flex flex-wrap items-center justify-between gap-3 rounded-md border p-4"
                                >
                                    <div className="space-y-1">
                                        <div className="flex items-center gap-2">
                                            <span className="font-medium">
                                                {alert.symbol}
                                            </span>

                                            <AlertStatusBadge status={alert.status} />
                                        </div>

                                        <div className="text-sm">
                                            {describeTarget(alert)}
                                        </div>

                                        <div className="text-xs text-muted-foreground">
                                            {describe(alert).join(" · ")}
                                        </div>
                                    </div>

                                    <div className="flex items-center gap-2">
                                        <Button
                                            variant="outline"
                                            size="sm"
                                            disabled={updateMutation.isPending}
                                            onClick={() => toggleEnabled(alert)}
                                        >
                                            {alert.isEnabled
                                                ? "Disable"
                                                : "Enable"}
                                        </Button>

                                        <Button
                                            variant="outline"
                                            size="sm"
                                            onClick={() => startEditing(alert)}
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
