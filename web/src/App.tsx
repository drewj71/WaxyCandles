import { useEffect, useState } from "react";
import {
  Navigate,
  Route,
  Routes,
} from "react-router-dom";

import DashboardLayout from "./layouts/DashboardLayout";
import Dashboard from "./pages/Dashboard";
import Login from "./pages/Login";
import { getCurrentUser, logout } from "./api/auth";
import Watchlist from "./pages/Watchlist";
import Stock from "./pages/Stock";
import Alerts from "./pages/Alerts";

interface CurrentUser {
  id: string;
  email: string;
}

function App() {
  const [user, setUser] =
    useState<CurrentUser | null>(null);

  const [checkingAuth, setCheckingAuth] =
    useState(true);

  useEffect(() => {
    async function checkAuthentication() {
      try {
        const currentUser = await getCurrentUser();

        setUser(currentUser);
      } catch (err) {
        console.error(err);
        setUser(null);
      } finally {
        setCheckingAuth(false);
      }
    }

    checkAuthentication();
  }, []);

  if (checkingAuth) {
    return (
      <div className="flex min-h-svh items-center justify-center">
        <p className="text-muted-foreground">
          Loading...
        </p>
      </div>
    );
  }

  return (
    <Routes>
      <Route
        path="/login"
        element={
          user ? (
            <Navigate
              to="/dashboard"
              replace
            />
          ) : (
            <Login
              onLogin={async () => {
                try {
                  const currentUser =
                    await getCurrentUser();

                  setUser(currentUser);
                } catch (err) {
                  console.error(err);
                }
              }}
            />
          )
        }
      />

      <Route
        path="/dashboard"
        element={
          user ? (
            <DashboardLayout
              userEmail={user.email}
              onLogout={async () => {
                try {
                  await logout();
                } finally {
                  setUser(null);
                }
              }}
            >
              <Dashboard />
            </DashboardLayout>
          ) : (
            <Navigate to="/login" replace />
          )
        }
      />

      <Route
        path="/watchlist"
        element={
          user ? (
            <DashboardLayout
              userEmail={user.email}
              onLogout={async () => {
                try {
                  await logout();
                } finally {
                  setUser(null);
                }
              }}
            >
              <Watchlist />
            </DashboardLayout>
          ) : (
            <Navigate to="/login" replace />
          )
        }
      />

      <Route
        path="/stocks/:symbol"
        element={
          user ? (
            <DashboardLayout
              userEmail={user.email}
              onLogout={async () => {
                try {
                  await logout();
                } finally {
                  setUser(null);
                }
              }}
            >
              <Stock />
            </DashboardLayout>
          ) : (
            <Navigate to="/login" replace />
          )
        }
      />

      <Route
        path="/alerts"
        element={
          user ? (
            <DashboardLayout
              userEmail={user.email}
              onLogout={async () => {
                try {
                  await logout();
                } finally {
                  setUser(null);
                }
              }}
            >
              <Alerts />
            </DashboardLayout>
          ) : (
            <Navigate to="/login" replace />
          )
        }
      />

      <Route
        path="/"
        element={
          <Navigate
            to={
              user
                ? "/dashboard"
                : "/login"
            }
            replace
          />
        }
      />

      <Route
        path="*"
        element={
          <Navigate
            to={
              user
                ? "/dashboard"
                : "/login"
            }
            replace
          />
        }
      />
    </Routes>
  );
}

export default App;