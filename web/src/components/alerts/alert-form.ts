import {
    defaultCooldownMinutes,
    type Alert,
    type AlertDirection,
    type AlertFrequency,
    type AlertSettings,
} from "@/api/alerts";

/** Form state for creating or editing an alert (inputs hold strings). */
export type AlertFormValues = {
    symbol: string;
    targetPrice: string;
    direction: AlertDirection;
    frequency: AlertFrequency;
    cooldownMinutes: string;
    /** A datetime-local value in the user's time zone, or "" for no expiry. */
    expiresAt: string;
};

export const emptyAlertForm: AlertFormValues = {
    symbol: "",
    targetPrice: "",
    direction: "Above",
    frequency: "Once",
    cooldownMinutes: String(defaultCooldownMinutes),
    expiresAt: "",
};

/** ISO instant -> "YYYY-MM-DDTHH:mm" in local time, for a datetime-local input. */
export function toLocalDateTimeInput(iso: string): string {
    const date = new Date(iso);
    const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);

    return local.toISOString().slice(0, 16);
}

export function toAlertForm(alert: Alert): AlertFormValues {
    return {
        symbol: alert.symbol,
        targetPrice: String(alert.targetPrice),
        direction: alert.direction,
        frequency: alert.frequency,
        cooldownMinutes: String(alert.cooldownMinutes),
        expiresAt: alert.expiresAt
            ? toLocalDateTimeInput(alert.expiresAt)
            : "",
    };
}

/**
 * Form values -> API settings. When editing, an expiry the user didn't touch is
 * sent back exactly as the API returned it: the input only has minute
 * precision, and the API only allows a past expiry if it is unchanged.
 */
export function toAlertSettings(
    values: AlertFormValues,
    original?: Alert,
): AlertSettings {
    let expiresAt: string | null = null;

    if (values.expiresAt) {
        expiresAt =
            original?.expiresAt &&
            toLocalDateTimeInput(original.expiresAt) === values.expiresAt
                ? original.expiresAt
                : new Date(values.expiresAt).toISOString();
    }

    return {
        targetPrice: Number(values.targetPrice),
        direction: values.direction,
        frequency: values.frequency,
        cooldownMinutes: Number(values.cooldownMinutes),
        expiresAt,
    };
}
