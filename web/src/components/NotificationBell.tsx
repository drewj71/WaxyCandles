import { Bell } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";

import {
    getNotifications,
    markAllNotificationsRead,
    markNotificationRead,
    notificationsQueryKey,
    type Notification,
} from "@/api/notifications";
import { Button } from "@/components/ui/button";
import {
    DropdownMenu,
    DropdownMenuContent,
    DropdownMenuGroup,
    DropdownMenuItem,
    DropdownMenuLabel,
    DropdownMenuSeparator,
    DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

const relativeTime = new Intl.RelativeTimeFormat(undefined, {
    numeric: "auto",
});

function timeAgo(iso: string) {
    const seconds = Math.round((new Date(iso).getTime() - Date.now()) / 1000);
    const units: [Intl.RelativeTimeFormatUnit, number][] = [
        ["day", 86_400],
        ["hour", 3_600],
        ["minute", 60],
    ];

    for (const [unit, size] of units) {
        if (Math.abs(seconds) >= size) {
            return relativeTime.format(Math.round(seconds / size), unit);
        }
    }

    return relativeTime.format(seconds, "second");
}

function NotificationBell() {
    const queryClient = useQueryClient();
    const navigate = useNavigate();

    const notificationsQuery = useQuery({
        queryKey: notificationsQueryKey,
        queryFn: getNotifications,
    });

    const items = notificationsQuery.data?.items ?? [];
    const unreadCount = notificationsQuery.data?.unreadCount ?? 0;

    function refresh() {
        return queryClient.invalidateQueries({
            queryKey: notificationsQueryKey,
        });
    }

    const markReadMutation = useMutation({
        mutationFn: markNotificationRead,
        onSuccess: refresh,
    });

    const markAllReadMutation = useMutation({
        mutationFn: markAllNotificationsRead,
        onSuccess: refresh,
    });

    function open(notification: Notification) {
        if (!notification.readAt) {
            markReadMutation.mutate(notification.id);
        }

        if (notification.alertId) {
            void navigate({ to: "/alerts" });
        }
    }

    return (
        <DropdownMenu>
            <DropdownMenuTrigger
                render={
                    <Button
                        variant="ghost"
                        size="icon"
                        className="relative"
                        aria-label={
                            unreadCount > 0
                                ? `Notifications, ${unreadCount} unread`
                                : "Notifications"
                        }
                    />
                }
            >
                <Bell />

                {unreadCount > 0 && (
                    <span className="absolute -top-0.5 -right-0.5 flex min-w-4 items-center justify-center rounded-full bg-destructive px-1 text-[10px] font-medium leading-4 text-white">
                        {unreadCount > 9 ? "9+" : unreadCount}
                    </span>
                )}
            </DropdownMenuTrigger>

            <DropdownMenuContent align="end" className="w-80">
                <DropdownMenuGroup>
                    <div className="flex items-center justify-between">
                        <DropdownMenuLabel>
                            Notifications
                        </DropdownMenuLabel>

                        {unreadCount > 0 && (
                            <Button
                                variant="link"
                                size="sm"
                                onClick={() => markAllReadMutation.mutate()}
                                disabled={markAllReadMutation.isPending}
                            >
                                Mark all read
                            </Button>
                        )}
                    </div>
                </DropdownMenuGroup>

                <DropdownMenuSeparator />

                {notificationsQuery.isError && (
                    <p className="px-2 py-6 text-center text-sm text-muted-foreground">
                        Couldn't load notifications.
                    </p>
                )}

                {!notificationsQuery.isError && items.length === 0 && (
                    <p className="px-2 py-6 text-center text-sm text-muted-foreground">
                        No notifications yet. Triggered alerts show up here.
                    </p>
                )}

                <DropdownMenuGroup className="max-h-96 overflow-y-auto">
                    {items.map((notification) => (
                        <DropdownMenuItem
                            key={notification.id}
                            onClick={() => open(notification)}
                            className="flex items-start gap-2 py-2"
                        >
                            <span
                                aria-hidden
                                className={`mt-1.5 size-2 shrink-0 rounded-full ${
                                    notification.readAt
                                        ? "bg-transparent"
                                        : "bg-primary"
                                }`}
                            />

                            <span className="flex min-w-0 flex-col gap-0.5">
                                <span
                                    className={
                                        notification.readAt
                                            ? "text-sm"
                                            : "text-sm font-semibold"
                                    }
                                >
                                    {notification.title}
                                </span>

                                <span className="text-xs text-muted-foreground">
                                    {notification.body}
                                </span>

                                <span className="text-xs text-muted-foreground">
                                    {timeAgo(notification.createdAt)}
                                </span>
                            </span>
                        </DropdownMenuItem>
                    ))}
                </DropdownMenuGroup>
            </DropdownMenuContent>
        </DropdownMenu>
    );
}

export default NotificationBell;
