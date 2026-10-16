import type { FieldErrors, UseFormRegister } from "react-hook-form";

import { gexLevelKinds, type AlertFrequency } from "@/api/alerts";
import { Input } from "@/components/ui/input";
import type { AlertFormValues, AlertTargetType } from "@/components/alerts/alert-form";
import { DAY_OPTIONS, RANGE_OPTIONS } from "@/components/gex/gex-search";

const selectClassName =
    "w-full rounded-md border bg-background px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-ring";

type AlertSettingsFieldsProps = {
    register: UseFormRegister<AlertFormValues>;
    errors: FieldErrors<AlertFormValues>;
    frequency: AlertFrequency;
    targetType: AlertTargetType;
    disabled: boolean;
};

function FieldError({ message }: { message?: string }) {
    return message ? (
        <p className="mt-1 text-sm text-destructive">{message}</p>
    ) : null;
}

/**
 * What the alert fires on (a price, or a GEX level), plus frequency, cooldown
 * and expiry: shared by create and edit.
 */
function AlertSettingsFields({
    register,
    errors,
    frequency,
    targetType,
    disabled,
}: AlertSettingsFieldsProps) {
    return (
        <>
            <div>
                <label className="mb-2 block text-sm font-medium">
                    Alert on
                </label>

                <select
                    {...register("targetType")}
                    disabled={disabled}
                    className={selectClassName}
                >
                    <option value="Price">A price</option>
                    <option value="Level">A GEX level</option>
                </select>
            </div>

            {targetType === "Price" ? (
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
                                validate: (value, values) =>
                                    values.targetType === "Level" ||
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
                </>
            ) : (
                <>
                    <div>
                        <label className="mb-2 block text-sm font-medium">
                            Level
                        </label>

                        <select
                            {...register("level")}
                            disabled={disabled}
                            className={selectClassName}
                        >
                            {gexLevelKinds.map((kind) => (
                                <option key={kind} value={kind}>
                                    {kind}
                                </option>
                            ))}
                        </select>
                    </div>

                    <div>
                        <label className="mb-2 block text-sm font-medium">
                            Within (% of the level)
                        </label>

                        <Input
                            type="number"
                            step="any"
                            min={0}
                            max={5}
                            disabled={disabled}
                            {...register("levelProximityPercent", {
                                validate: (value, values) => {
                                    const percent = Number(value);

                                    return (
                                        values.targetType === "Price" ||
                                        (value !== "" && percent >= 0 && percent <= 5) ||
                                        "Must be 0-5%"
                                    );
                                },
                            })}
                        />

                        <FieldError message={errors.levelProximityPercent?.message} />
                    </div>

                    <div>
                        <label className="mb-2 block text-sm font-medium">
                            Board expiries
                        </label>

                        <select
                            {...register("levelDays")}
                            disabled={disabled}
                            className={selectClassName}
                        >
                            {DAY_OPTIONS.map((option) => (
                                <option key={option.days} value={option.days}>
                                    {option.label}
                                </option>
                            ))}
                        </select>
                    </div>

                    <div>
                        <label className="mb-2 block text-sm font-medium">
                            Board strikes
                        </label>

                        <select
                            {...register("levelStrikeRange")}
                            disabled={disabled}
                            className={selectClassName}
                        >
                            {RANGE_OPTIONS.map((range) => (
                                <option key={range} value={range}>
                                    ±{range}%
                                </option>
                            ))}
                        </select>
                    </div>
                </>
            )}

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
