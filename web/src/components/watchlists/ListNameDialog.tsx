import { useEffect } from "react";
import { useForm } from "react-hook-form";

import { maxListNameLength } from "@/api/watchlists";
import { Button } from "@/components/ui/button";
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";

type ListNameDialogProps = {
    open: boolean;
    onOpenChange: (open: boolean) => void;
    title: string;
    description: string;
    submitLabel: string;
    initialName?: string;
    pending: boolean;
    /** Server-side errors (e.g. a duplicate name). */
    errors: string[];
    onSubmit: (name: string) => void;
};

type FormValues = { name: string };

/** Create or rename a watchlist. */
function ListNameDialog({
    open,
    onOpenChange,
    title,
    description,
    submitLabel,
    initialName = "",
    pending,
    errors,
    onSubmit,
}: ListNameDialogProps) {
    const {
        register,
        handleSubmit,
        reset,
        formState: { errors: formErrors },
    } = useForm<FormValues>({ defaultValues: { name: initialName } });

    useEffect(() => {
        if (open) {
            reset({ name: initialName });
        }
    }, [open, initialName, reset]);

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent>
                <DialogHeader>
                    <DialogTitle>{title}</DialogTitle>
                    <DialogDescription>{description}</DialogDescription>
                </DialogHeader>

                <form
                    onSubmit={handleSubmit((data) => onSubmit(data.name.trim()))}
                    className="space-y-4"
                >
                    <Input
                        autoFocus
                        placeholder="e.g. Semiconductors"
                        aria-label="List name"
                        aria-invalid={!!formErrors.name || errors.length > 0}
                        {...register("name", {
                            validate: (value) => {
                                const trimmed = value.trim();
                                if (!trimmed) {
                                    return "Enter a name.";
                                }
                                if (trimmed.length > maxListNameLength) {
                                    return `At most ${maxListNameLength} characters.`;
                                }
                                return true;
                            },
                        })}
                        disabled={pending}
                    />

                    {(formErrors.name?.message ? [formErrors.name.message] : errors).map((message) => (
                        <p key={message} className="text-sm text-destructive">
                            {message}
                        </p>
                    ))}

                    <DialogFooter>
                        <Button
                            type="button"
                            variant="ghost"
                            onClick={() => onOpenChange(false)}
                        >
                            Cancel
                        </Button>
                        <Button type="submit" disabled={pending}>
                            {pending ? "Saving..." : submitLabel}
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}

export default ListNameDialog;
