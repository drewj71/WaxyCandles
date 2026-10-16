import {
    createFileRoute,
} from "@tanstack/react-router";

import Gex from "@/pages/Gex";
import { parseGexSearch } from "@/components/gex/gex-search";

export const Route = createFileRoute(
    "/_dashboard/gex",
)({
    validateSearch: parseGexSearch,
    component: Gex,
});
