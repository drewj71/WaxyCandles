import { useQuery } from "@tanstack/react-query";
import { cn } from "cn";

import { getMarketStatus } from "@/api/market";
import type { LiveConnectionState } from "@/lib/live-prices-connection";

/**
 * "Live" while the market is open and the prices connection is up, "Market
 * closed" outside the session (prices don't change then), and "Connecting"
 * while the connection is being (re)established during the session.
 */
function LiveIndicator({
    state,
    className,
}: {
    state: LiveConnectionState;
    className?: string;
}) {
    const marketQuery = useQuery({
        queryKey: ["market-status"],
        queryFn: getMarketStatus,
        refetchInterval: 60_000,
    });

    const isOpen = marketQuery.data?.isOpen;

    const [label, dot] =
        isOpen === false
            ? ["Market closed", "bg-muted-foreground"]
            : state === "connected"
                ? ["Live", "bg-green-500 animate-pulse"]
                : ["Connecting", "bg-amber-500"];

    return (
        <span
            className={cn(
                "inline-flex items-center gap-1.5 text-xs font-medium text-muted-foreground",
                className,
            )}
            aria-live="polite"
        >
            <span className={cn("size-2 rounded-full", dot)} />
            {label}
        </span>
    );
}

export default LiveIndicator;
