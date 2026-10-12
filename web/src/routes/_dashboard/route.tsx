import {
    createFileRoute,
    redirect,
} from "@tanstack/react-router";

import DashboardLayoutRoute from "@/layouts/DashboardLayoutRoute";
import { getCurrentUser } from "@/api/auth";

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
