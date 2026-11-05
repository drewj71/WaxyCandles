import {
    CartesianGrid,
    Legend,
    Line,
    LineChart,
    ResponsiveContainer,
    Tooltip,
    XAxis,
    YAxis,
} from "recharts";

import type { BacktestEquityPoint } from "@/api/backtests";
import { formatDate, formatMoney } from "./backtest-format";

/**
 * Strategy vs buy-and-hold equity at each close, one dollar axis. Colours are
 * categorical slots 1 (blue) and 2 (orange), stepped for light and dark; the
 * benchmark is also dashed so the two never rely on colour alone.
 */
function EquityCurveChart({ points }: { points: BacktestEquityPoint[] }) {
    return (
        <div className="h-[360px] w-full [--bt-strategy:#2a78d6] [--bt-benchmark:#eb6834] dark:[--bt-strategy:#3987e5] dark:[--bt-benchmark:#d95926]">
            <ResponsiveContainer width="100%" height="100%">
                <LineChart data={points} margin={{ top: 8, right: 16, bottom: 0, left: 8 }}>
                    <CartesianGrid stroke="rgba(128, 128, 128, 0.15)" vertical={false} />
                    <XAxis
                        dataKey="date"
                        tickFormatter={(value: string) => formatDate(value, { month: "short", year: "2-digit" })}
                        minTickGap={48}
                        tick={{ fontSize: 12, fill: "#a1a1aa" }}
                        axisLine={false}
                        tickLine={false}
                    />
                    <YAxis
                        tickFormatter={(value: number) => formatMoney(value, true)}
                        width={72}
                        tick={{ fontSize: 12, fill: "#a1a1aa" }}
                        axisLine={false}
                        tickLine={false}
                        domain={["auto", "auto"]}
                    />
                    <Tooltip
                        labelFormatter={(label) => formatDate(String(label))}
                        formatter={(value) => formatMoney(Number(value))}
                        contentStyle={{
                            background: "var(--popover)",
                            border: "1px solid var(--border)",
                            borderRadius: 6,
                            color: "var(--popover-foreground)",
                            fontSize: 12,
                        }}
                    />
                    <Legend wrapperStyle={{ fontSize: 12 }} />
                    <Line
                        type="linear"
                        dataKey="strategy"
                        name="Strategy"
                        stroke="var(--bt-strategy)"
                        strokeWidth={2}
                        dot={false}
                        activeDot={{ r: 4 }}
                        isAnimationActive={false}
                    />
                    <Line
                        type="linear"
                        dataKey="benchmark"
                        name="Buy & hold"
                        stroke="var(--bt-benchmark)"
                        strokeWidth={2}
                        strokeDasharray="6 4"
                        dot={false}
                        activeDot={{ r: 4 }}
                        isAnimationActive={false}
                    />
                </LineChart>
            </ResponsiveContainer>
        </div>
    );
}

export default EquityCurveChart;
