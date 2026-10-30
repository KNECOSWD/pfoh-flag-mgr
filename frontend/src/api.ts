import {
  AccountInfo,
  IPublicClientApplication,
  InteractionRequiredAuthError
} from "@azure/msal-browser";
import { loginRequest } from "./authConfig";
import { isPublicApiPath } from "./publicApi.js";
import {
  bannerForTokenError,
  isQuietAuthRejection,
  isRetryableNetworkError,
  sharedTokenRequest,
  withNetworkRetry
} from "./tokenRequest.js";

export class ApiError extends Error {
  status: number;
  method?: string;
  url?: string;
  requestId?: string;
  detail?: string;
  serverMessage?: string;

  constructor(
    message: string,
    status: number,
    options: { method?: string; url?: string; requestId?: string; detail?: string; serverMessage?: string } = {}
  ) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.method = options.method;
    this.url = options.url;
    this.requestId = options.requestId;
    this.detail = options.detail;
    this.serverMessage = options.serverMessage;
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
    detail: detail || rawMessage,
    serverMessage: serverMessage || undefined
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


export type DuplicateClaimant = {
  userId: string;
  name?: string | null;
  email: string;
  claimedUtc: string;
  claimStatus: string;
};

export type DuplicateHonoree = {
  id: number;
  name: string;
  firstName: string;
  middleName?: string | null;
  lastName: string;
  suffix?: string | null;
  nickname?: string | null;
  rank: string;
  serviceBranchName?: string | null;
  serviceYears: string;
  conflictsServed: string;
  awards: string;
  description: string;
  kia: boolean;
  photoFileName: string;
  photoUrl?: string | null;
  flagGridId?: number | null;
  flagGridName?: string | null;
  phoneNumber: string;
  emailAddress: string;
  sponsorName: string;
  claimants: DuplicateClaimant[];
  // Honoree.CreatedDate: when this record was created and received.
  createdDate: string;
};

export type DuplicatePair = {
  leftId: number;
  rightId: number;
  reasonCodes: string[];
  explanation: string;
};

export type DuplicateGroup = {
  groupKey: string;
  likelihood: string;
  explanation: string;
  reasonCodes: string[];
  pairs: DuplicatePair[];
  honorees: DuplicateHonoree[];
};

export type DuplicateMergeResult = {
  survivorId: number;
  survivorName: string;
  deactivatedIds: number[];
  claimants: DuplicateClaimant[];
  reprintQueued?: boolean;
};

export type MergeHistoryAbsorbed = {
  id: number;
  name: string;
};

export type MergeHistoryRecord = {
  id: number;
  survivorId: number;
  survivorName: string;
  absorbed: MergeHistoryAbsorbed[];
  mergedBy: string;
  mergedUtc: string;
  undoneBy?: string | null;
  undoneUtc?: string | null;
};

export type ReprintDuplicateReview = {
  id: number;
  honoreeId: number;
  honoreeName: string;
  duplicateChangeRequestId: number;
  keptChangeRequestId: number;
  flaggedUtc: string;
};

export type AdminClaimantSummary = {
  claimId: number;
  claimantName?: string | null;
  claimantEmail: string;
  claimantPhone?: string | null;
  claimantOrganization?: string | null;
  claimStatus: string;
  createdUtc: string;
  submittedUtc?: string | null;
  latestRequestStatus?: string | null;
};

export type FlagClaim = {
  id: number;
  flagGridId?: number | null;
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
  flagGridId?: number | null;
  flagGridName: string;
  honoreeId?: number | null;
  honoreeName: string;
  rank?: string | null;
  serviceBranchName?: string | null;
  claimantEmail: string;
  claimantName?: string | null;
  claimantPhone?: string | null;
  claimantOrganization?: string | null;
  requestStatus: string;
  createdUtc: string;
  submittedUtc?: string | null;
  requiresCardReprint: boolean;
  cardPrintedUtc?: string | null;
  reviewNotes?: string | null;
  photoFileName?: string | null;
  description?: string | null;
  awards?: string | null;
  conflictsServed?: string | null;
  submissionKind?: string | null;
  fieldChanges?: { label: string; previous: string; proposed: string }[] | null;
  flagSpotAssigned?: boolean | null;
  flagSpotNotice?: string | null;
  requesterNote?: string | null;
  linkUsedUtc?: string | null;
};

export type ReactivationSummary = {
  name: string;
  rank?: string | null;
  serviceBranch?: string | null;
  years?: string | null;
  conflictsServed?: string | null;
  awards?: string | null;
  description?: string | null;
  kia: boolean;
};

export type ReactivationLinkState = {
  status: "invalid" | "sign-in" | "email-mismatch" | "ready" | "submitted";
  message: string;
  honoree?: ReactivationSummary | null;
};

export type ReactivationEligible = {
  honoreeId: number;
  name: string;
  requesterEmail: string;
  activeLinkExpiresUtc?: string | null;
};

export type ReactivationLinkRow = {
  honoreeId: string;
  name: string;
  requesterEmail: string;
  link: string;
  expiry: string;
  eligibilityReason: string;
};

export type ReactivationGenerateResult = {
  csv: string;
  rows: ReactivationLinkRow[];
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

export type CardPrintEvent = {
  batchId: string;
  printedUtc: string;
  printedBy: string;
  cardCount: number;
  comment?: string | null;
};

export type RemovalQueueItem = {
  id: number;
  honoreeId?: number | null;
  honoreeName: string;
  flagGridId: number;
  flagGridName: string;
  newFlagGridId?: number | null;
  newFlagGridName?: string | null;
  source: string;
  sourceDetail?: string | null;
  mergeAuditId?: number | null;
  createdUtc: string;
  createdBy: string;
  doneUtc?: string | null;
  doneBy?: string | null;
  canceledUtc?: string | null;
  canceledBy?: string | null;
  cancelReason?: string | null;
  loggedAlreadyDone: boolean;
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

export type PrintBatchCard = {
  changeRequestId: number;
  honoreeId: number;
  honoreeName: string;
};

export type PrintBatchPlan = {
  cards: PrintBatchCard[];
  notInQueue: number[];
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

export function changeRequestPhotoUrl(changeRequestId: number) {
  return `${apiBase}/api/flagclaims/change-requests/${changeRequestId}/photo`;
}

export function adminCardPreviewUrl(changeRequestId: number) {
  return `${apiBase}/api/admin/review/${changeRequestId}/card-preview`;
}

export function mySubmissionCardUrl(claimId: number) {
  return `${apiBase}/api/flag-claims/${claimId}/card`;
}

const requestTimeoutMs = 60_000;

function requestTimeoutSignal(existing?: AbortSignal | null) {
  if (existing) return existing;

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), requestTimeoutMs);
  controller.signal.addEventListener("abort", () => clearTimeout(timer), { once: true });
  return controller.signal;
}

export function isPendingHonoreePhoto(photoFileName?: string | null) {
  return !!photoFileName && photoFileName.trim().toLowerCase().startsWith("pending/") && !photoFileName.includes("..");
}

export async function fetchAuthorizedBlob(
  instance: IPublicClientApplication,
  account: AccountInfo,
  url: string,
  init: RequestInit = {}
) {
  const token = await getToken(instance, account);
  const method = (init.method ?? "GET").toUpperCase();
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${token}`);
  const response = await withNetworkRetry(async () => {
    try {
      return await fetch(url, {
        ...init,
        method,
        cache: "no-store",
        headers,
        signal: requestTimeoutSignal(init.signal)
      });
    } catch (error) {
      throw buildNetworkError(error, method, url);
    }
  });

  if (!response.ok) {
    throw await buildApiError(response, method, url);
  }

  try {
    return await response.blob();
  } catch (error) {
    throw buildNetworkError(error, method, url);
  }
}

async function fetchQueuedCardPdf(
  instance: IPublicClientApplication,
  account: AccountInfo,
  changeRequestId: number,
  signal?: AbortSignal
): Promise<ArrayBuffer | null> {
  const url = `${apiBase}/api/admin/print/cards/${changeRequestId}/pdf`;
  const token = await getToken(instance, account);

  // No timeout signal here: one slow card must not abort a batch. Only the caller's
  // signal (Close in the Print Center) cancels it.
  const attempt = async () => {
    let response: Response;
    try {
      response = await fetch(url, {
        method: "GET",
        cache: "no-store",
        headers: { Authorization: `Bearer ${token}` },
        signal
      });
    } catch (error) {
      throw buildNetworkError(error, "GET", url);
    }

    if (response.status === 404) {
      const body = (await response.clone().json().catch(() => null)) as { code?: string } | null;
      if (body?.code === "pdf-missing") return null;
    }

    if (!response.ok) {
      throw await buildApiError(response, "GET", url);
    }

    try {
      return await response.arrayBuffer();
    } catch (error) {
      throw buildNetworkError(error, "GET", url);
    }
  };

  try {
    return await attempt();
  } catch (error) {
    if (signal?.aborted || !isRetryableNetworkError(error)) throw error;
    return attempt();
  }
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
    return await sharedTokenRequest.acquire({
      silent: async () => {
        const response = await instance.acquireTokenSilent({ ...loginRequest, account });
        return response.accessToken;
      },
      redirect: () =>
        instance.acquireTokenRedirect({
          ...loginRequest,
          account,
          redirectStartPage: window.location.href
        }),
      isInteractionRequired: (error: unknown) => error instanceof InteractionRequiredAuthError
    });
  } catch (error) {
    const banner = bannerForTokenError(error);
    if (banner === "") {
      throw isQuietAuthRejection(error) ? error : new Error("Authentication is being refreshed.");
    }
    if (banner) {
      throw new ApiError(banner, 0);
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
  options: RequestInit = {},
  capture?: { headers?: Headers }
): Promise<T> {
  const method = options.method ?? "GET";
  if (isPublicApiPath(url)) {
    return publicRequest<T>(url, options);
  }

  const token = await getToken(instance, account);

  const isFormData = options.body instanceof FormData;

  const response = await withNetworkRetry(async () => {
    try {
      return await fetch(`${apiBase}${url}`, {
        ...options,
        signal: requestTimeoutSignal(options.signal),
        headers: {
          ...(isFormData ? {} : { "Content-Type": "application/json" }),
          Authorization: `Bearer ${token}`,
          ...(options.headers ?? {})
        }
      });
    } catch (error) {
      throw buildNetworkError(error, method, url);
    }
  });

  if (!response.ok) {
    throw await buildApiError(response, method, url);
  }

  if (capture) capture.headers = response.headers;

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
    request<{ message: string; removalRequested?: boolean }>(instance, account, `/api/flag-claims/${claimId}/unclaim`, {
      method: "POST"
    }),

  unlink: (instance: IPublicClientApplication, account: AccountInfo, claimId: number) =>
    request<{ message: string }>(instance, account, `/api/flag-claims/${claimId}/unlink`, {
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
  serviceBranches: () => publicRequest<ServiceBranch[]>("/api/lookups/service-branches"),

  serviceBranchCategories: () =>
    publicRequest<ServiceBranchCategory[]>("/api/lookups/service-branch-categories")
};

export const adminApi = {
  pending: async (instance: IPublicClientApplication, account: AccountInfo) => {
    const capture: { headers?: Headers } = {};
    const items = await request<AdminReviewItem[]>(instance, account, "/api/admin/review/pending", {}, capture);
    const raw = capture.headers?.get("X-Open-Duplicate-Groups");
    const parsed = raw == null ? 0 : Number(raw);
    return {
      items,
      openDuplicateGroupCount: Number.isFinite(parsed) && parsed >= 0 ? parsed : 0
    };
  },

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

  declineRemoval: (instance: IPublicClientApplication, account: AccountInfo, changeRequestId: number) =>
    request<{ message: string }>(instance, account, `/api/admin/review/${changeRequestId}/decline-removal`, {
      method: "POST"
    }),

  removedFromField: (instance: IPublicClientApplication, account: AccountInfo, changeRequestId: number) =>
    request<{ message: string }>(instance, account, `/api/admin/review/${changeRequestId}/removed-from-field`, {
      method: "POST"
    }),

  planPrintBatch: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    changeRequestIds: number[],
    signal?: AbortSignal
  ) =>
    request<PrintBatchPlan>(instance, account, "/api/admin/print/plan", {
      method: "POST",
      body: JSON.stringify({ changeRequestIds }),
      signal
    }),

  fetchQueuedCardPdf,


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
    honoreeId: number,
    signal?: AbortSignal
  ) =>
    request<RegeneratePdfResult>(
      instance,
      account,
      `/api/honorees/${honoreeId}/pdf/regenerate`,
      {
        method: "POST",
        signal
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
    changeRequestIds: number[],
    comment?: string
  ) =>
    request<{ batchId: string; count: number }>(instance, account, "/api/admin/print/mark-printed", {
      method: "POST",
      body: JSON.stringify({ changeRequestIds, comment: comment ?? "" })
    }),

  printHistory: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<CardPrintEvent[]>(instance, account, "/api/admin/print/history"),

  duplicateGroups: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<DuplicateGroup[]>(instance, account, "/api/admin/duplicates/groups"),

  mergeHistory: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<MergeHistoryRecord[]>(instance, account, "/api/admin/duplicates/merges"),

  undoMerge: (instance: IPublicClientApplication, account: AccountInfo, id: number) =>
    request<{ id: number }>(instance, account, `/api/admin/duplicates/merges/${id}/undo`, {
      method: "POST"
    }),

  mergeDuplicates: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    body: {
      survivorId: number;
      duplicateIds: number[];
      fields: Record<string, number>;
      values?: Record<string, string>;
      combineAwards?: boolean;
      queueReprint?: boolean;
    }
  ) =>
    request<DuplicateMergeResult>(instance, account, "/api/admin/duplicates/merge", {
      method: "POST",
      body: JSON.stringify(body)
    }),

  previewMergeCard: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    body: {
      survivorId: number;
      duplicateIds: number[];
      fields: Record<string, number>;
      values?: Record<string, string>;
      combineAwards?: boolean;
    }
  ) =>
    fetchAuthorizedBlob(instance, account, `${apiBase}/api/admin/duplicates/merge-preview/card`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    }),

  markNotDuplicates: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    leftId: number,
    rightId: number
  ) =>
    request<{ lowHonoreeId: number; highHonoreeId: number }>(instance, account, "/api/admin/duplicates/not-duplicates", {
      method: "POST",
      body: JSON.stringify({ leftId, rightId })
    }),

  reprintDuplicateReviews: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<ReprintDuplicateReview[]>(instance, account, "/api/admin/print/duplicate-reviews"),

  approveReprintDuplicate: (instance: IPublicClientApplication, account: AccountInfo, id: number) =>
    request<{ message: string }>(instance, account, `/api/admin/print/duplicate-reviews/${id}/approve`, {
      method: "POST"
    }),

  dismissReprintDuplicate: (instance: IPublicClientApplication, account: AccountInfo, id: number) =>
    request<{ message: string }>(instance, account, `/api/admin/print/duplicate-reviews/${id}/dismiss`, {
      method: "POST"
    }),

  removalQueue: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    status: "open" | "done" = "open"
  ) =>
    request<RemovalQueueItem[]>(instance, account, `/api/admin/removal-queue?status=${status}`),

  removalQueueCount: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<{ count: number }>(instance, account, "/api/admin/removal-queue/count"),

  markRemovalDone: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    ids: number[]
  ) =>
    request<{ count: number }>(instance, account, "/api/admin/removal-queue/done", {
      method: "POST",
      body: JSON.stringify({ ids })
    }),

  exportRemovalQueue: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    status: "open" | "done"
  ) =>
    downloadFile(
      instance,
      account,
      `/api/admin/removal-queue/export?status=${status}`,
      `pfoh-removal-queue-${new Date().toISOString().slice(0, 10)}.xlsx`
    ),

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
    ),

  exportFlagMapExcel: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    columns: readonly FlagMapExportField[]
  ) => {
    const selected = flagMapExportFields
      .map((field) => field.id)
      .filter((id) => columns.includes(id));
    const params = new URLSearchParams();
    if (selected.length > 0) {
      params.set("columns", selected.join(","));
    }

    const query = params.toString();
    return downloadFile(
      instance,
      account,
      query ? `/api/admin/review/flag-map-export?${query}` : "/api/admin/review/flag-map-export",
      `pfoh-flag-map-${new Date().toISOString().slice(0, 10)}.xlsx`
    );
  }
};

function reactivationPath(token: string) {
  return `/api/reactivate/${encodeURIComponent(token)}`;
}

export const reactivationApi = {
  inspectAnonymous: (token: string) => publicRequest<ReactivationLinkState>(reactivationPath(token)),

  inspect: (instance: IPublicClientApplication, account: AccountInfo, token: string) =>
    request<ReactivationLinkState>(instance, account, reactivationPath(token)),

  submit: (instance: IPublicClientApplication, account: AccountInfo, token: string, note: string) =>
    request<ReactivationLinkState>(instance, account, reactivationPath(token), {
      method: "POST",
      body: JSON.stringify({ note })
    })
};

export const adminReactivationApi = {
  eligible: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<ReactivationEligible[]>(instance, account, "/api/admin/reactivation/eligible"),

  generate: (instance: IPublicClientApplication, account: AccountInfo, honoreeIds: number[]) =>
    request<ReactivationGenerateResult>(instance, account, "/api/admin/reactivation/links", {
      method: "POST",
      body: JSON.stringify({ honoreeIds: honoreeIds.length > 0 ? honoreeIds : null })
    }),

  generateCsv: (instance: IPublicClientApplication, account: AccountInfo, file: File) => {
    const body = new FormData();
    body.append("file", file);
    return request<ReactivationGenerateResult>(instance, account, "/api/admin/reactivation/links/csv", {
      method: "POST",
      body
    });
  },

  revoke: (instance: IPublicClientApplication, account: AccountInfo, honoreeId: number) =>
    request<{ revoked: boolean }>(instance, account, `/api/admin/reactivation/${honoreeId}/revoke`, {
      method: "POST"
    })
};

export const flagMapExportFields = [
  { id: "firstName", label: "First Name" },
  { id: "lastName", label: "Last Name" },
  { id: "fullName", label: "Full Name" },
  { id: "flagGrid", label: "Flag Grid", hint: "A-01" },
  { id: "status", label: "Status", hint: "Open / Occupied / Reserved" },
  { id: "rank", label: "Rank" },
  { id: "serviceBranch", label: "Service Branch" },
  { id: "sponsorName", label: "Sponsor Name" },
  { id: "kia", label: "KIA", hint: "Yes / No" }
] as const;

export type FlagMapExportField = (typeof flagMapExportFields)[number]["id"];

export const displayNameMaxLength = 100;
export const organizationMaxLength = 200;

export { displayUsPhone, formatUsPhone, normalizeUsPhone, usPhoneErrorMessage } from "./usPhone.js";

export type UserProfile = {
  ownerObjectId: string;
  displayName: string;
  email: string;
  hasSavedDisplayName: boolean;
  phone: string;
  organization: string;
  phoneNeedsCorrection?: boolean;
};

export type UpdateUserProfileRequest = {
  displayName: string;
  phone: string;
  organization?: string;
};

export const profileApi = {
  get: (instance: IPublicClientApplication, account: AccountInfo) =>
    request<UserProfile>(instance, account, "/api/profile"),

  update: (instance: IPublicClientApplication, account: AccountInfo, profile: UpdateUserProfileRequest) =>
    request<UserProfile>(instance, account, "/api/profile", {
      method: "PUT",
      body: JSON.stringify({
        displayName: profile.displayName,
        phone: profile.phone,
        organization: profile.organization ?? ""
      })
    })
};
