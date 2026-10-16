import { useQuery } from "@tanstack/react-query";
import { getWatchlistQuotes } from "@/api/watchlist";
import { getAlerts } from "@/api/alerts";
import { getMarketStatus } from "@/api/market";
import {
    Activity,
    Bell,
    Eye,
    Star,
} from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

/** "4:00 PM ET" today, or "Mon 9:30 AM ET" on another day. */
function formatEastern(iso: string): string {
    const date = new Date(iso);
    const easternDay = (d: Date) =>
        d.toLocaleDateString("en-US", { timeZone: "America/New_York" });

    const time = date.toLocaleTimeString("en-US", {
        timeZone: "America/New_York",
        hour: "numeric",
        minute: "2-digit",
    });

    const day = easternDay(date) === easternDay(new Date())
        ? ""
        : `${date.toLocaleDateString("en-US", { timeZone: "America/New_York", weekday: "short" })} `;

    return `${day}${time} ET`;
}

function Dashboard() {
    const watchlistQuery = useQuery({
        queryKey: ["watchlist"],
        queryFn: getWatchlistQuotes,
    });

    const alertsQuery = useQuery({
        queryKey: ["alerts"],
        queryFn: getAlerts,
    });

    const marketQuery = useQuery({
        queryKey: ["market-status"],
        queryFn: getMarketStatus,
        // Changes at most twice a day; re-check each minute so the card flips
        // at the open and close without a reload.
        refetchInterval: 60_000,
    });

    const stocks = watchlistQuery.data ?? [];
    const alerts = alertsQuery.data ?? [];

    // Armed and watching: excludes fired, disabled and expired alerts.
    const activeAlerts = alerts.filter(
        (alert) => alert.status === "Active",
    );

    return (
        <div className="mx-auto max-w-7xl space-y-8">
            <div>
                <h1 className="text-3xl font-semibold tracking-tight">
                    Dashboard
                </h1>

                <p className="mt-1 text-muted-foreground">
                    Monitor your watchlist, alerts, and market activity.
                </p>
            </div>

            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                <Card>
                    <CardHeader className="flex flex-row items-center justify-between pb-2">
                        <CardTitle className="text-sm font-medium">
                            Watchlist
                        </CardTitle>

                        <Star className="size-4 text-muted-foreground" />
                    </CardHeader>

                    <CardContent>
                        <div className="text-2xl font-semibold">
                            {watchlistQuery.isLoading ? "—" : stocks.length}
                        </div>

                        <p className="text-xs text-muted-foreground">
                            Stocks you're tracking
                        </p>
                    </CardContent>
                </Card>

                <Card>
                    <CardHeader className="flex flex-row items-center justify-between pb-2">
                        <CardTitle className="text-sm font-medium">
                            Active Alerts
                        </CardTitle>

                        <Bell className="size-4 text-muted-foreground" />
                    </CardHeader>

                    <CardContent>
                        <div className="text-2xl font-semibold">
                            {alertsQuery.isLoading ? "—" : activeAlerts.length}
                        </div>

                        <p className="text-xs text-muted-foreground">
                            Price alerts currently active
                        </p>
                    </CardContent>
                </Card>

                <Card>
                    <CardHeader className="flex flex-row items-center justify-between pb-2">
                        <CardTitle className="text-sm font-medium">
                            Market Status
                        </CardTitle>

                        <Activity className="size-4 text-muted-foreground" />
                    </CardHeader>

                    <CardContent>
                        <div className="text-2xl font-semibold">
                            {marketQuery.data
                                ? marketQuery.data.isOpen
                                    ? "Open"
                                    : "Closed"
                                : "—"}
                        </div>

                        <p className="text-xs text-muted-foreground">
                            {marketQuery.data
                                ? `U.S. markets · ${marketQuery.data.isOpen ? "closes" : "opens"} ${formatEastern(marketQuery.data.nextChange)}`
                                : "U.S. markets"}
                        </p>
                    </CardContent>
                </Card>

                <Card>
                    <CardHeader className="flex flex-row items-center justify-between pb-2">
                        <CardTitle className="text-sm font-medium">
                            Symbols Tracked
                        </CardTitle>

                        <Eye className="size-4 text-muted-foreground" />
                    </CardHeader>

                    <CardContent>
                        <div className="text-2xl font-semibold">
                            {watchlistQuery.isLoading ? "—" : stocks.length}
                        </div>

                        <p className="text-xs text-muted-foreground">
                            Across your platform
                        </p>
                    </CardContent>
                </Card>
            </div>

            <Card>
                <CardHeader>
                    <CardTitle>Market Overview</CardTitle>
                </CardHeader>

                <CardContent>
                    <div className="flex min-h-64 items-center justify-center rounded-lg border border-dashed">
                        <div className="text-center">
                            <Activity className="mx-auto size-8 text-muted-foreground" />

                            <p className="mt-3 font-medium">
                                Market data will appear here
                            </p>

                            <p className="mt-1 text-sm text-muted-foreground">
                                Your watchlist and market data will populate this area.
                            </p>
                        </div>
                    </div>
                </CardContent>
            </Card>
        </div>
    );
}

export default Dashboard;