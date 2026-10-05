import {
    Activity,
    Bell,
    Eye,
    Star,
} from "lucide-react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

function Dashboard() {
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
                            0
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
                            0
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
                            Open
                        </div>

                        <p className="text-xs text-muted-foreground">
                            U.S. markets
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
                            0
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