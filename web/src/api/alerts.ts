import { api } from "@/api/client";

export type AlertDirection =
    | "Above"
    | "Below";

/** Once: fires one time. EveryCross: fires each time the price crosses the target. */
export type AlertFrequency =
    | "Once"
    | "EveryCross";

/** Computed by the API from the alert's state. */
export type AlertStatus =
    | "Active"
    | "Triggered"
    | "Disabled"
    | "Expired";

/** A named node on a symbol's GEX board, as an alert target. */
export type GexLevelKind =
    | "King"
    | "Gatekeeper"
    | "Floor"
    | "Ceiling";

export const gexLevelKinds: GexLevelKind[] = ["King", "Gatekeeper", "Floor", "Ceiling"];

export type Alert = {
    id: string;
    symbol: string;
    /** For a level alert, derived from the level each trading day. */
    targetPrice: number;
    direction: AlertDirection;
    frequency: AlertFrequency;
    cooldownMinutes: number;
    isEnabled: boolean;
    isTriggered: boolean;
    status: AlertStatus;
    createdAt: string;
    expiresAt: string | null;
    /** When it last fired. */
    triggeredAt: string | null;
    triggerCount: number;
    /** Set for a GEX level alert; null for a price alert. */
    level: GexLevelKind | null;
    levelProximityPercent: number | null;
    levelDays: number | null;
    levelStrikeRange: number | null;
    /** The level's strike when the target was last resolved. */
    levelPrice: number | null;
    /** The option chain snapshot the level came from. */
    levelAsOf: string | null;
};

export type AlertSettings = {
    /** Ignored for a level alert. */
    targetPrice: number;
    /** Ignored for a level alert. */
    direction: AlertDirection;
    frequency: AlertFrequency;
    cooldownMinutes: number;
    expiresAt: string | null;
    level: GexLevelKind | null;
    levelProximityPercent?: number;
    levelDays?: number;
    levelStrikeRange?: number;
};

export type CreateAlertRequest = AlertSettings & {
    symbol: string;
};

/** Replaces the alert's settings: send every field, not just the changed ones. */
export type UpdateAlertRequest = AlertSettings & {
    isEnabled: boolean;
};

export const defaultCooldownMinutes = 15;

export const defaultLevelProximityPercent = 0.5;

/** The settings of an existing alert, e.g. to toggle one field and PUT it back. */
export function toUpdateRequest(alert: Alert): UpdateAlertRequest {
    return {
        targetPrice: alert.targetPrice,
        direction: alert.direction,
        frequency: alert.frequency,
        cooldownMinutes: alert.cooldownMinutes,
        expiresAt: alert.expiresAt,
        isEnabled: alert.isEnabled,
        level: alert.level,
        levelProximityPercent: alert.levelProximityPercent ?? undefined,
        levelDays: alert.levelDays ?? undefined,
        levelStrikeRange: alert.levelStrikeRange ?? undefined,
    };
}

export async function getAlerts(): Promise<Alert[]> {
    const response =
        await api.get<Alert[]>("/alerts");

    return response.data;
}

export async function createAlert(
    request: CreateAlertRequest,
): Promise<Alert> {
    const response =
        await api.post<Alert>(
            "/alerts",
            request,
        );

    return response.data;
}

export async function updateAlert(
    id: string,
    request: UpdateAlertRequest,
): Promise<Alert> {
    const response =
        await api.put<Alert>(
            `/alerts/${id}`,
            request,
        );

    return response.data;
}

export async function deleteAlert(
    id: string,
): Promise<void> {
    await api.delete(`/alerts/${id}`);
}
