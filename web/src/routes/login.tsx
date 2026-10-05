import {
    createFileRoute,
    redirect,
} from "@tanstack/react-router";

import Login from "@/pages/Login";
import { getCurrentUser } from "@/api/auth";

export const Route = createFileRoute("/login")({
    beforeLoad: async () => {
        try {
            await getCurrentUser();
        } catch {
            return;
        }

        throw redirect({
            to: "/dashboard",
        });
    },

    component: Login,
});