import { getRouteApi, Outlet } from "@tanstack/react-router";

import DashboardLayout from "@/layouts/DashboardLayout";
import { logout } from "@/api/auth";
import { useNotificationsHub } from "@/hooks/use-notifications-hub";

// getRouteApi reads the /_dashboard route's context without importing the
// route file, which would be a circular import.
const dashboardRoute = getRouteApi("/_dashboard");

function DashboardLayoutRoute() {
    const { user } = dashboardRoute.useRouteContext();

    // Live notifications for as long as the user is in the signed-in area.
    useNotificationsHub();

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

export default DashboardLayoutRoute;
