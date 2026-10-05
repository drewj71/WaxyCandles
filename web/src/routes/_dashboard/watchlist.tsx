import {
    createFileRoute,
} from "@tanstack/react-router";

import Watchlist from "@/pages/Watchlist";

export const Route = createFileRoute(
    "/_dashboard/watchlist",
)({
    component: Watchlist,
});