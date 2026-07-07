import {
  AccountInfo,
  IPublicClientApplication,
  InteractionRequiredAuthError
} from "@azure/msal-browser";
import { loginRequest } from "./authConfig";

export class ApiError extends Error {
  status: number;
  method?: string;
  url?: string;
  requestId?: string;
  detail?: string;

  constructor(
    message: string,
    status: number,
    options: { method?: string; url?: string; requestId?: string; detail?: string } = {}
  ) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.method = options.method;
    this.url = options.url;
    this.requestId = options.requestId;
    this.detail = options.detail;
  }
}

function supportCode() {
  const randomPart =
    typeof crypto !== "undefined" && "randomUUID" in crypto
      ? crypto.randomUUID().slice(0, 8).toUpperCase()
      : Math.random().toString(16).slice(2, 10).toUpperCase();

  return `PFOH-${new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14)}-${randomPart}`;
}

async function buildApiError(response: Response, method: string, url: string): Promise<ApiError> {
  const rawMessage = await response.text();
  let serverMessage = rawMessage;
  let detail = "";

  try {
    const parsed = JSON.parse(rawMessage) as {
      message?: string;
      detail?: string;
      title?: string;
      errors?: Record<string, string[]>;
      currentStatus?: string;
    };

    const validationErrors = parsed.errors
      ? Object.entries(parsed.errors)
          .flatMap(([field, messages]) => messages.map((message) => `${field}: ${message}`))
          .join("; ")
      : "";

    serverMessage =
      parsed.message ||
      parsed.detail ||
      parsed.title ||
      validationErrors ||
      rawMessage;

    detail = [parsed.detail, validationErrors, parsed.currentStatus ? `Current status: ${parsed.currentStatus}` : ""]
      .filter(Boolean)
      .join(" | ");
  } catch {
    // Use raw response text.
  }

  const requestId =
    response.headers.get("x-correlation-id") ||
    response.headers.get("x-request-id") ||
    response.headers.get("traceparent") ||
    supportCode();

  const statusText = `${response.status} ${response.statusText}`.trim();
  const endpoint = `${method.toUpperCase()} ${url}`;
  const message = [
    `Request failed: ${endpoint}.`,
    `Status: ${statusText}.`,
    serverMessage ? `Server message: ${serverMessage}.` : "",
    `Reference: ${requestId}.`,
    "Please share this message with the developer if the issue continues."
  ]
    .filter(Boolean)
    .join(" ");

  return new ApiError(message, response.status, {
    method,
    url,
    requestId,
    detail: detail || rawMessage
  });
}

function buildNetworkError(error: unknown, method: string, url: string) {
  const requestId = supportCode();
  const detail = error instanceof Error ? error.message : String(error);
  return new ApiError(
    [
      `Network or browser error while calling ${method.toUpperCase()} ${url}.`,
      detail ? `Details: ${detail}.` : "",
      `Reference: ${requestId}.`,
      "Please check the connection and share this message with the developer if the issue continues."
    ]
      .filter(Boolean)
      .join(" "),
    0,
    { method, url, requestId, detail }
  );
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
  photoFileName?: string | null;
  submitterPhoneNumber?: string | null;
  submitterEmailAddress?: string | null;
  requestStatus: string;
  createdUtc: string;
  submittedUtc?: string | null;
};


