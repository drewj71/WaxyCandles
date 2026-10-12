import type { FieldErrors, UseFormRegister } from "react-hook-form";

import type { AlertFrequency } from "@/api/alerts";
import { Input } from "@/components/ui/input";
import type { AlertFormValues } from "@/components/alerts/alert-form";

const selectClassName =
    "w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-ring";

type AlertSettingsFieldsProps = {
    register: UseFormRegister<AlertFormValues>;
    errors: FieldErrors<AlertFormValues>;
    frequency: AlertFrequency;
    disabled: boolean;
};

function FieldError({ message }: { message?: string }) {
    return message ? (
        <p className="mt-1 text-sm text-destructive">{message}</p>
    ) : null;
}

/** Target, direction, frequency, cooldown and expiry: shared by create and edit. */
function AlertSettingsFields({
    register,
    errors,
    frequency,
    disabled,
}: AlertSettingsFieldsProps) {
    return (
        <>
            <div>
                <label className="mb-2 block text-sm font-medium">
                    Target Price
                </label>

                <Input
                    type="number"
                    step="any"
                    placeholder="250.00"
                    disabled={disabled}
                    {...register("targetPrice", {
                        required: "Target price is required",
                        validate: (value) =>
                            Number(value) > 0 ||
                            "Target price must be greater than 0",
                    })}
                />

                <FieldError message={errors.targetPrice?.message} />
            </div>

            <div>
                <label className="mb-2 block text-sm font-medium">
                    Direction
                </label>

                <select
                    {...register("direction")}
                    disabled={disabled}
                    className={selectClassName}
                >
                    <option value="Above">Above</option>
                    <option value="Below">Below</option>
                </select>
            </div>

            <div>
                <label className="mb-2 block text-sm font-medium">
                    Repeat
                </label>

                <select
                    {...register("frequency")}
                    disabled={disabled}
                    className={selectClassName}
                >
                    <option value="Once">Once</option>
                    <option value="EveryCross">Every time it crosses</option>
                </select>
            </div>

            {frequency === "EveryCross" && (
                <div>
                    <label className="mb-2 block text-sm font-medium">
                        Cooldown (minutes)
                    </label>

                    <Input
                        type="number"
                        min={1}
                        max={1440}
                        step={1}
                        disabled={disabled}
                        {...register("cooldownMinutes", {
                            validate: (value) => {
                                const minutes = Number(value);

                                return (
                                    (Number.isInteger(minutes) &&
                                        minutes >= 1 &&
                                        minutes <= 1440) ||
                                    "Cooldown must be 1-1440 minutes"
                                );
                            },
                        })}
                    />

                    <FieldError message={errors.cooldownMinutes?.message} />
                </div>
            )}

            <div>
                <label className="mb-2 block text-sm font-medium">
                    Expires (optional)
                </label>

                <Input
                    type="datetime-local"
                    disabled={disabled}
                    {...register("expiresAt")}
                />
            </div>
        </>
    );
}

export default AlertSettingsFields;
