import {
    createFileRoute,
} from "@tanstack/react-router";

import Backtest from "@/pages/Backtest";

export const Route = createFileRoute(
    "/_dashboard/backtest",
)({
    component: Backtest,
});
