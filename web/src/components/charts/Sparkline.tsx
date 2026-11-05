import type { SparklinePoint } from "@/api/watchlists";

type SparklineProps = {
    points: SparklinePoint[];
    width?: number;
    height?: number;
};

/** A tiny line of daily closes; green if the last close is above the first. */
function Sparkline({ points, width = 96, height = 28 }: SparklineProps) {
    if (points.length < 2) {
        return (
            <span className="text-xs text-muted-foreground" title="No stored daily candles in the last 30 days">
                No data
            </span>
        );
    }

    const closes = points.map((p) => p.close);
    const min = Math.min(...closes);
    const max = Math.max(...closes);
    const range = max - min || 1;
    const pad = 2;

    const path = closes
        .map((close, i) => {
            const x = pad + (i / (closes.length - 1)) * (width - 2 * pad);
            const y = pad + (1 - (close - min) / range) * (height - 2 * pad);
            return `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
        })
        .join(" ");

    const rising = closes.at(-1)! >= closes[0];

    return (
        <svg
            width={width}
            height={height}
            viewBox={`0 0 ${width} ${height}`}
            role="img"
            aria-label={`30-day trend, ${rising ? "up" : "down"}`}
        >
            <path
                d={path}
                fill="none"
                strokeWidth={1.5}
                className={rising ? "stroke-green-500" : "stroke-red-500"}
            />
        </svg>
    );
}

export default Sparkline;
