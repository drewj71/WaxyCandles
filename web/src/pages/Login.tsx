import { type FormEvent, useState } from "react";
import { login } from "../api/auth";
import { useNavigate } from "@tanstack/react-router";

function Login() {
    const navigate = useNavigate();
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [error, setError] = useState("");
    const [loading, setLoading] = useState(false);

    async function handleSubmit(
        event: FormEvent<HTMLFormElement>,
    ) {
        event.preventDefault();

        setError("");
        setLoading(true);

        try {
            await login({
                email,
                password,
            });

            await navigate({
                to: "/dashboard",
            });
        } catch (err) {
            console.error(err);
            setError("Invalid email or password.");
        } finally {
            setLoading(false);
        }
    }

    return (
        <div>
            <h1>WaxyCandles</h1>

            <h2>Login</h2>

            <form onSubmit={handleSubmit}>
                <div>
                    <label htmlFor="email">
                        Email
                    </label>

                    <input
                        id="email"
                        type="email"
                        value={email}
                        onChange={(event) =>
                            setEmail(event.target.value)
                        }
                        required
                    />
                </div>

                <div>
                    <label htmlFor="password">
                        Password
                    </label>

                    <input
                        id="password"
                        type="password"
                        value={password}
                        onChange={(event) =>
                            setPassword(event.target.value)
                        }
                        required
                    />
                </div>

                {error && <p>{error}</p>}

                <button
                    type="submit"
                    disabled={loading}
                >
                    {loading ? "Logging in..." : "Login"}
                </button>
            </form>
        </div>
    );
}

export default Login;