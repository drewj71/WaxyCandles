import {
    createFileRoute,
} from "@tanstack/react-router";

import Alerts from "@/pages/Alerts";
import { parseAlertsSearch } from "@/components/alerts/alerts-search";

export const Route = createFileRoute(
    "/_dashboard/alerts",
)({
    validateSearch: parseAlertsSearch,
    component: Alerts,
});
