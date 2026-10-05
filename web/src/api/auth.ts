import { api } from "./client";

export interface LoginRequest {
  email: string;
  password: string;
}

export interface CurrentUser {
  id: string;
  email: string;
}

export async function login(
  request: LoginRequest
): Promise<void> {
  await api.post("/auth/login", request);
}

export async function refresh(): Promise<void> {
  await api.post("/auth/refresh");
}

export async function logout(): Promise<void> {
  await api.post("/auth/logout");
}

export async function getCurrentUser(): Promise<CurrentUser> {
  const response = await api.get<CurrentUser>("/auth/me");

  return response.data;
}