export type AdminClaimantSummary = {
  claimId: number;
  claimantName?: string | null;
  claimantEmail: string;
  claimStatus: string;
  createdUtc: string;
  submittedUtc?: string | null;
  latestRequestStatus?: string | null;
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
  hasOtherClaimants?: boolean;
  otherClaimantCount?: number;
  claimNotice?: string | null;
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
  photoFileName?: string | null;
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


export type RegeneratePdfResult = {
  honoreeId: number;
  fileName: string;
  generatedUtc: string;
  uploaded: boolean;
  storageAccount?: string;
  container?: string;
  message?: string;
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

export type AdminFlagPosition = {
  flagGridId: number;
  flagGridName: string;
  rowLabel: string;
  columnNumber?: number | null;
  isOpen: boolean;
  isReserved: boolean;
  honoreeId?: number | null;
  honoreeName?: string | null;
  rank?: string | null;
  serviceBranchName?: string | null;
};

export type AdminUnassignedHonoree = {
  id: number;
  fullName: string;
  nickname?: string | null;
  rank?: string | null;
  serviceBranchName?: string | null;
};

const apiBase = import.meta.env.VITE_API_BASE_URL ?? "";

export function honoreePdfUrl(honoreeId: number) {
  return `${apiBase}/api/honorees/${honoreeId}/pdf`;
}

export function honoreePhotoUrl(honoreeId: number) {
  return `${apiBase}/api/honorees/${honoreeId}/photo`;
}


async function publicRequest<T>(url: string, options: RequestInit = {}): Promise<T> {
  const method = options.method ?? "GET";

  let response: Response;
  try {
    response = await fetch(`${apiBase}${url}`, {
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(options.headers ?? {})
      }
    });
  } catch (error) {
    throw buildNetworkError(error, method, url);
  }

  if (!response.ok) {
    throw await buildApiError(response, method, url);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  try {
    return (await response.json()) as T;
  } catch (error) {
    throw new ApiError(
      `The server returned an invalid JSON response for ${method.toUpperCase()} ${url}. Reference: ${supportCode()}. Please share this message with the developer.`,
      0,
      { method, url, detail: error instanceof Error ? error.message : String(error) }
    );
  }
}


function appendFormValue(formData: FormData, key: string, value: unknown) {
  if (value === undefined || value === null) return;
  formData.append(key, String(value));
}

function honoreeChangeFormData(requestBody: SaveHonoreeChangeRequest, photo?: File | null) {
  const formData = new FormData();

  Object.entries(requestBody).forEach(([key, value]) => appendFormValue(formData, key, value));

  if (photo) {
    formData.append("photo", photo);
  }

  return formData;
}

async function getToken(instance: IPublicClientApplication, account: AccountInfo) {
  try {
    const response = await instance.acquireTokenSilent({ ...loginRequest, account });
    return response.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await instance.acquireTokenRedirect({ ...loginRequest, account });
    }

    const requestId = supportCode();
    throw new ApiError(
      [
        "Authentication failed before the API request could be sent.",
        error instanceof Error ? `Details: ${error.message}.` : "",
        `Reference: ${requestId}.`,
        "Please sign out and sign back in. If it continues, share this message with the developer."
      ]
        .filter(Boolean)
        .join(" "),
      0,
      { requestId, detail: error instanceof Error ? error.message : String(error) }
    );
  }
}

async function request<T>(
  instance: IPublicClientApplication,
  account: AccountInfo,
  url: string,
  options: RequestInit = {}
): Promise<T> {
  const method = options.method ?? "GET";
  const token = await getToken(instance, account);

  const isFormData = options.body instanceof FormData;

  let response: Response;
  try {
    response = await fetch(`${apiBase}${url}`, {
      ...options,
      headers: {
        ...(isFormData ? {} : { "Content-Type": "application/json" }),
        Authorization: `Bearer ${token}`,
        ...(options.headers ?? {})
      }
    });
  } catch (error) {
    throw buildNetworkError(error, method, url);
  }

  if (!response.ok) {
    throw await buildApiError(response, method, url);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  try {
    return (await response.json()) as T;
  } catch (error) {
    throw new ApiError(
      `The server returned an invalid JSON response for ${method.toUpperCase()} ${url}. Reference: ${supportCode()}. Please share this message with the developer.`,
      0,
      { method, url, detail: error instanceof Error ? error.message : String(error) }
    );
  }
}

async function downloadFile(
  instance: IPublicClientApplication,
  account: AccountInfo,
  url: string,
  fileName: string,
  options: RequestInit = {}
) {
  const method = options.method ?? "GET";
  const token = await getToken(instance, account);

  const isFormData = options.body instanceof FormData;

  let response: Response;
  try {
    response = await fetch(`${apiBase}${url}`, {
      ...options,
      headers: {
        ...(isFormData ? {} : { "Content-Type": "application/json" }),
        Authorization: `Bearer ${token}`,
        ...(options.headers ?? {})
      }
    });
  } catch (error) {
    throw buildNetworkError(error, method, url);
  }

  if (!response.ok) {
    throw await buildApiError(response, method, url);
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

  nominate: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    requestBody: SaveHonoreeChangeRequest,
    photo?: File | null
  ) =>
    request<FlagClaim>(instance, account, "/api/flag-claims/nominate", {
      method: "POST",
      body: honoreeChangeFormData(requestBody, photo)
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
    requestBody: SaveHonoreeChangeRequest,
    photo?: File | null
  ) =>
    request<HonoreeChangeRequest>(instance, account, `/api/flag-claims/${claimId}/honoree-draft`, {
      method: "PUT",
      body: honoreeChangeFormData(requestBody, photo)
    }),

  submit: (instance: IPublicClientApplication, account: AccountInfo, claimId: number) =>
    request<FlagClaim>(instance, account, `/api/flag-claims/${claimId}/submit`, {
      method: "POST"
    }),

  unclaim: (instance: IPublicClientApplication, account: AccountInfo, claimId: number) =>
    request<{ message: string }>(instance, account, `/api/flag-claims/${claimId}/unclaim`, {
      method: "POST"
    }),

  claimantsForHonoree: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    honoreeId: number
  ) =>
    request<AdminClaimantSummary[]>(instance, account, `/api/flag-claims/admin/honoree/${honoreeId}/claimants`)
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


  queueHonoreeReprint: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    honoreeId: number
  ) =>
    request<AdminPrintQueueItem>(instance, account, `/api/admin/review/honoree/${honoreeId}/queue-reprint`, {
      method: "POST"
    }),

  regenerateHonoreePdf: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    honoreeId: number
  ) =>
    request<RegeneratePdfResult>(
      instance,
      account,
      `/api/honorees/${honoreeId}/pdf/regenerate`,
      {
        method: "POST"
      }
    ),

  removeFromReprintQueue: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    changeRequestIds: number[]
  ) =>
    request<{ count: number }>(instance, account, "/api/admin/review/reprint-queue/remove", {
      method: "POST",
      body: JSON.stringify({ changeRequestIds })
    }),

  markPrinted: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    changeRequestIds: number[]
  ) =>
    request<{ batchId: string; count: number }>(instance, account, "/api/admin/print/mark-printed", {
      method: "POST",
      body: JSON.stringify({ changeRequestIds })
    }),

  flagPositions: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<AdminFlagPosition[]>(instance, account, "/api/admin/review/flag-positions"),

  unassignedHonorees: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<AdminUnassignedHonoree[]>(instance, account, "/api/admin/review/unassigned-honorees"),

  assignFlagPosition: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    flagGridId: number,
    honoreeId: number
  ) =>
    request<AdminFlagPosition>(instance, account, `/api/admin/review/flag-positions/${flagGridId}/assign`, {
      method: "POST",
      body: JSON.stringify({ honoreeId })
    }),

  clearFlagPosition: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    flagGridId: number
  ) =>
    request<AdminFlagPosition>(instance, account, `/api/admin/review/flag-positions/${flagGridId}/clear`, {
      method: "POST"
    }),

  exportHonoreesExcel: (instance: IPublicClientApplication, account: AccountInfo) =>
    downloadFile(
      instance,
      account,
      "/api/admin/review/honorees-export",
      `pfoh-honorees-${new Date().toISOString().slice(0, 10)}.xls`
    )
};
