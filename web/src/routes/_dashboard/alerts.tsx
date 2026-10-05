import {
    createFileRoute,
} from "@tanstack/react-router";

import Alerts from "@/pages/Alerts";

export const Route = createFileRoute(
    "/_dashboard/alerts",
)({
    component: Alerts,
});