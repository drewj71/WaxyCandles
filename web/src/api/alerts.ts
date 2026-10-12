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

export type Alert = {
    id: string;
    symbol: string;
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
};

export type AlertSettings = {
    targetPrice: number;
    direction: AlertDirection;
    frequency: AlertFrequency;
    cooldownMinutes: number;
    expiresAt: string | null;
};

export type CreateAlertRequest = AlertSettings & {
    symbol: string;
};

/** Replaces the alert's settings: send every field, not just the changed ones. */
export type UpdateAlertRequest = AlertSettings & {
    isEnabled: boolean;
};

export const defaultCooldownMinutes = 15;

/** The settings of an existing alert, e.g. to toggle one field and PUT it back. */
export function toUpdateRequest(alert: Alert): UpdateAlertRequest {
    return {
        targetPrice: alert.targetPrice,
        direction: alert.direction,
        frequency: alert.frequency,
        cooldownMinutes: alert.cooldownMinutes,
        expiresAt: alert.expiresAt,
        isEnabled: alert.isEnabled,
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