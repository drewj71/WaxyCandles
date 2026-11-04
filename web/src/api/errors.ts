import { isAxiosError } from "axios";

type ApiErrorBody = {
    message?: string;
    errors?: Record<string, string[]>;
};

/** The HTTP status of a failed API call, if the API answered at all. */
export function apiErrorStatus(error: unknown): number | undefined {
    return isAxiosError(error) ? error.response?.status : undefined;
}

/**
 * Seconds to wait from the response's Retry-After header (seconds or an HTTP
 * date), rounded up. Undefined if absent, unreadable or already past. The API
 * exposes the header over CORS; without that the browser hides it.
 */
export function retryAfterSeconds(error: unknown): number | undefined {
    if (!isAxiosError(error)) {
        return undefined;
    }

    const value: unknown = error.response?.headers?.["retry-after"];

    if (typeof value !== "string" || value.trim() === "") {
        return undefined;
    }

    const seconds = /^\d+$/.test(value.trim())
        ? Number(value.trim())
        : (Date.parse(value) - Date.now()) / 1000;

    return Number.isFinite(seconds) && seconds > 0
        ? Math.ceil(seconds)
        : undefined;
}

/** "45 s", "3 min", "2 h": a wait short enough to read at a glance. */
function formatWait(seconds: number): string {
    if (seconds < 120) {
        return `${seconds} s`;
    }

    if (seconds < 2 * 60 * 60) {
        return `${Math.ceil(seconds / 60)} min`;
    }

    return `${Math.ceil(seconds / 3600)} h`;
}

/**
 * A message for the statuses that mean "wait", whichever endpoint answered:
 * - 429: the API's own rate limit (per user, or per IP on login/register);
 * - 503: the market data provider's limit (e.g. its daily credits are spent);
 * - 504: the market data provider didn't answer in time.
 * Undefined for any other failure, so callers keep their own message.
 */
export function apiErrorStatusMessage(error: unknown): string | undefined {
    const wait = retryAfterSeconds(error);
    const tryAgain = wait === undefined
        ? "try again shortly"
        : `try again in ${formatWait(wait)}`;

    switch (apiErrorStatus(error)) {
        case 429:
            return `Too many requests, ${tryAgain}.`;
        case 503:
            return wait === undefined
                ? "Market data provider is unavailable (request limit reached). Try again later."
                : `Market data provider is unavailable (request limit reached), ${tryAgain}.`;
        case 504:
            return "Market data provider timed out. Try again shortly.";
        default:
            return undefined;
    }
}

/**
 * Readable messages for a failed API call: a rate-limit or provider message
 * for 429/503/504, else the per-field validation errors if the API returned
 * them, else its message, else the fallback.
 */
export function apiErrorMessages(
    error: unknown,
    fallback: string,
): string[] {
    const statusMessage = apiErrorStatusMessage(error);

    if (statusMessage) {
        return [statusMessage];
    }

    if (isAxiosError<ApiErrorBody>(error) && error.response?.data) {
        const { message, errors } = error.response.data;

        if (errors) {
            const fieldMessages = Object.values(errors).flat();

            if (fieldMessages.length > 0) {
                return fieldMessages;
            }
        }

        if (message) {
            return [message];
        }
    }

    return [fallback];
}

/**
 * TanStack Query's retry for queries. Retries only failures that might pass
 * on a second try (network errors, 500s), at most twice. Never:
 * - a 4xx, including 429 (the API's limit; retrying only spends more of it);
 * - a 503 or 504 from the market data provider: every attempt is a billed
 *   vendor request, and a spent daily allowance won't come back in seconds.
 * Mutations are never retried (QueryClient default, set explicitly in main.tsx).
 */
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
    const status = apiErrorStatus(error);

    if (status !== undefined && (status < 500 || status === 503 || status === 504)) {
        return false;
    }

    return failureCount < 2;
}
