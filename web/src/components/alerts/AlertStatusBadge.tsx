import type { AlertStatus } from "@/api/alerts";

const styles: Record<AlertStatus, string> = {
    Active: "bg-emerald-500/15 text-emerald-700 dark:text-emerald-400",
    Triggered: "bg-amber-500/15 text-amber-700 dark:text-amber-400",
    Disabled: "bg-muted text-muted-foreground",
    Expired: "bg-muted text-muted-foreground line-through",
};

function AlertStatusBadge({ status }: { status: AlertStatus }) {
    return (
        <span
            className={`inline-flex rounded-full px-2 py-0.5 text-xs font-medium ${styles[status]}`}
        >
            {status}
        </span>
    );
}

export default AlertStatusBadge;
