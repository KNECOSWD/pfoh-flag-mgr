import {
  AccountInfo,
  IPublicClientApplication,
  InteractionRequiredAuthError
} from "@azure/msal-browser";
import { loginRequest } from "./authConfig";

export type AvailableFlagGrid = {
  id: number;
  flagGridName: string;
  reserved: boolean;
};

export type ServiceBranch = {
  id: number;
  serviceBranchCategoryId: number;
  serviceBranchName: string;
  description: string;
};

export type ServiceBranchCategory = {
  id: number;
  serviceBranchCategoryName: string;
  description: string;
};

export type HonoreeChangeRequest = {
  id: number;
  flagClaimId: number;
  flagGridId: number;
  honoreeId?: number | null;
  firstName: string;
  middleName?: string | null;
  lastName: string;
  suffix?: string | null;
  nickname?: string | null;
  rank?: string | null;
  serviceBranchId?: number | null;
  serviceBranchCategoryId?: number | null;
  startYear?: number | null;
  endYear?: number | null;
  datesUserEntry?: string | null;
  conflictsServed?: string | null;
  awards?: string | null;
  description?: string | null;
  kia: boolean;
  submitterPhoneNumber?: string | null;
  submitterEmailAddress?: string | null;
  requestStatus: string;
  createdUtc: string;
  submittedUtc?: string | null;
};

export type FlagClaim = {
  id: number;
  flagGridId: number;
  flagGridName: string;
  honoreeId?: number | null;
  claimStatus: string;
  externalUserEmail: string;
  externalUserName?: string | null;
  createdUtc: string;
  submittedUtc?: string | null;
  latestChangeRequest?: HonoreeChangeRequest | null;
};

export type SaveHonoreeChangeRequest = {
  firstName: string;
  middleName?: string | null;
  lastName: string;
  suffix?: string | null;
  nickname?: string | null;
  rank?: string | null;
  serviceBranchId?: number | null;
  serviceBranchCategoryId?: number | null;
  startYear?: number | null;
  endYear?: number | null;
  datesUserEntry?: string | null;
  conflictsServed?: string | null;
  awards?: string | null;
  description?: string | null;
  kia: boolean;
  submitterPhoneNumber?: string | null;
  submitterEmailAddress?: string | null;
};

const apiBase = import.meta.env.VITE_API_BASE_URL ?? "";

async function getToken(instance: IPublicClientApplication, account: AccountInfo) {
  try {
    const response = await instance.acquireTokenSilent({ ...loginRequest, account });
    return response.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await instance.acquireTokenRedirect({ ...loginRequest, account });
    }

    throw error;
  }
}

async function request<T>(
  instance: IPublicClientApplication,
  account: AccountInfo,
  url: string,
  options: RequestInit = {}
): Promise<T> {
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

  if (response.status === 204) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

export const flagGridApi = {
  available: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<AvailableFlagGrid[]>(instance, account, "/api/flag-grids/available")
};

export const flagClaimApi = {
  mine: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<FlagClaim[]>(instance, account, "/api/flag-claims/my"),

  claim: (instance: IPublicClientApplication, account: AccountInfo, flagGridId: number) =>
    request<FlagClaim>(instance, account, `/api/flag-claims/${flagGridId}/claim`, {
      method: "POST"
    }),

  saveDraft: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    claimId: number,
    requestBody: SaveHonoreeChangeRequest
  ) =>
    request<HonoreeChangeRequest>(instance, account, `/api/flag-claims/${claimId}/honoree-draft`, {
      method: "PUT",
      body: JSON.stringify(requestBody)
    }),

  submit: (instance: IPublicClientApplication, account: AccountInfo, claimId: number) =>
    request<FlagClaim>(instance, account, `/api/flag-claims/${claimId}/submit`, {
      method: "POST"
    })
};

export const lookupApi = {
  serviceBranches: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<ServiceBranch[]>(instance, account, "/api/lookups/service-branches"),

  serviceBranchCategories: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<ServiceBranchCategory[]>(
      instance,
      account,
      "/api/lookups/service-branch-categories"
    )
};
