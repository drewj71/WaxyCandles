import {
    createFileRoute,
} from "@tanstack/react-router";

import Stock from "@/pages/Stock";

export const Route = createFileRoute(
    "/_dashboard/stocks/$symbol",
)({
    component: Stock,
});