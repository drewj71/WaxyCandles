import { api } from "@/api/client";

export type AlertDirection =
    | "Above"
    | "Below";

export type Alert = {
    id: string;
    symbol: string;
    targetPrice: number;
    direction: AlertDirection;
    isTriggered: boolean;
    createdAt: string;
};

export type CreateAlertRequest = {
    symbol: string;
    targetPrice: number;
    direction: AlertDirection;
};

export type UpdateAlertRequest = {
    targetPrice: number;
    direction: AlertDirection;
};

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