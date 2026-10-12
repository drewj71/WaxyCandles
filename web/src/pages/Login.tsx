import { useForm } from "react-hook-form";
import { useNavigate } from "@tanstack/react-router";
import { useMutation } from "@tanstack/react-query";
import { login } from "@/api/auth";
import { Button } from "@/components/ui/button";
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";

function Login() {
    const navigate = useNavigate();

    type LoginFormValues = {
        email: string;
        password: string;
    };

    const {
        register,
        handleSubmit,
        formState: { errors },
    } = useForm<LoginFormValues>({
        defaultValues: {
            email: "",
            password: "",
        },
    });

    const loginMutation = useMutation({
        mutationFn: login,
        onSuccess: async () => {
            await navigate({
                to: "/dashboard",
            });
        },
    });

    function onSubmit(data: LoginFormValues) {
        loginMutation.mutate(data);
    }

    return (
        <div className="flex min-h-screen items-center justify-center bg-muted/40 px-4">
            <Card className="w-full max-w-md">
                <CardHeader className="text-center">
                    <CardTitle className="text-2xl">
                        WaxyCandles
                    </CardTitle>

                    <CardDescription>
                        Sign in to your account
                    </CardDescription>
                </CardHeader>

                <CardContent>
                    <form
                        onSubmit={handleSubmit(onSubmit)}
                        className="space-y-4"
                    >
                        <div className="space-y-2">
                            <label
                                htmlFor="email"
                                className="text-sm font-medium"
                            >
                                Email
                            </label>

                            <Input
                                id="email"
                                type="email"
                                placeholder="you@example.com"
                                autoComplete="email"
                                {...register("email", {
                                    required: "Email is required",
                                })}
                                disabled={loginMutation.isPending}
                            />

                            {errors.email && (
                                <p className="text-sm text-destructive">
                                    {errors.email.message}
                                </p>
                            )}
                        </div>

                        <div className="space-y-2">
                            <label
                                htmlFor="password"
                                className="text-sm font-medium"
                            >
                                Password
                            </label>

                            <Input
                                id="password"
                                type="password"
                                placeholder="Enter your password"
                                autoComplete="current-password"
                                {...register("password", {
                                    required: "Password is required",
                                })}
                                disabled={loginMutation.isPending}
                            />

                            {errors.password && (
                                <p className="text-sm text-destructive">
                                    {errors.password.message}
                                </p>
                            )}
                        </div>

                        {loginMutation.isError && (
                            <div className="rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
                                Invalid email or password.
                            </div>
                        )}

                        <Button
                            type="submit"
                            className="w-full"
                            disabled={loginMutation.isPending}
                        >
                            {loginMutation.isPending
                                ? "Logging in..."
                                : "Login"}
                        </Button>
                    </form>
                </CardContent>
            </Card>
        </div>
    );
}

export default Login;