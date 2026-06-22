import {
  AccountInfo,
  IPublicClientApplication,
  InteractionRequiredAuthError
} from "@azure/msal-browser";
import { loginRequest } from "./authConfig";

export class ApiError extends Error {
  status: number;

  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

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

export type HonoreeSearchResult = {
  id: number;
  fullName: string;
  firstName: string;
  middleName?: string | null;
  lastName: string;
  suffix?: string | null;
  nickname?: string | null;
  rank: string;
  kia: boolean;
  serviceBranchName?: string | null;
  flagGrid?: string | null;
  sponsorName?: string | null;
  imageUrl?: string | null;
  pdfUrl?: string | null;
  isActive: boolean;
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
  honoreeName: string;
  honoreeImageUrl?: string | null;
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

export type AdminReviewItem = {
  changeRequestId: number;
  claimId: number;
  flagGridId: number;
  flagGridName: string;
  honoreeId?: number | null;
  honoreeName: string;
  rank?: string | null;
  serviceBranchName?: string | null;
  claimantEmail: string;
  claimantName?: string | null;
  requestStatus: string;
  createdUtc: string;
  submittedUtc?: string | null;
  requiresCardReprint: boolean;
  cardPrintedUtc?: string | null;
  reviewNotes?: string | null;
};


export type AdminPrintQueueItem = {
  changeRequestId: number;
  claimId: number;
  honoreeId?: number | null;
  honoreeName: string;
  flagGridName: string;
  serviceBranchName?: string | null;
  pdfUrl?: string | null;
  approvedUtc?: string | null;
  cardPrintedUtc?: string | null;
};

const apiBase = import.meta.env.VITE_API_BASE_URL ?? "";


async function publicRequest<T>(url: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(`${apiBase}${url}`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...(options.headers ?? {})
    }
  });

  if (!response.ok) {
    const message = await response.text();
    throw new ApiError(message || `${response.status} ${response.statusText}`, response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

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
    throw new ApiError(message || `${response.status} ${response.statusText}`, response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

async function downloadFile(
  instance: IPublicClientApplication,
  account: AccountInfo,
  url: string,
  fileName: string,
  options: RequestInit = {}
) {
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
    throw new ApiError(message || `${response.status} ${response.statusText}`, response.status);
  }

  const blob = await response.blob();
  const objectUrl = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = objectUrl;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(objectUrl);
}

export const honoreeApi = {
  search: (query: string, take = 25) =>
    publicRequest<HonoreeSearchResult[]>(
      `/api/honorees/search?q=${encodeURIComponent(query)}&take=${take}`
    )
};

export const flagClaimApi = {
  mine: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<FlagClaim[]>(instance, account, "/api/flag-claims/my"),

  claimHonoree: (instance: IPublicClientApplication, account: AccountInfo, honoreeId: number) =>
    request<FlagClaim>(instance, account, `/api/flag-claims/honoree/${honoreeId}/claim`, {
      method: "POST"
    }),

  startAdminEdit: (instance: IPublicClientApplication, account: AccountInfo, honoreeId: number) =>
    request<FlagClaim>(instance, account, `/api/flag-claims/admin/honoree/${honoreeId}/edit`, {
      method: "POST"
    }),

  applyAdminEditReprint: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    claimId: number
  ) =>
    request<FlagClaim>(instance, account, `/api/flag-claims/${claimId}/admin-apply-reprint`, {
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

export const adminApi = {
  pending: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<AdminReviewItem[]>(instance, account, "/api/admin/review/pending"),

  printQueue: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<AdminPrintQueueItem[]>(instance, account, "/api/admin/review/approved-reprint-queue"),

  approve: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    changeRequestId: number,
    requiresCardReprint: boolean,
    reviewNotes?: string
  ) =>
    request<AdminReviewItem>(instance, account, `/api/admin/review/${changeRequestId}/approve`, {
      method: "POST",
      body: JSON.stringify({ requiresCardReprint, reviewNotes })
    }),

  reject: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    changeRequestId: number,
    reviewNotes: string
  ) =>
    request<AdminReviewItem>(instance, account, `/api/admin/review/${changeRequestId}/reject`, {
      method: "POST",
      body: JSON.stringify({ reviewNotes })
    }),

  downloadMergedPrintPdf: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    changeRequestIds: number[]
  ) =>
    downloadFile(
      instance,
      account,
      "/api/admin/print/merge",
      `pfoh-card-reprints-${new Date().toISOString().slice(0, 10)}.pdf`,
      {
        method: "POST",
        body: JSON.stringify({ changeRequestIds })
      }
    ),

  markPrinted: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    changeRequestIds: number[]
  ) =>
    request<{ batchId: string; count: number }>(instance, account, "/api/admin/print/mark-printed", {
      method: "POST",
      body: JSON.stringify({ changeRequestIds })
    })
};
