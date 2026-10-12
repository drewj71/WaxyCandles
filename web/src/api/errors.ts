import { isAxiosError } from "axios";

type ApiErrorBody = {
    message?: string;
    errors?: Record<string, string[]>;
};

/**
 * Readable messages for a failed API call: the per-field validation errors if
 * the API returned them, else its message, else the fallback.
 */
export function apiErrorMessages(
    error: unknown,
    fallback: string,
): string[] {
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
