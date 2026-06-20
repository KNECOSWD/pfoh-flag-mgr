import { IPublicClientApplication, AccountInfo } from "@azure/msal-browser";
import { loginRequest } from "./authConfig";

export type FlagRecord = {
  id: number;
  honoreeName: string;
  serviceBranch?: string;
  rankOrTitle?: string;
  flagNumber?: string;
  gridLocation?: string;
  tributeText?: string;
  status: string;
  createdUtc: string;
  updatedUtc: string;
};

export type UpsertFlagRequest = Omit<FlagRecord, "id" | "createdUtc" | "updatedUtc">;

const apiBase = import.meta.env.VITE_API_BASE_URL ?? "";

async function getToken(instance: IPublicClientApplication, account: AccountInfo) {
  const response = await instance.acquireTokenSilent({ ...loginRequest, account });
  return response.accessToken;
}

async function request<T>(instance: IPublicClientApplication, account: AccountInfo, url: string, options: RequestInit = {}): Promise<T> {
  const token = await getToken(instance, account);
  const response = await fetch(`${apiBase}${url}`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
      ...(options.headers ?? {})
    }
  });

  if (!response.ok) {
    const message = await response.text();
    throw new Error(message || `${response.status} ${response.statusText}`);
  }

  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export const flagsApi = {
  list: (instance: IPublicClientApplication, account: AccountInfo) => request<FlagRecord[]>(instance, account, "/api/flags"),
  create: (instance: IPublicClientApplication, account: AccountInfo, flag: UpsertFlagRequest) =>
    request<FlagRecord>(instance, account, "/api/flags", { method: "POST", body: JSON.stringify(flag) }),
  update: (instance: IPublicClientApplication, account: AccountInfo, id: number, flag: UpsertFlagRequest) =>
    request<FlagRecord>(instance, account, `/api/flags/${id}`, { method: "PUT", body: JSON.stringify(flag) }),
  delete: (instance: IPublicClientApplication, account: AccountInfo, id: number) =>
    request<void>(instance, account, `/api/flags/${id}`, { method: "DELETE" })
};
