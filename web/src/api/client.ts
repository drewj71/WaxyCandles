import axios, {
  AxiosError,
  type InternalAxiosRequestConfig,
} from "axios";

export const api = axios.create({
  baseURL: "http://localhost:5148/api",
  withCredentials: true,
});

// Auth endpoints answer 401 for bad credentials or a dead refresh token;
// refreshing and retrying those would be wrong (or loop).
const noRefreshUrls = [
  "/auth/login",
  "/auth/register",
  "/auth/refresh",
  "/auth/logout",
];

type RetriableConfig = InternalAxiosRequestConfig & {
  _retriedAfterRefresh?: boolean;
};

let refreshInFlight: Promise<void> | null = null;

/**
 * Exchanges the refresh-token cookie for new access and refresh cookies.
 * Concurrent callers share one request: the API rotates the refresh token, so
 * a second refresh with the same cookie would be rejected.
 */
export function refreshSession(): Promise<void> {
  refreshInFlight ??= api
    .post("/auth/refresh")
    .then(() => undefined)
    .finally(() => {
      refreshInFlight = null;
    });

  return refreshInFlight;
}

// The access token lasts 15 minutes. When a request fails with 401, refresh
// once and retry it, so the session lasts as long as the refresh token.
api.interceptors.response.use(undefined, async (error: unknown) => {
  if (!isUnauthorized(error)) {
    throw error;
  }

  const config = error.config as RetriableConfig | undefined;

  if (
    !config ||
    config._retriedAfterRefresh ||
    noRefreshUrls.some((url) => config.url?.startsWith(url))
  ) {
    throw error;
  }

  config._retriedAfterRefresh = true;

  try {
    await refreshSession();
  } catch (refreshError) {
    // A network error or 5xx says nothing about the session; surface the
    // original failure and leave the user where they are.
    if (!isUnauthorized(refreshError)) {
      throw error;
    }

    // Another tab may have rotated the refresh token first, in which case the
    // browser already holds its new cookies and the retry below succeeds.
    try {
      return await api.request(config);
    } catch (retryError) {
      if (isUnauthorized(retryError)) {
        sessionExpired();
      }

      throw retryError;
    }
  }

  return api.request(config);
});

function isUnauthorized(error: unknown): error is AxiosError {
  return error instanceof AxiosError && error.response?.status === 401;
}

/** The refresh token is gone or revoked: back to the login page. */
function sessionExpired() {
  if (window.location.pathname !== "/login") {
    window.location.assign("/login");
  }
}
