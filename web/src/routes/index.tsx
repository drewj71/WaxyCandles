import {
    createFileRoute,
    redirect,
} from "@tanstack/react-router";

import { getCurrentUser } from "@/api/auth";

export const Route = createFileRoute("/")({
    beforeLoad: async () => {
        try {
            await getCurrentUser();
        } catch {
            throw redirect({
                to: "/login",
            });
        }

        throw redirect({
            to: "/dashboard",
        });
    },
});