import { useEffect } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
    HubConnectionBuilder,
    HubConnectionState,
    LogLevel,
} from "@microsoft/signalr";
import { toast } from "sonner";

import {
    notificationReceivedEvent,
    notificationsHubUrl,
    notificationsQueryKey,
    type Notification,
} from "@/api/notifications";

const maxRetryDelayMs = 30_000;

// 1s, 2s, 4s ... capped at 30s, forever: the default policy gives up after
// four attempts, which would silently stop live alerts until a page reload.
function retryDelay(attempt: number) {
    return Math.min(maxRetryDelayMs, 1000 * 2 ** attempt);
}

/**
 * Keeps a SignalR connection open while the user is signed in, so triggered
 * alerts appear immediately: a toast, plus fresh notification and alert data.
 * Authenticated by the same httpOnly cookie as the REST API.
 */
export function useNotificationsHub() {
    const queryClient = useQueryClient();

    useEffect(() => {
        let disposed = false;
        let startTimer: ReturnType<typeof setTimeout> | undefined;

        const connection = new HubConnectionBuilder()
            .withUrl(notificationsHubUrl, { withCredentials: true })
            .withAutomaticReconnect({
                nextRetryDelayInMilliseconds: (context) =>
                    retryDelay(context.previousRetryCount),
            })
            .configureLogging(LogLevel.Warning)
            .build();

        function refresh() {
            void queryClient.invalidateQueries({
                queryKey: notificationsQueryKey,
            });
        }

        connection.on(
            notificationReceivedEvent,
            (notification: Notification) => {
                toast(notification.title, {
                    description: notification.body,
                });

                refresh();

                // A triggered alert also changes the alert list.
                void queryClient.invalidateQueries({
                    queryKey: ["alerts"],
                });
            },
        );

        // Pushes sent while disconnected are missed; the stored list isn't.
        connection.onreconnected(refresh);

        // Automatic reconnect only covers a connection that was established, so
        // retry the initial start too (e.g. API restarting when the page loads).
        async function start(attempt: number) {
            try {
                await connection.start();
                refresh();
            } catch (error) {
                if (disposed) {
                    return;
                }

                console.warn(
                    "Notifications connection failed; retrying.",
                    error,
                );

                startTimer = setTimeout(
                    () => void start(attempt + 1),
                    retryDelay(attempt),
                );
            }
        }

        void start(0);

        return () => {
            disposed = true;
            clearTimeout(startTimer);

            if (connection.state !== HubConnectionState.Disconnected) {
                void connection.stop();
            }
        };
    }, [queryClient]);
}
