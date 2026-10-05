import {
    createFileRoute,
    Outlet,
    redirect,
} from "@tanstack/react-router";

import DashboardLayout from "@/layouts/DashboardLayout";
import { getCurrentUser, logout } from "@/api/auth";

export const Route = createFileRoute("/_dashboard")({
    beforeLoad: async () => {
        try {
            const user = await getCurrentUser();

            return {
                user,
            };
        } catch {
            throw redirect({
                to: "/login",
            });
        }
    },

    component: DashboardLayoutRoute,
});

function DashboardLayoutRoute() {
    const { user } = Route.useRouteContext();

    async function handleLogout() {
        try {
            await logout();
        } finally {
            window.location.href = "/login";
        }
    }

    return (
        <DashboardLayout
            userEmail={user.email}
            onLogout={handleLogout}
        >
            <Outlet />
        </DashboardLayout>
    );
}