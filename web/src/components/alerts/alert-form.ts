import {
    defaultCooldownMinutes,
    defaultLevelProximityPercent,
    type Alert,
    type AlertDirection,
    type AlertFrequency,
    type AlertSettings,
    type GexLevelKind,
} from "@/api/alerts";
import { DEFAULT_DAYS, DEFAULT_RANGE } from "@/components/gex/gex-search";

/** Price: a fixed target. Level: follows a node of the symbol's GEX board. */
export type AlertTargetType = "Price" | "Level";

/** Form state for creating or editing an alert (inputs hold strings). */
export type AlertFormValues = {
    symbol: string;
    targetType: AlertTargetType;
    targetPrice: string;
    direction: AlertDirection;
    level: GexLevelKind;
    levelProximityPercent: string;
    levelDays: string;
    levelStrikeRange: string;
    frequency: AlertFrequency;
    cooldownMinutes: string;
    /** A datetime-local value in the user's time zone, or "" for no expiry. */
    expiresAt: string;
};

export const emptyAlertForm: AlertFormValues = {
    symbol: "",
    targetType: "Price",
    targetPrice: "",
    direction: "Above",
    level: "King",
    levelProximityPercent: String(defaultLevelProximityPercent),
    levelDays: String(DEFAULT_DAYS),
    levelStrikeRange: String(DEFAULT_RANGE),
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
        targetType: alert.level ? "Level" : "Price",
        targetPrice: String(alert.targetPrice),
        direction: alert.direction,
        level: alert.level ?? emptyAlertForm.level,
        levelProximityPercent: String(alert.levelProximityPercent ?? defaultLevelProximityPercent),
        levelDays: String(alert.levelDays ?? DEFAULT_DAYS),
        levelStrikeRange: String(alert.levelStrikeRange ?? DEFAULT_RANGE),
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

    const isLevel = values.targetType === "Level";

    return {
        // Ignored by the API for a level alert, which derives them.
        targetPrice: isLevel ? 0 : Number(values.targetPrice),
        direction: values.direction,
        frequency: values.frequency,
        cooldownMinutes: Number(values.cooldownMinutes),
        expiresAt,
        level: isLevel ? values.level : null,
        ...(isLevel && {
            levelProximityPercent: Number(values.levelProximityPercent),
            levelDays: Number(values.levelDays),
            levelStrikeRange: Number(values.levelStrikeRange),
        }),
    };
}
