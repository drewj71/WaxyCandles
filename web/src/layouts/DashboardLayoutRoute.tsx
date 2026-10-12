import { getRouteApi, Outlet } from "@tanstack/react-router";

import DashboardLayout from "@/layouts/DashboardLayout";
import { logout } from "@/api/auth";

// getRouteApi reads the /_dashboard route's context without importing the
// route file, which would be a circular import.
const dashboardRoute = getRouteApi("/_dashboard");

function DashboardLayoutRoute() {
    const { user } = dashboardRoute.useRouteContext();

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
