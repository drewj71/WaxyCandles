import { api } from "@/api/client";

export type NotificationType = "AlertTriggered";

export type Notification = {
    id: string;
    type: NotificationType;
    title: string;
    body: string;
    alertId: string | null;
    createdAt: string;
    readAt: string | null;
};

export type NotificationList = {
    items: Notification[];
    unreadCount: number;
};

export const notificationsQueryKey = ["notifications"] as const;

// The SignalR hub lives on the API's origin, outside /api.
export const notificationsHubUrl = new URL(
    "/hubs/notifications",
    api.defaults.baseURL,
).toString();

// Client method the hub calls for each new notification.
export const notificationReceivedEvent = "notificationReceived";

export async function getNotifications(): Promise<NotificationList> {
    const response =
        await api.get<NotificationList>("/notifications");

    return response.data;
}

export async function markNotificationRead(
    id: string,
): Promise<void> {
    await api.post(`/notifications/${id}/read`);
}

export async function markAllNotificationsRead(): Promise<void> {
    await api.post("/notifications/read-all");
}
