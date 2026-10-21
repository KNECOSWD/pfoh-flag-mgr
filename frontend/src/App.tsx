import { type FormEvent, type MouseEvent, useEffect, useMemo, useRef, useState } from "react";
import packageInfo from "../package.json";
import { CacheLookupPolicy, type SilentRequest } from "@azure/msal-browser";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import * as Dialog from "@radix-ui/react-dialog";
import {
  AdminClaimantSummary,
  AdminFlagPosition,
  AdminUnassignedHonoree,
  AdminPrintQueueItem,
  CardPrintEvent,
  AdminReviewItem,
  ApiError,
  FlagClaim,
  HonoreeSearchResult,
  SaveHonoreeChangeRequest,
  ServiceBranch,
  ServiceBranchCategory,
  adminApi,
  flagClaimApi,
  flagMapExportFields,
  honoreeApi,
  adminCardPreviewUrl,
  mySubmissionCardUrl,
  changeRequestPhotoUrl,
  fetchAuthorizedBlob,
  honoreePdfUrl,
  honoreePhotoUrl,
  isPendingHonoreePhoto,
  displayNameMaxLength,
  displayUsPhone,
  formatUsPhone,
  normalizeUsPhone,
  usPhoneErrorMessage,
  organizationMaxLength,
  type FlagMapExportField,
  lookupApi,
  profileApi
} from "./api";
import { decideAdminLoad } from "./adminAccess.js";
import { adminAttentionItems } from "./adminAttention.js";
import { thumbnailLoadKey } from "./authorizedThumbnail.js";
import { startVersionWatch, versionUrl } from "./versionReload.js";
import { AdminNavMenu } from "./AdminNavMenu";
import { DuplicateMerge } from "./DuplicateMerge";
import { PrintDuplicateReviews } from "./PrintDuplicateReviews";
import PublicFlagMap from "./PublicFlagMap";
import {
  clearKioskSession,
  kioskBackTarget,
  kioskBlocksNavigationKey,
  kioskChrome,
  kioskLinkLeavesMap,
  kioskSessionOn,
  kioskStayHref,
  rememberKioskSession
} from "./kioskSession.js";
import { createBlobUrlCache } from "./blobUrlCache.js";
import { shouldCompressHonoreePhoto, withDeadline } from "./prepareHonoreePhoto.js";
import { createProfileLoadGate } from "./profileLoad.js";
import {
  cardPreviewLoadingMessage,
  honoreeCardDownloadName,
  honoreeCardHeading,
  formatPrintEventTime,
  mergedPrintName,
  PRINT_EVENT_COMMENT_MAX,
  printCenterHeading,
  printSelectionSubtitle
} from "./cardPreview.js";
import {
  mergePrintBatch,
  preparePrintBatch,
  printBatchFailureSummary,
  printBatchProgressText,
  printCardLabel,
  printFailureStepLabel
} from "./printBatch.js";
import type { PrintBatchFailure, PrintBatchProgress } from "./printBatch.js";
import {
  dropWithdrawnSubmission,
  fieldChangeText,
  flagSpotLabel,
  submissionKindLabel,
  submissionPhotoKind
} from "./submissionDisplay.js";
import { loginRequest, readMappedAccountClaim } from "./authConfig";
import {
  accountIsAdmin,
  clearStickyAdmin,
  lookupCachedAdminRole,
  noteServerAdmin,
  readStickyAdmin,
  resolvePublicAdmin
} from "./accountRoles.js";
import { FindSearchFrame } from "./findSearchLayout.js";
import { isPublicPage } from "./publicRoutes.js";
import { publishSignInError, subscribeSignInError } from "./signInError";
import { isQuietAuthRejection, PLEASE_SIGN_IN_AGAIN } from "./tokenRequest.js";
import knecoLogoBlue from "./assets/kneco-logo-blue.png";
import pfohFooterLogo from "./assets/pfoh-footer-logo.png";

const RETURN_INTENT_KEY = "pfoh-return-intent";

const thumbnailUrls = createBlobUrlCache({
  createObjectURL: (blob) => URL.createObjectURL(blob),
  revokeObjectURL: (url) => URL.revokeObjectURL(url)
});

function AuthorizedThumbnail({
  instance,
  account,
  url,
  photoKey,
  alt,
  className,
  emptyLabel
}: {
  instance: Parameters<typeof fetchAuthorizedBlob>[0];
  account: Parameters<typeof fetchAuthorizedBlob>[1];
  url: string;
  photoKey: string;
  alt: string;
  className: string;
  emptyLabel: string | null;
}) {
  const [src, setSrc] = useState("");
  const [failed, setFailed] = useState(false);
  const instanceRef = useRef(instance);
  const accountRef = useRef(account);
  instanceRef.current = instance;
  accountRef.current = account;
  const loadKey = thumbnailLoadKey(photoKey, url);

  useEffect(() => {
    const requestUrl = url;
    let active = true;
    let held = false;
    setFailed(false);
    setSrc("");

    void (async () => {
      try {
        const blob = await fetchAuthorizedBlob(instanceRef.current, accountRef.current, requestUrl);
        if (!active) return;
        held = true;
        setSrc(thumbnailUrls.hold(photoKey, blob));
      } catch {
        if (active) setFailed(true);
      }
    })();

    return () => {
      active = false;
      if (held) {
        thumbnailUrls.release(photoKey);
        held = false;
      }
    };
  }, [loadKey]);

  if (!src && !failed) {
    return <span className={className} aria-busy="true" />;
  }

  if (!src || failed) {
    if (!emptyLabel) return null;
    return <div className={`${className} ownedFlagImagePlaceholder`}>{emptyLabel}</div>;
  }

  return <img className={className} src={src} alt={alt} />;
}

const legacySubmitHonoreeAction = "submit-veteran" as const;

type ReturnIntent =
  | { action: "submit-honoree" | typeof legacySubmitHonoreeAction }
  | { action: "suggest-change"; honoreeId: number };

function isSubmitHonoreeIntent(intent: { action: string }) {
  return intent.action === "submit-honoree" || intent.action === legacySubmitHonoreeAction;
}

const publicCopy = {
  title: "HONOR A HERO",
  subtitle: "Find an honoree or submit a tribute. No cost to look or submit a hero.",
  searchHeading: "Search honoree records",
  searchHelp:
    "Search by name, military branch, rank, submitter’s name, or flag grid location. A flag grid location is the spot on the field, such as AA-10.",
  searchPlaceholder: "Enter a name, submitter, branch, rank, or flag grid (such as AA-10)",
  missingLead: "Can’t find your honoree?",
  submitHonoree: "Submit an honoree",
  suggestChange: "Suggest a change",
  loginRequired: "Login required to submit.",
  loginPrompt:
    "Log in or create an account to submit an honoree record or suggest a change. You can search and view records without an account.",
  aboveSubmit: "Your submission will be reviewed by an administrator before it appears publicly. Your name can be used to look up flags you submitted at the event kiosk.",
  newRecordConfirmation:
    "Your honoree record has been submitted for review. It will appear publicly after administrator approval.",
  editConfirmation:
    "Your suggested changes have been submitted for review. The current record will remain visible until your changes are approved.",
  pendingReview: "Pending review",
  mySubmissions: "My submissions",
  stepSearchTitle: "Search first",
  stepSearchBody: "Look for an existing record for the honoree you want to honor.",
  stepSubmitTitle: "Submit a record or suggest a change",
  stepSubmitBody:
    "If you find the honoree, open their record (login required) and select “Suggest a change.” If no record exists, select “Submit an honoree.” You’ll need to log in or create an account.",
  stepReviewTitle: "Administrator review",
  stepReviewBody:
    "An administrator reviews all new records and suggested changes. They appear publicly only after approval."
};

const blankForm: SaveHonoreeChangeRequest = {
  firstName: "",
  middleName: "",
  lastName: "",
  suffix: "",
  nickname: "",
  rank: "",
  serviceBranchId: null,
  serviceBranchCategoryId: null,
  startYear: null,
  endYear: null,
  datesUserEntry: "",
  conflictsServed: "",
  awards: "",
  description: "",
  kia: false,
  submitterPhoneNumber: "",
  submitterEmailAddress: ""
};

type AppRoute = "/find" | "/my-flags" | "/honor-a-hero" | "/flag-map" | "/admin/review" | "/admin/duplicates" | "/admin/flag-map" | "/admin/printing";

const appRoutes: AppRoute[] = [
  "/find",
  "/my-flags",
  "/honor-a-hero",
  "/flag-map",
  "/admin/review",
  "/admin/duplicates",
  "/admin/flag-map",
  "/admin/printing"
];

function normalizeAppRoute(pathname: string): AppRoute {
  const normalized = pathname.replace(/\/$/, "") || "/find";
  return appRoutes.includes(normalized as AppRoute) ? (normalized as AppRoute) : "/find";
}

function formatDate(value?: string | null) {
  if (!value) return "—";

  try {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric"
    }).format(new Date(value));
  } catch {
    return value;
  }
}

function nullableNumber(value: string) {
  if (value.trim() === "") return null;
  const parsed = Number(value);
  return Number.isNaN(parsed) ? null : parsed;
}

function statusClass(status: string) {
  return `status status-${status.toLowerCase()}`;
}

function latestRequestStatus(claim: FlagClaim) {
  return claim.latestChangeRequest?.requestStatus ?? claim.claimStatus;
}

function ownershipStatusLabel(claim: FlagClaim) {
  const latest = claim.latestChangeRequest?.requestStatus ?? claim.claimStatus;

  if (latest === "Submitted") return publicCopy.pendingReview;
  if (latest === "Approved") return "Approved";
  if (latest === "Rejected") return "Needs revision";
  if (latest === "Draft") return "Draft saved";

  return "In progress";
}

function profileClaimString(claims: Record<string, unknown> | undefined, keys: string[]) {
  if (!claims) return "";

  for (const key of keys) {
    const value = claims[key];

    if (typeof value === "string" && value.trim()) {
      return value.trim();
    }

    if (Array.isArray(value)) {
      const firstString = value.find((item) => typeof item === "string" && item.trim());

      if (typeof firstString === "string") {
        return firstString.trim();
      }
    }
  }

  return "";
}

function getAccountSubmitterContact(
  account: { username?: string; idTokenClaims?: Record<string, unknown> } | null | undefined
) {
  const claims = account?.idTokenClaims;

  return {
    email:
      profileClaimString(claims, [
        "email",
        "emails",
        "preferred_username",
        "upn",
        "unique_name",
        "signInNames.emailAddress"
      ]) ||
      account?.username ||
      "",
    phone: readMappedAccountClaim(claims, "Phone")
  };
}

function isPlaceholderDisplayName(value?: string | null) {
  const normalized = value?.trim().toLowerCase().replace(/\s+/g, " ") ?? "";
  return normalized === "" || normalized === "unknown" || normalized === "unknown unknown";
}

function displayNameWithNickname(name: string, nickname?: string | null) {
  const cleanName = name.trim();
  const cleanNickname = nickname?.trim();

  if (!cleanNickname) {
    return cleanName;
  }

  const normalizedName = cleanName.toLowerCase();
  const normalizedNickname = `(${cleanNickname.toLowerCase()})`;

  if (normalizedName.endsWith(normalizedNickname)) {
    return cleanName;
  }

  return `${cleanName} (${cleanNickname})`;
}

function displayUnassignedHonoreeName(honoree: AdminUnassignedHonoree) {
  return displayNameWithNickname(honoree.fullName, honoree.nickname);
}

type ConfirmDialogState = {
  title: string;
  message: string;
  confirmText: string;
  cancelText: string;
  tone?: "default" | "danger";
};

type CardPreviewState = {
  heading: string;
  title: string;
  phase: "loading" | "ready" | "error";
  url?: string;
  revoke?: boolean;
  message?: string;
  downloadName?: string;
  progress?: PrintBatchProgress;
  failures?: PrintBatchFailure[];
};

function printCardFailureMessage(error: unknown) {
  if (error instanceof ApiError) {
    const reference = error.requestId ? ` Reference: ${error.requestId}.` : "";
    const status = error.status ? `HTTP ${error.status}: ` : "";
    return `${status}${error.serverMessage || error.message}${reference}`;
  }
  return error instanceof Error && error.message.trim() ? error.message : String(error);
}

function reportableErrorMessage(error: unknown, fallback: string) {
  if (isQuietAuthRejection(error)) return "";

  if (error instanceof ApiError) {
    return error.message;
  }

  if (error instanceof Error && error.message.trim()) {
    return `${fallback} Details: ${error.message}. Please share this message with the developer if the issue continues.`;
  }

  return `${fallback} Please share this message with the developer if the issue continues.`;
}

function honoreeCardViewUrl(url: string) {
  const [base] = url.split("#");
  return `${base}#view=Fit&zoom=page-fit`;
}

function compareFlagSections(left: string, right: string) {
  const normalize = (value: string) => value.trim().toUpperCase();
  const leftValue = normalize(left);
  const rightValue = normalize(right);

  if (leftValue === "OTHER") return 1;
  if (rightValue === "OTHER") return -1;

  if (leftValue.length !== rightValue.length) {
    return leftValue.length - rightValue.length;
  }

  return leftValue.localeCompare(rightValue, undefined, { numeric: true });
}


export default function App() {
  const isAuthenticated = useIsAuthenticated();
  const { instance, accounts } = useMsal();
  const account = instance.getActiveAccount() ?? accounts[0];

  const [myClaims, setMyClaims] = useState<FlagClaim[]>([]);
  const [serviceBranches, setServiceBranches] = useState<ServiceBranch[]>([]);
  const [serviceBranchCategories, setServiceBranchCategories] = useState<ServiceBranchCategory[]>([]);
  const [selectedClaim, setSelectedClaim] = useState<FlagClaim | null>(null);
  const [isNominating, setIsNominating] = useState(false);
  const [form, setForm] = useState<SaveHonoreeChangeRequest>(blankForm);
  const [selectedPhoto, setSelectedPhoto] = useState<File | null>(null);
  const [selectedPhotoRotation, setSelectedPhotoRotation] = useState(0);
  const [selectedPhotoPreviewUrl, setSelectedPhotoPreviewUrl] = useState("");
  const formCardRef = useRef<HTMLElement | null>(null);
  const firstFormInputRef = useRef<HTMLInputElement | null>(null);

  const [honoreeSearchText, setHonoreeSearchText] = useState("");
  const [honoreeResults, setHonoreeResults] = useState<HonoreeSearchResult[]>([]);
  const [honoreeSearchPerformed, setHonoreeSearchPerformed] = useState(false);
  const [searchLoading, setSearchLoading] = useState(false);

  const [adminFromApi, setIsAdmin] = useState(false);
  const [cachedApiAdmin, setCachedApiAdmin] = useState(false);
  const [reviewLoadError, setReviewLoadError] = useState("");
  const [printLoadError, setPrintLoadError] = useState("");
  const [flagMapLoadError, setFlagMapLoadError] = useState("");
  const [pendingReviews, setPendingReviews] = useState<AdminReviewItem[]>([]);
  const [openDuplicateGroups, setOpenDuplicateGroups] = useState(0);
  const [pendingPhotoObjectUrl, setPendingPhotoObjectUrl] = useState("");
  const [submitPhase, setSubmitPhase] = useState("");
  const [cardPreview, setCardPreview] = useState<CardPreviewState | null>(null);
  const [versionReloadReady, setVersionReloadReady] = useState(false);
  const cardPreviewFrameRef = useRef<HTMLIFrameElement | null>(null);
  const cardPreviewRequest = useRef(0);
  const printBatchAbort = useRef<AbortController | null>(null);
  const cardPreviewRef = useRef<CardPreviewState | null>(null);
  cardPreviewRef.current = cardPreview;
  const [printQueue, setPrintQueue] = useState<AdminPrintQueueItem[]>([]);
  const [printHistory, setPrintHistory] = useState<CardPrintEvent[]>([]);
  const [printHistoryError, setPrintHistoryError] = useState("");
  const [markPrintedOpen, setMarkPrintedOpen] = useState(false);
  const [markPrintedComment, setMarkPrintedComment] = useState("");
  const [selectedPrintIds, setSelectedPrintIds] = useState<number[]>([]);
  const [adminBusyId, setAdminBusyId] = useState<number | null>(null);
  const [adminBusyAction, setAdminBusyAction] = useState<"approve-reprint" | "approve" | "reject" | null>(null);
  const [regeneratingPdfHonoreeId, setRegeneratingPdfHonoreeId] = useState<number | null>(null);
  const [queueingReprintHonoreeId, setQueueingReprintHonoreeId] = useState<number | null>(null);
  const [claimantsByHonoreeId, setClaimantsByHonoreeId] = useState<Record<number, AdminClaimantSummary[]>>({});
  const [claimantBusyHonoreeId, setClaimantBusyHonoreeId] = useState<number | null>(null);
  const [flagPositions, setFlagPositions] = useState<AdminFlagPosition[]>([]);
  const [flagPositionHonoree, setFlagPositionHonoree] = useState<HonoreeSearchResult | null>(null);
  const [assignGridHonoree, setAssignGridHonoree] = useState<HonoreeSearchResult | null>(null);
  const [selectedAssignFlagGridId, setSelectedAssignFlagGridId] = useState("");
  const [assignGridSearchText, setAssignGridSearchText] = useState("");
  const [assignGridSectionFilter, setAssignGridSectionFilter] = useState("");
  const [flagPositionBusyId, setFlagPositionBusyId] = useState<number | null>(null);
  const [flagPositionsLoading, setFlagPositionsLoading] = useState(false);
  const [flagMapExporting, setFlagMapExporting] = useState(false);
  const [flagMapExportOpen, setFlagMapExportOpen] = useState(false);
  const [flagMapExportError, setFlagMapExportError] = useState("");
  const [flagMapExportSelection, setFlagMapExportSelection] = useState<FlagMapExportField[]>(
    flagMapExportFields.map((field) => field.id)
  );
  const [showFlagPositionManager, setShowFlagPositionManager] = useState(true);
  const [flagPositionSearchText, setFlagPositionSearchText] = useState("");
  const [flagPositionSectionFilter, setFlagPositionSectionFilter] = useState("");
  const [flagPositionOccupancyFilter, setFlagPositionOccupancyFilter] = useState<"all" | "open" | "occupied" | "reserved">("all");
  const [flagPositionModalPosition, setFlagPositionModalPosition] = useState<AdminFlagPosition | null>(null);
  const [flagPositionDetailPosition, setFlagPositionDetailPosition] = useState<AdminFlagPosition | null>(null);
  const [unassignedHonorees, setUnassignedHonorees] = useState<AdminUnassignedHonoree[]>([]);
  const [selectedUnassignedHonoreeId, setSelectedUnassignedHonoreeId] = useState("");
  const [unassignedHonoreesLoading, setUnassignedHonoreesLoading] = useState(false);

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [flagSpotNotice, setFlagSpotNotice] = useState("");
  const [showHowItWorks, setShowHowItWorks] = useState(false);
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const [kioskLocked, setKioskLocked] = useState(() => rememberKioskSession(window.sessionStorage, window.location.search));
  const kioskLockedRef = useRef(kioskLocked);
  kioskLockedRef.current = kioskLocked;
  const [activeRoute, setActiveRoute] = useState<AppRoute>(() =>
    kioskSessionOn(window.sessionStorage) ? "/flag-map" : normalizeAppRoute(window.location.pathname)
  );
  const [confirmDialog, setConfirmDialog] = useState<ConfirmDialogState | null>(null);
  const [authPrompt, setAuthPrompt] = useState<ReturnIntent | null>(null);
  const confirmResolveRef = useRef<((confirmed: boolean) => void) | null>(null);

  const [savedDisplayName, setSavedDisplayName] = useState("");
  const [profileOpen, setProfileOpen] = useState(false);
  const [profileLoading, setProfileLoading] = useState(false);
  const [profileSaving, setProfileSaving] = useState(false);
  const [profileError, setProfileError] = useState("");
  const [profileNotice, setProfileNotice] = useState("");
  const [profileDisplayName, setProfileDisplayName] = useState("");
  const [profileEmail, setProfileEmail] = useState("");
  const [profilePhone, setProfilePhone] = useState("");
  const [profileOrganization, setProfileOrganization] = useState("");
  const accountKey = account?.homeAccountId ?? "";

  const displayName = useMemo(() => {
    if (savedDisplayName.trim()) {
      return savedDisplayName.trim();
    }

    if (!isPlaceholderDisplayName(account?.name)) {
      return account!.name!.trim();
    }

    if (!isPlaceholderDisplayName(account?.username)) {
      return account!.username!.trim();
    }

    return "Supporter";
  }, [savedDisplayName, account]);

  const submitterContact = useMemo(
    () => getAccountSubmitterContact(account),
    [account]
  );

  const allPrintQueueIds = useMemo(
    () => printQueue.map((item) => item.changeRequestId),
    [printQueue]
  );

  const visiblePrintHistory = useMemo(
    () => printHistory.filter((event) => event.cardCount > 0),
    [printHistory]
  );

  const allPrintItemsSelected =
    allPrintQueueIds.length > 0 &&
    allPrintQueueIds.every((id) => selectedPrintIds.includes(id));

  const printQueueMissingPdfCount = useMemo(
    () => printQueue.filter((item) => !item.honoreeId && !item.pdfUrl).length,
    [printQueue]
  );

  const claimedByMultipleCount = useMemo(
    () => myClaims.filter((claim) => claim.hasOtherClaimants).length,
    [myClaims]
  );

  const selectedPrintQueueItems = useMemo(
    () => printQueue.filter((item) => selectedPrintIds.includes(item.changeRequestId)),
    [printQueue, selectedPrintIds]
  );

  const isFindRoute = activeRoute === "/find";
  const isPublicFlagMapRoute = activeRoute === "/flag-map";
  const isMyFlagsRoute = activeRoute === "/my-flags";
  const isHonorHeroRoute = activeRoute === "/honor-a-hero";
  const isAdminReviewRoute = activeRoute === "/admin/review";
  const isDuplicatesRoute = activeRoute === "/admin/duplicates";
  const isFlagMapRoute = activeRoute === "/admin/flag-map";
  const siteNav: { route: AppRoute; label: string; show: boolean }[] = [
    { route: "/find", label: "Search", show: true },
    { route: "/flag-map", label: "Flag Map", show: true },
    { route: "/my-flags", label: publicCopy.mySubmissions, show: true }
  ];
  const adminNav: { route: AppRoute; label: string }[] = [
    { route: "/admin/review", label: "Review" },
    { route: "/admin/duplicates", label: "Duplicates" },
    { route: "/admin/printing", label: "Print Center" },
    { route: "/admin/flag-map", label: "Flag Manager" }
  ];
  const isPrintingRoute = activeRoute === "/admin/printing";
  const isAdminRoute = activeRoute.startsWith("/admin");
  const stickyAdmin = adminFromApi || readStickyAdmin(window.sessionStorage, account?.homeAccountId ?? "");
  const publicAdmin = isPublicPage(activeRoute)
    ? resolvePublicAdmin({ account, sticky: stickyAdmin, cachedApiAdmin })
    : null;
  const isAdmin = publicAdmin ? publicAdmin.isAdmin : adminFromApi;
  const chrome = kioskChrome(kioskLocked, isAdmin);

  const adminAlertItems = useMemo(
    () =>
      adminAttentionItems({
        pendingReview: pendingReviews.length,
        readyToPrint: printQueue.length,
        duplicatesForReview: openDuplicateGroups,
        multipleClaimAlerts: claimedByMultipleCount
      }).map((item) => ({ ...item, route: item.route as AppRoute })),
    [pendingReviews.length, printQueue.length, openDuplicateGroups, claimedByMultipleCount]
  );

  const adminAlertTotalCount = useMemo(
    () => adminAlertItems.reduce((total, item) => total + item.count, 0),
    [adminAlertItems]
  );

  const showInlineHowItWorks =
    isFindRoute &&
    !searchLoading &&
    !honoreeSearchPerformed &&
    honoreeSearchText.trim() === "";

  const flagPositionSections = useMemo(
    () =>
      [...new Set(flagPositions.map((position) => position.rowLabel || "Other"))]
        .sort(compareFlagSections),
    [flagPositions]
  );

  const openAssignableFlagPositions = useMemo(
    () =>
      flagPositions
        .filter((position) => position.isOpen && !position.isReserved)
        .sort((a, b) => a.flagGridName.localeCompare(b.flagGridName, undefined, { numeric: true, sensitivity: "base" })),
    [flagPositions]
  );

  const assignGridSections = useMemo(
    () =>
      [...new Set(openAssignableFlagPositions.map((position) => position.rowLabel || "Other"))]
        .sort(compareFlagSections),
    [openAssignableFlagPositions]
  );

  const filteredAssignableFlagPositions = useMemo(() => {
    const query = assignGridSearchText.trim().toLowerCase();

    return openAssignableFlagPositions.filter((position) => {
      if (assignGridSectionFilter && (position.rowLabel || "Other") !== assignGridSectionFilter) {
        return false;
      }

      if (!query) {
        return true;
      }

      return [
        position.flagGridName,
        position.rowLabel
      ]
        .filter(Boolean)
        .some((value) => value!.toLowerCase().includes(query));
    });
  }, [assignGridSearchText, assignGridSectionFilter, openAssignableFlagPositions]);

  const selectedAssignFlagGrid = useMemo(
    () =>
      openAssignableFlagPositions.find(
        (position) => String(position.flagGridId) === selectedAssignFlagGridId
      ) ?? null,
    [openAssignableFlagPositions, selectedAssignFlagGridId]
  );

  const filteredFlagPositions = useMemo(() => {
    const query = flagPositionSearchText.trim().toLowerCase();

    return flagPositions.filter((position) => {
      if (flagPositionOccupancyFilter === "open" && !position.isOpen) {
        return false;
      }

      if (flagPositionOccupancyFilter === "occupied" && (position.isOpen || position.isReserved)) {
        return false;
      }

      if (flagPositionOccupancyFilter === "reserved" && !position.isReserved) {
        return false;
      }

      if (flagPositionSectionFilter && (position.rowLabel || "Other") !== flagPositionSectionFilter) {
        return false;
      }

      if (!query) {
        return true;
      }

      return [
        position.flagGridName,
        position.rowLabel,
        position.honoreeName,
        position.rank,
        position.serviceBranchName
      ]
        .filter(Boolean)
        .some((value) => value!.toLowerCase().includes(query));
    });
  }, [flagPositionOccupancyFilter, flagPositionSearchText, flagPositionSectionFilter, flagPositions]);

  const flagPositionRows = useMemo(() => {
    const groups = filteredFlagPositions.reduce<Record<string, AdminFlagPosition[]>>((current, position) => {
      const row = position.rowLabel || "Other";
      current[row] = current[row] ?? [];
      current[row].push(position);
      return current;
    }, {});

    return Object.entries(groups)
      .sort(([left], [right]) => compareFlagSections(left, right))
      .map(([rowLabel, positions]) => ({
        rowLabel,
        positions: [...positions].sort((left, right) => {
          const leftColumn = left.columnNumber ?? Number.MAX_SAFE_INTEGER;
          const rightColumn = right.columnNumber ?? Number.MAX_SAFE_INTEGER;

          if (leftColumn !== rightColumn) {
            return leftColumn - rightColumn;
          }

          return left.flagGridName.localeCompare(right.flagGridName, undefined, { numeric: true });
        })
      }));
  }, [filteredFlagPositions]);

  const visibleOpenFlagPositionCount = useMemo(
    () => filteredFlagPositions.filter((position) => position.isOpen).length,
    [filteredFlagPositions]
  );

  const visibleReservedFlagPositionCount = useMemo(
    () => filteredFlagPositions.filter((position) => position.isReserved).length,
    [filteredFlagPositions]
  );

  const openFlagPositionCount = useMemo(
    () => flagPositions.filter((position) => position.isOpen).length,
    [flagPositions]
  );

  const reservedFlagPositionCount = useMemo(
    () => flagPositions.filter((position) => position.isReserved).length,
    [flagPositions]
  );

  const occupiedFlagPositionCount = useMemo(
    () => flagPositions.filter((position) => !position.isOpen && !position.isReserved).length,
    [flagPositions]
  );

  const flagPositionAssignmentOptions = useMemo(
    () =>
      honoreeResults
        .map((honoree) => ({
          honoree,
          isAssigned: !!honoree.flagGrid
        }))
        .sort((left, right) =>
          displayNameWithNickname(left.honoree.fullName, left.honoree.nickname).localeCompare(
            displayNameWithNickname(right.honoree.fullName, right.honoree.nickname)
          )
        ),
    [honoreeResults]
  );


  const filteredServiceBranches = useMemo(() => {
    if (!form.serviceBranchCategoryId) return [];

    return serviceBranches.filter(
      (branch) => branch.serviceBranchCategoryId === form.serviceBranchCategoryId
    );
  }, [form.serviceBranchCategoryId, serviceBranches]);

  const activeRouteRef = useRef(activeRoute);
  activeRouteRef.current = activeRoute;

  useEffect(() => subscribeSignInError((message) => {
    if (kioskLockedRef.current) return;
    if (isPublicPage(activeRouteRef.current)) return;
    if (message) setError(message);
  }), []);

  useEffect(() => startVersionWatch({
    loadedVersion: packageInfo.version,
    kiosk: () => kioskLockedRef.current,
    loadRemote: async () => {
      const response = await fetch(versionUrl(import.meta.env.VITE_API_BASE_URL), {
        cache: "no-store",
        headers: { Accept: "application/json" }
      });
      if (!response.ok) return "";
      const body = await response.json() as { version?: string };
      return body.version ?? "";
    },
    onPrompt: () => setVersionReloadReady(true),
    onReload: () => {
      if (!kioskLockedRef.current) return;
      window.location.reload();
    },
    isHidden: () => document.hidden
  }), []);

  useEffect(() => {
    return () => {
      const current = cardPreviewRef.current;
      if (current?.revoke && current.url) URL.revokeObjectURL(current.url);
    };
  }, []);

  const profileLoadGate = useRef(createProfileLoadGate());
  const profileAccountRef = useRef(account);
  const profileInstanceRef = useRef(instance);
  profileAccountRef.current = account;
  profileInstanceRef.current = instance;

  useEffect(() => {
    if (isPublicPage(activeRoute)) {
      return;
    }

    if (kioskLocked || !isAuthenticated || !accountKey) {
      profileLoadGate.current.reset();
      setSavedDisplayName("");
      return;
    }

    if (!profileLoadGate.current.claim(accountKey)) {
      return;
    }

    const currentAccount = profileAccountRef.current;
    if (!currentAccount) {
      profileLoadGate.current.release(accountKey);
      return;
    }

    let cancelled = false;

    profileApi.get(profileInstanceRef.current, currentAccount)
      .then((profile) => {
        if (cancelled) return;

        profileLoadGate.current.settle(accountKey);
        setSavedDisplayName((current) => {
          if (current.trim()) return current;
          return profile.hasSavedDisplayName ? profile.displayName : "";
        });
        if (profile.phoneNeedsCorrection) {
          setError(usPhoneErrorMessage);
        }
      })
      .catch(() => {
        if (cancelled) return;
        profileLoadGate.current.settle(accountKey);
        setSavedDisplayName("");
      });

    return () => {
      cancelled = true;
      profileLoadGate.current.release(accountKey);
    };
  }, [isAuthenticated, accountKey, kioskLocked, activeRoute]);

  async function openProfile() {
    if (kioskLockedRef.current || !account) return;

    setProfileOpen(true);
    setProfileError("");
    setProfileNotice("");
    setProfileLoading(true);

    try {
      const profile = await profileApi.get(instance, account);
      const claimPhone = readMappedAccountClaim(account.idTokenClaims, "Phone");
      const normalizedPhone = normalizeUsPhone(profile.phone) || normalizeUsPhone(claimPhone);
      setProfileDisplayName(profile.displayName);
      setProfileEmail(profile.email);
      setProfilePhone(normalizedPhone ? formatUsPhone(normalizedPhone) : "");
      setProfileOrganization(profile.organization || readMappedAccountClaim(account.idTokenClaims, "Organization"));
      setSavedDisplayName(profile.hasSavedDisplayName ? profile.displayName : "");
      if (!normalizedPhone && (profile.phoneNeedsCorrection || (claimPhone.trim() && !normalizeUsPhone(claimPhone)))) {
        setProfileError(usPhoneErrorMessage);
      }
    } catch (err) {
      setProfileError(reportableErrorMessage(err, "Unable to load your profile."));
    } finally {
      setProfileLoading(false);
    }
  }

  async function saveProfile(event: FormEvent) {
    event.preventDefault();
    if (!account) return;

    const trimmed = profileDisplayName.trim();
    const phone = normalizeUsPhone(profilePhone);
    const organization = profileOrganization.trim();
    if (!trimmed) {
      setProfileError("Name is required.");
      setProfileNotice("");
      return;
    }

    if (trimmed.length > displayNameMaxLength) {
      setProfileError(`Name must be ${displayNameMaxLength} characters or fewer.`);
      setProfileNotice("");
      return;
    }

    if (!profileEmail.trim()) {
      setProfileError("Email is required.");
      setProfileNotice("");
      return;
    }

    if (!phone) {
      setProfileError(usPhoneErrorMessage);
      setProfileNotice("");
      return;
    }

    if (organization.length > organizationMaxLength) {
      setProfileError(`Organization must be ${organizationMaxLength} characters or fewer.`);
      setProfileNotice("");
      return;
    }

    setProfileSaving(true);
    setProfileError("");
    setProfileNotice("");

    try {
      const profile = await profileApi.update(instance, account, {
        displayName: trimmed,
        phone,
        organization
      });
      setSavedDisplayName(profile.displayName);
      setProfileDisplayName(profile.displayName);
      setProfileEmail(profile.email);
      setProfilePhone(formatUsPhone(profile.phone));
      setProfileOrganization(profile.organization);
      setProfileNotice("Profile saved. The header name is updated.");
    } catch (err) {
      setProfileError(reportableErrorMessage(err, "Unable to save your profile."));
    } finally {
      setProfileSaving(false);
    }
  }

  async function signIn(mode: "login" | "create" = "login", intent?: ReturnIntent | null) {
    if (kioskLockedRef.current) return;

    if (intent) {
      sessionStorage.setItem(RETURN_INTENT_KEY, JSON.stringify(intent));
    } else {
      sessionStorage.removeItem(RETURN_INTENT_KEY);
    }

    setAuthPrompt(null);
    setMobileNavOpen(false);

    try {
      await instance.loginRedirect({
        ...loginRequest,
        prompt: mode === "create" ? "create" : "login",
        redirectStartPage: window.location.href
      });
    } catch (err) {
      setError(publishSignInError(err));
    }
  }

  async function signOut() {
    if (kioskLockedRef.current) return;
    clearStickyAdmin(window.sessionStorage, account?.homeAccountId);
    setIsAdmin(false);
    setCachedApiAdmin(false);
    await instance.logoutRedirect();
  }

  function leaveKiosk() {
    clearKioskSession(window.sessionStorage);
    kioskLockedRef.current = false;
    setKioskLocked(false);
    setActiveRoute("/find");
    setMobileNavOpen(false);
    window.history.pushState(null, "", "/find");
  }

  function navigateToRoute(route: AppRoute, options?: { replace?: boolean }) {
    if (kioskLockedRef.current) {
      setActiveRoute("/flag-map");
      setMobileNavOpen(false);
      if ((window.location.pathname.replace(/\/$/, "") || "/") !== "/flag-map") {
        window.history.replaceState({ pfohKiosk: 1 }, "", "/flag-map");
      }
      return;
    }

    setActiveRoute(route);
    setMobileNavOpen(false);

    if (route !== "/honor-a-hero") {
      setIsNominating(false);
    }

    if (route !== "/my-flags") {
      setSelectedClaim(null);
    }

    if (window.location.pathname !== route) {
      if (options?.replace) {
        window.history.replaceState(null, "", route);
      } else {
        window.history.pushState(null, "", route);
      }
    }

    window.setTimeout(() => {
      window.scrollTo({ top: 0, behavior: "smooth" });
    }, 40);
  }

  function handleRouteLink(event: MouseEvent<HTMLAnchorElement>, route: AppRoute) {
    event.preventDefault();
    navigateToRoute(route);
  }

  function requestConfirmation(
    message: string,
    options?: {
      title?: string;
      confirmText?: string;
      cancelText?: string;
      tone?: "default" | "danger";
    }
  ) {
    return new Promise<boolean>((resolve) => {
      confirmResolveRef.current = resolve;
      setConfirmDialog({
        title: options?.title ?? "Please confirm",
        message,
        confirmText: options?.confirmText ?? "Continue",
        cancelText: options?.cancelText ?? "Cancel",
        tone: options?.tone ?? "default"
      });
    });
  }

  function resolveConfirmation(confirmed: boolean) {
    confirmResolveRef.current?.(confirmed);
    confirmResolveRef.current = null;
    setConfirmDialog(null);
  }

  useEffect(() => {
    const handlePopState = () => {
      if (kioskLockedRef.current) {
        const stay = kioskBackTarget(window.location.pathname, true) ?? "/flag-map";
        window.history.pushState({ pfohKiosk: 1 }, "", stay);
        setActiveRoute("/flag-map");
        setMobileNavOpen(false);
        return;
      }

      const nextRoute = normalizeAppRoute(window.location.pathname);
      setActiveRoute(nextRoute);
      setMobileNavOpen(false);

      if (nextRoute !== "/honor-a-hero") {
        setIsNominating(false);
      }

      if (nextRoute !== "/my-flags") {
        setSelectedClaim(null);
      }
    };

    window.addEventListener("popstate", handlePopState);
    return () => window.removeEventListener("popstate", handlePopState);
  }, []);

  useEffect(() => {
    if (!kioskLocked) return;
    const stay = kioskStayHref(window.location.href);
    const current = `${window.location.pathname}${window.location.search}`;
    if (current !== stay) {
      window.history.replaceState({ pfohKiosk: 1 }, "", stay);
    }
    window.history.pushState({ pfohKiosk: 1 }, "", stay || "/flag-map");
    setActiveRoute("/flag-map");
  }, [kioskLocked]);

  useEffect(() => {
    if (!kioskLocked) return;
    const onClick = (event: globalThis.MouseEvent) => {
      const target = event.target;
      if (!(target instanceof Element)) return;
      const anchor = target.closest("a");
      if (!anchor) return;
      const href = anchor.href || anchor.getAttribute("href") || "";
      if (!kioskLinkLeavesMap(href, window.location.href)) return;
      event.preventDefault();
      event.stopPropagation();
      setActiveRoute("/flag-map");
      if ((window.location.pathname.replace(/\/$/, "") || "/") !== "/flag-map") {
        window.history.replaceState({ pfohKiosk: 1 }, "", "/flag-map");
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target;
      const typing = target instanceof HTMLElement && Boolean(target.closest("input, textarea"));
      if (kioskBlocksNavigationKey(event.key, event.altKey, typing)) event.preventDefault();
    };
    document.addEventListener("click", onClick, true);
    window.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("click", onClick, true);
      window.removeEventListener("keydown", onKeyDown);
    };
  }, [kioskLocked]);

  useEffect(() => {
    if (activeRoute !== "/honor-a-hero" || !account || selectedClaim || isNominating) {
      return;
    }

    setSelectedPhoto(null);
    setSelectedPhotoRotation(0);
    setSelectedClaim(null);
    setIsNominating(true);
    setForm(applySubmitterProfileDefaults(blankForm));
    setNotice("");
    setError("");
  }, [activeRoute, account, selectedClaim, isNominating, submitterContact.email, submitterContact.phone]);

  useEffect(() => {
    if (kioskLocked || !isAuthenticated || !account) return;

    const raw = sessionStorage.getItem(RETURN_INTENT_KEY);
    if (!raw) return;

    let cancelled = false;

    void (async () => {
      await Promise.resolve();
      if (cancelled) return;

      sessionStorage.removeItem(RETURN_INTENT_KEY);

      let intent: ReturnIntent;
      try {
        intent = JSON.parse(raw) as ReturnIntent;
      } catch {
        return;
      }

      if (isSubmitHonoreeIntent(intent)) {
        beginNomination();
        return;
      }

      if (intent.action === "suggest-change") {
        await suggestChange(intent.honoreeId);
      }
    })();

    return () => {
      cancelled = true;
    };

    // The return intent is read once after sign-in. The handlers below close over the signed-in account.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAuthenticated, account?.homeAccountId, kioskLocked]);

  async function loadAdminPanel<T>(name: string, fallback: string, load: () => Promise<T>) {
    try {
      const value = await load();
      return { name, ok: true as const, status: 200, message: "", value };
    } catch (err) {
      return {
        name,
        ok: false as const,
        status: err instanceof ApiError ? err.status : 0,
        message: reportableErrorMessage(err, fallback),
        value: null
      };
    }
  }

  async function loadAdminData() {
    if (!account) return;

    const [pendingResult, queueResult, positionsResult] = await Promise.all([
      loadAdminPanel("review", "Pending honoree reviews could not be loaded.", () => adminApi.pending(instance, account)),
      loadAdminPanel("printing", "The honoree print queue could not be loaded.", () => adminApi.printQueue(instance, account)),
      loadAdminPanel("flagMap", "The flag map could not be loaded.", () => adminApi.flagPositions(instance, account))
    ]);

    const decision = decideAdminLoad([pendingResult, queueResult, positionsResult]);
    if (!decision.isAdmin) {
      noteServerAdmin(window.sessionStorage, account.homeAccountId, false);
      setIsAdmin(false);
      setCachedApiAdmin(false);
      setPendingReviews([]);
      setOpenDuplicateGroups(0);
      setPrintQueue([]);
      setPrintHistory([]);
      setPrintHistoryError("");
      setFlagPositions([]);
      setSelectedPrintIds([]);
      setReviewLoadError("");
      setPrintLoadError("");
      setFlagMapLoadError("");
      return;
    }

    noteServerAdmin(window.sessionStorage, account.homeAccountId, true);
    setIsAdmin(true);
    setReviewLoadError(decision.errors.review ?? "");
    setPrintLoadError(decision.errors.printing ?? "");
    setFlagMapLoadError(decision.errors.flagMap ?? "");

    if (pendingResult.ok) {
      setPendingReviews(pendingResult.value.items);
      setOpenDuplicateGroups(pendingResult.value.openDuplicateGroupCount);
    } else {
      setPendingReviews([]);
      setOpenDuplicateGroups(0);
    }

    if (queueResult.ok) {
      setPrintQueue(queueResult.value);
      setSelectedPrintIds((current) =>
        current.filter((id) => queueResult.value.some((item) => item.changeRequestId === id))
      );
    } else {
      setPrintQueue([]);
      setSelectedPrintIds([]);
    }

    if (positionsResult.ok) {
      setFlagPositions(positionsResult.value);
    } else {
      setFlagPositions([]);
    }

    void loadPrintHistory();
  }

  async function loadPrintHistory() {
    if (!account) return;

    try {
      setPrintHistory(await adminApi.printHistory(instance, account));
      setPrintHistoryError("");
    } catch (err) {
      setPrintHistoryError(reportableErrorMessage(err, "Print history could not be loaded."));
    }
  }

  async function loadData() {
    if (!account) return;

    setLoading(true);
    setError("");

    try {
      const [claims, branches, categories] = await withDeadline(
        () => Promise.all([
          flagClaimApi.mine(instance, account),
          lookupApi.serviceBranches(),
          lookupApi.serviceBranchCategories()
        ]),
        20000,
        "Loading your submissions timed out."
      );

      setMyClaims(claims);
      setServiceBranches(branches);
      setServiceBranchCategories(categories);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to load flag data."));
    } finally {
      setLoading(false);
    }

    void loadAdminData();
  }

  const stickyAccountRef = useRef<string | null>(null);
  useEffect(() => {
    const previous = stickyAccountRef.current;
    if (previous === accountKey) return;
    if (previous) clearStickyAdmin(window.sessionStorage, previous);
    stickyAccountRef.current = accountKey;
    setCachedApiAdmin(false);
    setIsAdmin(accountKey ? readStickyAdmin(window.sessionStorage, accountKey) : false);
  }, [accountKey]);

  useEffect(() => {
    const current = profileAccountRef.current;
    if (!isPublicPage(activeRoute) || !current) return;
    if (adminFromApi || accountIsAdmin(current) || readStickyAdmin(window.sessionStorage, current.homeAccountId)) return;

    let cancelled = false;
    lookupCachedAdminRole({
      account: current,
      scopes: loginRequest.scopes,
      cacheLookupPolicy: CacheLookupPolicy.AccessToken,
      acquireTokenSilent: (request: SilentRequest) => instance.acquireTokenSilent(request)
    }).then((result) => {
      if (!cancelled && result.isAdmin) setCachedApiAdmin(true);
    });

    return () => {
      cancelled = true;
    };
  }, [activeRoute, accountKey, adminFromApi, instance]);

  useEffect(() => {
    if (!kioskChrome(kioskLocked, true).loadSignedInData) return;
    if (isPublicPage(activeRoute)) return;
    if (isAuthenticated && account) {
      loadData();
    }

    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAuthenticated, account?.homeAccountId, kioskLocked, activeRoute]);

  useEffect(() => {
    const timers: number[] = [];

    if (error) {
      timers.push(window.setTimeout(() => setError(""), 8000));
    }

    if (notice) {
      timers.push(window.setTimeout(() => setNotice(""), 12000));
    }

    return () => {
      timers.forEach((timer) => window.clearTimeout(timer));
    };
  }, [error, notice]);

  useEffect(() => {
    if (!selectedClaim && !isNominating) return;

    const timer = window.setTimeout(() => {
      formCardRef.current?.scrollIntoView({ behavior: "smooth", block: "start" });
      firstFormInputRef.current?.focus({ preventScroll: true });
    }, 75);

    return () => window.clearTimeout(timer);
  }, [selectedClaim, isNominating]);

  useEffect(() => {
    if (!selectedPhoto) {
      setSelectedPhotoPreviewUrl("");
      return;
    }

    const previewUrl = URL.createObjectURL(selectedPhoto);
    setSelectedPhotoPreviewUrl(previewUrl);

    return () => URL.revokeObjectURL(previewUrl);
  }, [selectedPhoto]);

  const pendingChangeRequestPhotoId =
    !isNominating && isPendingHonoreePhoto(selectedClaim?.latestChangeRequest?.photoFileName)
      ? selectedClaim?.latestChangeRequest?.id ?? null
      : null;

  useEffect(() => {
    if (!account || pendingChangeRequestPhotoId === null) {
      setPendingPhotoObjectUrl("");
      return;
    }

    let cancelled = false;
    let objectUrl = "";
    setPendingPhotoObjectUrl("");

    void (async () => {
      try {
        const blob = await fetchAuthorizedBlob(
          instance,
          account,
          changeRequestPhotoUrl(pendingChangeRequestPhotoId)
        );
        if (cancelled) {
          return;
        }

        objectUrl = URL.createObjectURL(blob);
        setPendingPhotoObjectUrl(objectUrl);
      } catch {
        if (!cancelled) {
          setPendingPhotoObjectUrl("");
        }
      }
    })();

    return () => {
      cancelled = true;
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
    };
  }, [accountKey, instance, pendingChangeRequestPhotoId]);

  async function searchHonorees(event: React.FormEvent) {
    event.preventDefault();

    setSearchLoading(true);
    setError("");
    setNotice("");

    try {
      const results = await honoreeApi.search(honoreeSearchText, 25);
      setHonoreeResults(results);
      setHonoreeSearchPerformed(true);
      setClaimantsByHonoreeId({});
      void loadClaimantsForSearchResults(results);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to search honorees."));
    } finally {
      setSearchLoading(false);
    }
  }

  function clearHonoreeSearch() {
    setHonoreeSearchText("");
    setHonoreeResults([]);
    setHonoreeSearchPerformed(false);
    setClaimantsByHonoreeId({});
  }

  async function loadClaimantsForSearchResults(results: HonoreeSearchResult[]) {
    if (!account || !isAdmin || results.length === 0) {
      return;
    }

    try {
      const claimantEntries = await Promise.all(
        results.map(async (honoree) => {
          const claimants = await flagClaimApi.claimantsForHonoree(instance, account, honoree.id);
          return [honoree.id, claimants] as const;
        })
      );

      const claimedOnly = claimantEntries.reduce<Record<number, AdminClaimantSummary[]>>(
        (current, [honoreeId, claimants]) => {
          if (claimants.length > 0) {
            current[honoreeId] = claimants;
          }

          return current;
        },
        {}
      );

      setClaimantsByHonoreeId(claimedOnly);
    } catch {
      // Claimant visibility is helpful for admins, but a claimant lookup error
      // should not block the public honoree search results from displaying.
    }
  }

  function applySubmitterProfileDefaults(
    currentForm: SaveHonoreeChangeRequest
  ): SaveHonoreeChangeRequest {
    return {
      ...currentForm,
      submitterPhoneNumber:
        currentForm.submitterPhoneNumber?.trim() || formatUsPhone(submitterContact.phone),
      submitterEmailAddress:
        currentForm.submitterEmailAddress?.trim() || submitterContact.email
    };
  }

  function setFormFromClaim(claim: FlagClaim) {
    const draft = claim.latestChangeRequest;

    const nextForm: SaveHonoreeChangeRequest = {
      firstName: draft?.firstName ?? "",
      middleName: draft?.middleName ?? "",
      lastName: draft?.lastName ?? "",
      suffix: draft?.suffix ?? "",
      nickname: draft?.nickname ?? "",
      rank: draft?.rank ?? "",
      serviceBranchId: draft?.serviceBranchId ?? null,
      serviceBranchCategoryId: draft?.serviceBranchCategoryId ?? null,
      startYear: draft?.startYear ?? null,
      endYear: draft?.endYear ?? null,
      datesUserEntry: draft?.datesUserEntry ?? "",
      conflictsServed: draft?.conflictsServed ?? "",
      awards: draft?.awards ?? "",
      description: draft?.description ?? "",
      kia: draft?.kia ?? false,
      submitterPhoneNumber: displayUsPhone(draft?.submitterPhoneNumber ?? ""),
      submitterEmailAddress: draft?.submitterEmailAddress ?? ""
    };

    setForm(applySubmitterProfileDefaults(nextForm));
  }

  function beginEdit(claim: FlagClaim) {
    setIsNominating(false);
    setSelectedPhoto(null);
    setSelectedPhotoRotation(0);
    setSelectedClaim(claim);
    setFormFromClaim(claim);
    setNotice("");
    setError("");
    navigateToRoute("/my-flags");
  }

  function beginNomination() {
    if (!account) {
      setAuthPrompt({ action: "submit-honoree" });
      return;
    }

    setAuthPrompt(null);
    setSelectedPhoto(null);
    setSelectedPhotoRotation(0);
    setSelectedClaim(null);
    setIsNominating(true);
    setForm(applySubmitterProfileDefaults(blankForm));
    setNotice("");
    setError("");
    navigateToRoute("/honor-a-hero");
  }

  async function suggestChange(honoreeId: number) {
    if (!account) {
      setAuthPrompt({ action: "suggest-change", honoreeId });
      return;
    }

    setAuthPrompt(null);
    setSaving(true);
    setError("");
    setNotice("");

    try {
      const claim = await flagClaimApi.claimHonoree(instance, account, honoreeId);
      await loadData();
      beginEdit(claim);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to open this record for a suggested change."));
    } finally {
      setSaving(false);
    }
  }


  async function unclaimFlag(claim: FlagClaim) {
    if (!account) return;

    const ok = await requestConfirmation(
      `Withdraw your submission for ${claim.honoreeName || "this honoree"}? It will be removed from My submissions.`,
      { title: "Withdraw submission", confirmText: "Withdraw", tone: "danger" }
    );

    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice(`Withdrawing ${claim.honoreeName || "this submission"}...`);

    try {
      await flagClaimApi.unclaim(instance, account, claim.id);

      const remaining = dropWithdrawnSubmission(myClaims, pendingReviews, claim.id);
      setMyClaims(remaining.claims);
      setPendingReviews(remaining.reviews);

      if (selectedClaim?.id === claim.id) {
        setSelectedClaim(null);
        setForm(blankForm);
        setSelectedPhoto(null);
        setSelectedPhotoRotation(0);
      }

      void loadData();
      setNotice(`${claim.honoreeName || "This submission"} was withdrawn.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to withdraw this submission."));
      setNotice("");
    } finally {
      setSaving(false);
    }
  }

  async function beginAdminDirectEdit(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can directly edit and queue a reprint.");
      return;
    }

    const ok = await requestConfirmation(
      `Edit ${displayNameWithNickname(honoree.fullName, honoree.nickname)}'s flag record directly and queue a card reprint after saving?`,
      { title: "Start admin edit", confirmText: "Start edit" }
    );

    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const claim = await flagClaimApi.startAdminEdit(instance, account, honoree.id);
      beginEdit(claim);
      setNotice("Admin edit started. Save changes and queue the reprint when ready.");
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to start admin edit."));
    } finally {
      setSaving(false);
    }
  }



  async function viewClaimants(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can view claimants.");
      return;
    }

    if (claimantsByHonoreeId[honoree.id]) {
      setClaimantsByHonoreeId((current) => {
        const next = { ...current };
        delete next[honoree.id];
        return next;
      });
      return;
    }

    setClaimantBusyHonoreeId(honoree.id);
    setError("");

    try {
      const claimants = await flagClaimApi.claimantsForHonoree(instance, account, honoree.id);
      setClaimantsByHonoreeId((current) => ({
        ...current,
        [honoree.id]: claimants
      }));
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to load claimants for this honoree."));
    } finally {
      setClaimantBusyHonoreeId(null);
    }
  }

  async function queueHonoreeReprint(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can add honorees to the reprint queue.");
      return;
    }

    // This action is safe/idempotent and is often triggered from a mobile
    // details/dropdown menu. Run it directly instead of opening a confirmation
    // dialog so iPhone/iPad Safari does not swallow the tap.
    setQueueingReprintHonoreeId(honoree.id);
    setError("");
    setNotice(`Adding ${displayNameWithNickname(honoree.fullName, honoree.nickname)} to the reprint queue...`);

    try {
      await adminApi.queueHonoreeReprint(instance, account, honoree.id);
      await loadData();
      setNotice(`${displayNameWithNickname(honoree.fullName, honoree.nickname)} was added to the reprint queue.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to regenerate the PDF or add honoree to the reprint queue."));
      setNotice("");
    } finally {
      setQueueingReprintHonoreeId(null);
    }
  }

  async function regenerateHonoreePdf(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can regenerate honoree PDFs.");
      return;
    }

    const ok = await requestConfirmation(
      `Generate a new PDF for ${displayNameWithNickname(honoree.fullName, honoree.nickname)} and overwrite the existing stored PDF?`,
      { title: "Regenerate PDF", confirmText: "Regenerate PDF" }
    );

    if (!ok) return;

    setRegeneratingPdfHonoreeId(honoree.id);
    setError("");
    setNotice("");

    try {
      const result = await adminApi.regenerateHonoreePdf(instance, account, honoree.id);

      if (result.uploaded) {
        setNotice(result.message || `PDF regenerated for ${displayNameWithNickname(honoree.fullName, honoree.nickname)}: ${result.fileName}`);
      } else {
        setError(result.message || "PDF was generated but could not be saved.");
      }
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to regenerate PDF."));
    } finally {
      setRegeneratingPdfHonoreeId(null);
    }
  }

  function handlePhotoSelected(file?: File | null) {
    setSelectedPhoto(file ?? null);
    setSelectedPhotoRotation(0);
  }

  function currentPhotoSourceUrl() {
    if (isNominating) {
      return "";
    }

    const request = selectedClaim?.latestChangeRequest;
    if (request && isPendingHonoreePhoto(request.photoFileName)) {
      return changeRequestPhotoUrl(request.id);
    }

    if (selectedClaim?.honoreeId) {
      return honoreePhotoUrl(selectedClaim.honoreeId);
    }

    return selectedClaim?.honoreeImageUrl ?? "";
  }

  function normalizeRotation(degrees: number) {
    const next = degrees % 360;
    return next < 0 ? next + 360 : next;
  }

  function rotateSelectedPhoto(degrees: number) {
    setSelectedPhotoRotation((current) => normalizeRotation(current + degrees));
  }

  async function rotateDisplayedPhoto(degrees: number) {
    if (selectedPhoto) {
      rotateSelectedPhoto(degrees);
      return;
    }

    try {
      const currentPhoto = await fetchCurrentPhotoAsFile();

      if (!currentPhoto) {
        setError("Select a photo before rotating it.");
        return;
      }

      setError("");
      setSelectedPhoto(currentPhoto);
      setSelectedPhotoRotation(normalizeRotation(degrees));
      setNotice("Photo rotation is staged. Save or submit the form to apply it.");
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to load the current photo for rotation."));
    }
  }

  async function fetchCurrentPhotoAsFile() {
    const sourceUrl = currentPhotoSourceUrl();

    if (!sourceUrl) {
      return null;
    }

    let blob: Blob;
    if (sourceUrl.includes("/api/flagclaims/change-requests/")) {
      if (!account) {
        return null;
      }

      blob = await fetchAuthorizedBlob(instance, account, sourceUrl);
    } else {
      const response = await fetch(sourceUrl, { cache: "no-store" });

      if (!response.ok) {
        throw new Error("Unable to load the current photo for rotation.");
      }

      blob = await response.blob();
    }
    const contentType = blob.type || "image/jpeg";
    const extension = contentType.includes("png") ? "png" : "jpg";

    return new File([blob], `current-honoree-photo.${extension}`, {
      type: contentType,
      lastModified: Date.now()
    });
  }

  async function getSelectedPhotoForUpload() {
    if (!selectedPhoto || selectedPhotoRotation % 360 === 0) {
      return selectedPhoto;
    }

    return rotateImageFile(selectedPhoto, selectedPhotoRotation);
  }

  async function resizeHonoreePhoto(file: File, image: HTMLImageElement) {
    const longest = Math.max(image.naturalWidth, image.naturalHeight);
    const scale = longest > 1600 ? 1600 / longest : 1;
    const canvas = document.createElement("canvas");
    canvas.width = Math.max(1, Math.round(image.naturalWidth * scale));
    canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
    const context = canvas.getContext("2d");
    if (!context) {
      throw new Error("Unable to prepare the photo.");
    }

    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob(
        (createdBlob) => {
          if (createdBlob) {
            resolve(createdBlob);
          } else {
            reject(new Error("Unable to prepare the photo."));
          }
        },
        "image/jpeg",
        0.82
      );
    });
    const name = file.name.replace(/\.\w+$/, "") || "honoree-photo";
    return new File([blob], `${name}.jpg`, { type: "image/jpeg", lastModified: Date.now() });
  }

  async function prepareSubmissionPhoto() {
    setSubmitPhase("Preparing photo...");
    return withDeadline(async () => {
      const file = await getSelectedPhotoForUpload();
      if (!file) return null;

      const image = await loadImageFromFile(file);
      if (!shouldCompressHonoreePhoto(file, image.naturalWidth, image.naturalHeight)) {
        return file;
      }

      return resizeHonoreePhoto(file, image);
    }, 20000, "Preparing the photo took too long. Try a smaller photo.");
  }

  function rememberSubmission(claim: FlagClaim) {
    setMyClaims((current) => [claim, ...current.filter((item) => item.id !== claim.id)]);
  }

  async function rotateImageFile(file: File, rotationDegrees: number): Promise<File> {
    const image = await loadImageFromFile(file);
    const normalizedRotation = ((rotationDegrees % 360) + 360) % 360;
    const swapDimensions = normalizedRotation === 90 || normalizedRotation === 270;
    const canvas = document.createElement("canvas");

    canvas.width = swapDimensions ? image.naturalHeight : image.naturalWidth;
    canvas.height = swapDimensions ? image.naturalWidth : image.naturalHeight;

    const context = canvas.getContext("2d");
    if (!context) {
      return file;
    }

    switch (normalizedRotation) {
      case 90:
        context.translate(canvas.width, 0);
        context.rotate(Math.PI / 2);
        break;
      case 180:
        context.translate(canvas.width, canvas.height);
        context.rotate(Math.PI);
        break;
      case 270:
        context.translate(0, canvas.height);
        context.rotate(-Math.PI / 2);
        break;
      default:
        break;
    }

    context.drawImage(image, 0, 0);

    const outputType = file.type === "image/png" ? "image/png" : "image/jpeg";
    const blob = await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob(
        (createdBlob) => {
          if (createdBlob) {
            resolve(createdBlob);
          } else {
            reject(new Error("Unable to rotate image."));
          }
        },
        outputType,
        outputType === "image/jpeg" ? 0.92 : undefined
      );
    });

    return new File([blob], file.name, {
      type: outputType,
      lastModified: Date.now()
    });
  }

  function loadImageFromFile(file: File): Promise<HTMLImageElement> {
    return new Promise((resolve, reject) => {
      const image = new Image();
      const objectUrl = URL.createObjectURL(file);

      image.onload = () => {
        URL.revokeObjectURL(objectUrl);
        resolve(image);
      };

      image.onerror = () => {
        URL.revokeObjectURL(objectUrl);
        reject(new Error("Unable to load selected image."));
      };

      image.src = objectUrl;
    });
  }

  async function submitNomination(event: React.FormEvent) {
    event.preventDefault();

    if (!account) return;

    setSaving(true);
    setSubmitPhase("Preparing photo...");
    setError("");
    setNotice("");

    try {
      const formToSubmit = applySubmitterProfileDefaults(form);
      const photoForUpload = await prepareSubmissionPhoto();
      setForm(formToSubmit);
      setSubmitPhase("Uploading submission...");
      const claim = await withDeadline(
        () => flagClaimApi.nominate(instance, account, formToSubmit, photoForUpload),
        75000,
        "Uploading the submission took too long."
      );
      rememberSubmission(claim);
      setSubmitPhase("Finishing submission...");
      setIsNominating(false);
      setSelectedPhoto(null);
      setSelectedPhotoRotation(0);
      setForm(blankForm);
      setNotice(publicCopy.newRecordConfirmation);
      navigateToRoute("/my-flags");
      void loadData();
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to submit nomination."));
    } finally {
      setSaving(false);
      setSubmitPhase("");
    }
  }

  async function saveDraft() {
    if (!account || !selectedClaim) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const formToSave = applySubmitterProfileDefaults(form);
      const photoForUpload = await getSelectedPhotoForUpload();
      setForm(formToSave);
      await flagClaimApi.saveDraft(instance, account, selectedClaim.id, formToSave, photoForUpload);
      await loadData();
      setNotice("Draft saved.");
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to save draft."));
    } finally {
      setSaving(false);
    }
  }

  async function submitClaim(event: React.FormEvent) {
    event.preventDefault();

    if (!account || !selectedClaim) return;

    const isAdminDirectEdit = selectedClaim.claimStatus === "AdminDirectEdit";

    setSaving(true);
    setSubmitPhase("Preparing photo...");
    setError("");
    setNotice("");

    try {
      const formToSubmit = applySubmitterProfileDefaults(form);
      const photoForUpload = await prepareSubmissionPhoto();
      setForm(formToSubmit);
      setSubmitPhase("Uploading submission...");
      await withDeadline(
        () => flagClaimApi.saveDraft(instance, account, selectedClaim.id, formToSubmit, photoForUpload),
        75000,
        "Uploading the submission took too long."
      );

      if (isAdminDirectEdit) {
        const claim = await withDeadline(
          () => flagClaimApi.applyAdminEditReprint(instance, account, selectedClaim.id),
          75000,
          "Uploading the submission took too long."
        );
        rememberSubmission(claim);
        setSubmitPhase("Finishing submission...");
        setNotice("Admin edit applied and added to the reprint queue.");
      } else {
        const claim = await withDeadline(
          () => flagClaimApi.submit(instance, account, selectedClaim.id),
          75000,
          "Uploading the submission took too long."
        );
        rememberSubmission(claim);
        setSubmitPhase("Finishing submission...");
        setNotice(publicCopy.editConfirmation);
      }

      setSelectedClaim(null);
      setSelectedPhoto(null);
      setSelectedPhotoRotation(0);
      setForm(blankForm);

      if (!isAdminDirectEdit) {
        navigateToRoute("/my-flags");
      }

      void loadData();
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to submit changes."));
    } finally {
      setSaving(false);
      setSubmitPhase("");
    }
  }

  async function refreshFlagPositions() {
    if (!account || !isAdmin) return;

    setFlagPositionsLoading(true);
    setError("");

    try {
      const positions = await adminApi.flagPositions(instance, account);
      setFlagPositions(positions);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to load flag grids."));
    } finally {
      setFlagPositionsLoading(false);
    }
  }

  async function beginFlagPositionAssignment(honoree: HonoreeSearchResult) {
    if (!account || !isAdmin) return;

    setAssignGridHonoree(honoree);
    setSelectedAssignFlagGridId("");
    setAssignGridSearchText("");
    setAssignGridSectionFilter("");
    setFlagPositionHonoree(null);
    setError("");
    setNotice("");

    setFlagPositionsLoading(true);
    try {
      const positions = await adminApi.flagPositions(instance, account);
      setFlagPositions(positions);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to load open flag grids."));
    } finally {
      setFlagPositionsLoading(false);
    }
  }

  function closeAssignGridHonoreeModal() {
    setAssignGridHonoree(null);
    setSelectedAssignFlagGridId("");
    setAssignGridSearchText("");
    setAssignGridSectionFilter("");
  }

  async function assignSelectedGridToHonoree() {
    if (!account || !assignGridHonoree || !selectedAssignFlagGrid) return;

    const honoreeName = displayNameWithNickname(assignGridHonoree.fullName, assignGridHonoree.nickname);
    const flagGridName = selectedAssignFlagGrid.flagGridName;
    const ok = await requestConfirmation(
      `Assign ${honoreeName} to flag grid ${flagGridName}? The PDF will be regenerated and the card will be added to the reprint queue.`,
      { title: "Assign flag grid", confirmText: "Assign flag grid" }
    );

    if (!ok) return;

    setFlagPositionBusyId(selectedAssignFlagGrid.flagGridId);
    setError("");
    setNotice(`Assigning ${honoreeName} to ${flagGridName}...`);

    try {
      await adminApi.assignFlagPosition(instance, account, selectedAssignFlagGrid.flagGridId, assignGridHonoree.id);
      const [positions, results, unassigned, queue] = await Promise.all([
        adminApi.flagPositions(instance, account),
        honoreeSearchPerformed ? honoreeApi.search(honoreeSearchText, 25) : Promise.resolve(honoreeResults),
        adminApi.unassignedHonorees(instance, account),
        adminApi.printQueue(instance, account)
      ]);

      setFlagPositions(positions);
      setUnassignedHonorees(unassigned);
      setPrintQueue(queue);
      setSelectedPrintIds((current) =>
        current.filter((id) => queue.some((item) => item.changeRequestId === id))
      );

      if (honoreeSearchPerformed) {
        setHonoreeResults(results);
        setClaimantsByHonoreeId({});
        await loadClaimantsForSearchResults(results);
      }

      closeAssignGridHonoreeModal();
      setNotice(`${honoreeName} was assigned to ${flagGridName}, the PDF was regenerated, and the card was added to the reprint queue.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to assign flag grid."));
      setNotice("");
    } finally {
      setFlagPositionBusyId(null);
    }
  }

  function selectFlagPositionHonoree(honoreeIdValue: string) {
    const honoreeId = Number(honoreeIdValue);

    if (!honoreeId) {
      setFlagPositionHonoree(null);
      return;
    }

    const selected = honoreeResults.find((honoree) => honoree.id === honoreeId);
    if (selected) {
      setFlagPositionHonoree(selected);
      setNotice(`Select an open flag grid for ${displayNameWithNickname(selected.fullName, selected.nickname)}.`);
    }
  }

  function openFlagPositionDetail(position: AdminFlagPosition) {
    setFlagPositionDetailPosition(position);
  }

  function closeFlagPositionDetail() {
    setFlagPositionDetailPosition(null);
  }

  function assignFromFlagPositionDetail(position: AdminFlagPosition) {
    closeFlagPositionDetail();
    void openAssignFlagPositionModal(position);
  }

  async function openAssignFlagPositionModal(position: AdminFlagPosition) {
    if (!account || !position.isOpen) return;

    setFlagPositionModalPosition(position);
    setSelectedUnassignedHonoreeId(flagPositionHonoree?.id ? String(flagPositionHonoree.id) : "");
    setUnassignedHonoreesLoading(true);
    setError("");

    try {
      const honorees = await adminApi.unassignedHonorees(instance, account);
      setUnassignedHonorees(honorees);

      if (flagPositionHonoree?.id && honorees.some((honoree) => honoree.id === flagPositionHonoree.id)) {
        setSelectedUnassignedHonoreeId(String(flagPositionHonoree.id));
      } else {
        setSelectedUnassignedHonoreeId("");
      }
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to load unassigned honorees."));
    } finally {
      setUnassignedHonoreesLoading(false);
    }
  }

  function closeAssignFlagPositionModal() {
    setFlagPositionModalPosition(null);
    setSelectedUnassignedHonoreeId("");
  }

  async function assignSelectedHonoreeToPosition(position: AdminFlagPosition) {
    if (!account || !position.isOpen) return;

    const honoreeId = Number(selectedUnassignedHonoreeId);
    const selectedHonoree = unassignedHonorees.find((honoree) => honoree.id === honoreeId);

    if (!honoreeId || !selectedHonoree) {
      setError("Choose an unassigned honoree before assigning this flag grid.");
      return;
    }

    const honoreeName = displayUnassignedHonoreeName(selectedHonoree);
    const ok = await requestConfirmation(
      `Assign ${honoreeName} to flag grid ${position.flagGridName}?`,
      { title: "Assign flag grid", confirmText: "Assign" }
    );

    if (!ok) return;

    setFlagPositionBusyId(position.flagGridId);
    setError("");
    setNotice(`Assigning ${honoreeName} to ${position.flagGridName}...`);

    try {
      await adminApi.assignFlagPosition(instance, account, position.flagGridId, honoreeId);
      const [positions, results, unassigned, queue] = await Promise.all([
        adminApi.flagPositions(instance, account),
        honoreeSearchPerformed ? honoreeApi.search(honoreeSearchText, 25) : Promise.resolve(honoreeResults),
        adminApi.unassignedHonorees(instance, account),
        adminApi.printQueue(instance, account)
      ]);

      setFlagPositions(positions);
      setUnassignedHonorees(unassigned);
      setPrintQueue(queue);
      setSelectedPrintIds((current) =>
        current.filter((id) => queue.some((item) => item.changeRequestId === id))
      );
      closeFlagPositionDetail();

      if (honoreeSearchPerformed) {
        setHonoreeResults(results);
        setClaimantsByHonoreeId({});
        await loadClaimantsForSearchResults(results);
      }

      setFlagPositionHonoree(null);
      closeAssignFlagPositionModal();
      closeFlagPositionDetail();
      setNotice(`${honoreeName} was assigned to ${position.flagGridName} and added to the reprint queue.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to assign flag grid."));
      setNotice("");
    } finally {
      setFlagPositionBusyId(null);
    }
  }

  async function clearFlagPosition(position: AdminFlagPosition) {
    if (!account || position.isOpen) return;

    const honoreeName = position.honoreeName || "this honoree";
    const ok = await requestConfirmation(
      `Remove ${honoreeName} from flag grid ${position.flagGridName}?`,
      { title: "Remove from flag grid", confirmText: "Remove", tone: "danger" }
    );

    if (!ok) return;

    setFlagPositionBusyId(position.flagGridId);
    setError("");
    setNotice(`Removing ${honoreeName} from ${position.flagGridName}...`);

    try {
      await adminApi.clearFlagPosition(instance, account, position.flagGridId);
      const [positions, results, unassigned] = await Promise.all([
        adminApi.flagPositions(instance, account),
        honoreeSearchPerformed ? honoreeApi.search(honoreeSearchText, 25) : Promise.resolve(honoreeResults),
        adminApi.unassignedHonorees(instance, account)
      ]);

      setFlagPositions(positions);
      setUnassignedHonorees(unassigned);
      closeFlagPositionDetail();

      if (honoreeSearchPerformed) {
        setHonoreeResults(results);
        setClaimantsByHonoreeId({});
        await loadClaimantsForSearchResults(results);

        const removedHonoree = position.honoreeId
          ? results.find((honoree) => honoree.id === position.honoreeId)
          : null;

        if (removedHonoree) {
          setFlagPositionHonoree(removedHonoree);
          setShowFlagPositionManager(true);
          setNotice(`${honoreeName} was removed from ${position.flagGridName}. Select an open position to reassign.`);
          return;
        }
      }

      setNotice(`${honoreeName} was removed from ${position.flagGridName}.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to remove the honoree from this flag grid."));
      setNotice("");
    } finally {
      setFlagPositionBusyId(null);
    }
  }

  function openFlagMapExport() {
    if (!account) {
      void signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can export the flag map.");
      return;
    }

    setError("");
    setNotice("");
    setFlagMapExportError("");
    setFlagMapExportOpen(true);
  }

  function toggleFlagMapExportField(fieldId: FlagMapExportField) {
    setFlagMapExportSelection((current) =>
      current.includes(fieldId) ? current.filter((id) => id !== fieldId) : [...current, fieldId]
    );
  }

  async function confirmFlagMapExport() {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can export the flag map.");
      return;
    }

    const selected = flagMapExportFields
      .map((field) => field.id)
      .filter((id) => flagMapExportSelection.includes(id));

    if (selected.length === 0) {
      setFlagMapExportError("Select at least one field to export.");
      return;
    }

    setError("");
    setNotice("");
    setFlagMapExportError("");
    setFlagMapExporting(true);

    try {
      await adminApi.exportFlagMapExcel(instance, account, selected);
      setFlagMapExportOpen(false);
      setNotice("Flag map export downloaded.");
    } catch (err) {
      setFlagMapExportError(reportableErrorMessage(err, "Unable to export the flag map."));
    } finally {
      setFlagMapExporting(false);
    }
  }

  async function exportHonoreesExcel() {
    if (!account) {
      await signIn();
      return;
    }

    if (!isAdmin) {
      setError("Only PFOH administrators can export honoree lists.");
      return;
    }

    setError("");
    setNotice("");

    try {
      await adminApi.exportHonoreesExcel(instance, account);
      setNotice("Honoree export downloaded.");
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to export honorees."));
    }
  }

  function closeCardPreview() {
    cardPreviewRequest.current += 1;
    printBatchAbort.current?.abort();
    printBatchAbort.current = null;
    setCardPreview((current) => {
      if (current?.revoke && current.url) URL.revokeObjectURL(current.url);
      return null;
    });
  }

  function beginCardPreview(
    title: string,
    heading = honoreeCardHeading,
    downloadName = honoreeCardDownloadName
  ) {
    const requestId = ++cardPreviewRequest.current;
    setCardPreview((current) => {
      if (current?.revoke && current.url) URL.revokeObjectURL(current.url);
      return { heading, title, phase: "loading", downloadName };
    });
    return requestId;
  }

  function finishCardPreview(
    requestId: number,
    title: string,
    url: string,
    revoke: boolean,
    heading = honoreeCardHeading,
    downloadName = honoreeCardDownloadName
  ) {
    if (cardPreviewRequest.current !== requestId) {
      if (revoke) URL.revokeObjectURL(url);
      return;
    }

    setCardPreview({ heading, title, phase: "ready", url, revoke, downloadName });
  }

  function failCardPreview(requestId: number, title: string, message: string, heading = honoreeCardHeading) {
    if (cardPreviewRequest.current !== requestId) return;

    setCardPreview((current) => {
      if (current?.revoke && current.url) URL.revokeObjectURL(current.url);
      return { heading, title, phase: "error", message };
    });
  }

  function openCardBlob(
    title: string,
    load: () => Promise<Blob>,
    options?: { heading?: string; downloadName?: string; errorFallback?: string }
  ) {
    const heading = options?.heading ?? honoreeCardHeading;
    const downloadName = options?.downloadName ?? honoreeCardDownloadName;
    setError("");
    const requestId = beginCardPreview(title, heading, downloadName);
    void load()
      .then((blob) => finishCardPreview(requestId, title, URL.createObjectURL(blob), true, heading, downloadName))
      .catch((err) => failCardPreview(
        requestId,
        title,
        reportableErrorMessage(err, options?.errorFallback ?? "Unable to open the honoree card."),
        heading
      ));
  }

  function openSubmissionCard(claim: FlagClaim) {
    if (!account) return;

    openCardBlob(claim.honoreeName || "Honoree", () =>
      fetchAuthorizedBlob(instance, account, mySubmissionCardUrl(claim.id)));
  }

  async function openPublicHonoreeCard(honoreeId: number, title: string) {
    setError("");
    const requestId = beginCardPreview(title);
    const url = honoreePdfUrl(honoreeId);
    try {
      const response = await fetch(url, { cache: "no-store" });
      if (!response.ok) {
        throw new Error("Unable to open the honoree card.");
      }

      const type = response.headers.get("content-type") ?? "";
      if (type.includes("pdf") || type.includes("octet-stream") || type.trim() === "") {
        finishCardPreview(requestId, title, URL.createObjectURL(await response.blob()), true);
        return;
      }

      finishCardPreview(requestId, title, response.url || url, false);
    } catch (err) {
      if (err instanceof TypeError) {
        finishCardPreview(requestId, title, url, false);
        return;
      }

      failCardPreview(requestId, title, reportableErrorMessage(err, "Unable to open the honoree card."));
    }
  }

  function previewHonoreeCard(item: AdminReviewItem) {
    if (!account) return;

    openCardBlob(item.honoreeName, () =>
      fetchAuthorizedBlob(instance, account, adminCardPreviewUrl(item.changeRequestId)));
  }

  function downloadCardPreview() {
    if (!cardPreview?.url) return;

    const anchor = document.createElement("a");
    anchor.href = cardPreview.url;
    anchor.download = cardPreview.downloadName || honoreeCardDownloadName;
    anchor.rel = "noopener";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  }

  function printCardPreview() {
    const frameWindow = cardPreviewFrameRef.current?.contentWindow;
    if (!frameWindow) return;

    try {
      frameWindow.focus();
      frameWindow.print();
    } catch {
      setError("The browser could not print this honoree card from the viewer. Use Download, then print the file.");
    }
  }

  async function confirmRemovedFromField(item: AdminReviewItem) {
    if (!account) {
      setError("You must be signed in as an administrator to remove a flag from the field.");
      return;
    }

    const ok = await requestConfirmation(
      `Confirm that ${item.honoreeName}'s flag was removed from ${flagSpotLabel(item.flagGridName, item.flagGridId)}? The spot will be released and the record will stay on file.`,
      { title: "Removed from field", confirmText: "Removed from field", tone: "danger" }
    );
    if (!ok) return;

    setAdminBusyId(item.changeRequestId);
    setAdminBusyAction("approve");
    setError("");
    setNotice(`Removing ${item.honoreeName} from the field...`);

    try {
      await adminApi.removedFromField(instance, account, item.changeRequestId);
      await loadData();
      setNotice(`${item.honoreeName} was removed from the field. The record stays on file.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to confirm this flag was removed from the field."));
      setNotice("");
    } finally {
      setAdminBusyId(null);
      setAdminBusyAction(null);
    }
  }

  async function approveReview(item: AdminReviewItem, requiresCardReprint: boolean) {
    if (!account) {
      setError("You must be signed in as an administrator to approve review items.");
      return;
    }

    if (!item.changeRequestId) {
      setError("This review item is missing its change request ID. Refresh the page and try again.");
      return;
    }

    setAdminBusyId(item.changeRequestId);
    setAdminBusyAction(requiresCardReprint ? "approve-reprint" : "approve");
    setError("");
    setNotice(`${requiresCardReprint ? "Approving and queuing reprint" : "Approving"} ${item.honoreeName}...`);

    try {
      const approved = await adminApi.approve(instance, account, item.changeRequestId, requiresCardReprint);
      await loadData();
      if (approved.flagSpotAssigned === false) {
        setFlagSpotNotice(
          approved.flagSpotNotice ||
            "This honoree was approved and added, but no flag spot is available. Free a spot and assign the honoree manually in the Flag Map."
        );
      }
      setNotice(`${item.honoreeName} approved${requiresCardReprint ? " and added to the card reprint queue" : ""}.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to approve item."));
      setNotice("");
    } finally {
      setAdminBusyId(null);
      setAdminBusyAction(null);
    }
  }

  async function rejectReview(item: AdminReviewItem) {
    if (!account) {
      setError("You must be signed in as an administrator to reject review items.");
      return;
    }

    if (!item.changeRequestId) {
      setError("This review item is missing its change request ID. Refresh the page and try again.");
      return;
    }

    const reviewNotes = window.prompt(`Why are you rejecting ${item.honoreeName}?`, "Rejected by administrator.");
    if (!reviewNotes || !reviewNotes.trim()) {
      setNotice("Reject canceled. A rejection reason is required.");
      return;
    }

    setAdminBusyId(item.changeRequestId);
    setAdminBusyAction("reject");
    setError("");
    setNotice(`Rejecting ${item.honoreeName}...`);

    try {
      await adminApi.reject(instance, account, item.changeRequestId, reviewNotes.trim());
      await loadData();
      setNotice(`${item.honoreeName} rejected.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to reject item."));
      setNotice("");
    } finally {
      setAdminBusyId(null);
      setAdminBusyAction(null);
    }
  }

  async function openSelectedPrintPdf() {
    if (!account) return;

    const count = selectedPrintIds.length;
    const title = printSelectionSubtitle(count);
    if (count === 0) {
      const requestId = beginCardPreview(title, printCenterHeading);
      failCardPreview(requestId, title, "Select at least one item to print.", printCenterHeading);
      return;
    }

    const ids = [...selectedPrintIds];
    const downloadName = `${mergedPrintName(count)}.pdf`;
    setError("");
    printBatchAbort.current?.abort();
    const controller = new AbortController();
    printBatchAbort.current = controller;
    const requestId = beginCardPreview(title, printCenterHeading, downloadName);
    const isCurrent = () => cardPreviewRequest.current === requestId && !controller.signal.aborted;
    const showProgress = (progress: PrintBatchProgress) => {
      if (!isCurrent()) return;
      setCardPreview((current) => (current && current.phase === "loading" ? { ...current, progress } : current));
    };
    const failBatch = (message: string, failures?: PrintBatchFailure[]) => {
      if (!isCurrent()) return;
      setCardPreview({ heading: printCenterHeading, title, phase: "error", message, failures });
    };

    try {
      const plan = await adminApi.planPrintBatch(instance, account, ids, controller.signal);
      if (plan.notInQueue.length > 0) {
        const failures = plan.notInQueue.map((id): PrintBatchFailure => {
          const item = printQueue.find((entry) => entry.changeRequestId === id);
          return {
            card: { changeRequestId: id, honoreeId: item?.honoreeId ?? 0, honoreeName: item?.honoreeName ?? `Queue item #${id}` },
            step: "queue",
            message: "It was removed, printed, or changed after the Print Center loaded. Refresh the page and select it again."
          };
        });
        failBatch(printBatchFailureSummary(failures, ids.length), failures);
        return;
      }

      const cards = plan.cards;
      const prepared = await preparePrintBatch({
        cards,
        regeneratePdf: async (card, signal) => {
          try {
            const result = await adminApi.regenerateHonoreePdf(instance, account, card.honoreeId, signal);
            if (!result.uploaded) throw new Error(result.message || "The card PDF could not be regenerated.");
          } catch (err) {
            if (signal?.aborted) throw err;
            throw new Error(printCardFailureMessage(err));
          }
        },
        loadStoredPdf: async (card, signal) => {
          try {
            return await adminApi.fetchQueuedCardPdf(instance, account, card.changeRequestId, signal);
          } catch (err) {
            if (signal?.aborted) throw err;
            throw new Error(printCardFailureMessage(err));
          }
        },
        onProgress: showProgress,
        signal: controller.signal
      });

      if (prepared.failures.length > 0) {
        failBatch(printBatchFailureSummary(prepared.failures, cards.length), prepared.failures);
        return;
      }

      const mergedName = mergedPrintName(cards.length);
      const merged = await mergePrintBatch(cards, prepared.pdfs as ArrayBuffer[], {
        onProgress: showProgress,
        signal: controller.signal,
        regenerated: prepared.regenerated,
        title: mergedName
      });
      if (merged.failures.length > 0 || !merged.bytes) {
        failBatch(printBatchFailureSummary(merged.failures, cards.length), merged.failures);
        return;
      }

      if (!isCurrent()) return;
      const url = URL.createObjectURL(new Blob([merged.bytes as BlobPart], { type: "application/pdf" }));
      const doneTitle = `${printSelectionSubtitle(cards.length)}, merged.`;
      finishCardPreview(requestId, doneTitle, url, true, printCenterHeading, `${mergedName}.pdf`);
    } catch (err) {
      if (controller.signal.aborted) return;
      failBatch(reportableErrorMessage(err, "Unable to prepare the print PDF."));
    } finally {
      if (printBatchAbort.current === controller) printBatchAbort.current = null;
    }
  }

  function markSelectedPrinted() {
    if (!account) return;

    if (selectedPrintIds.length === 0) {
      setError("Select at least one item to mark printed.");
      return;
    }

    setMarkPrintedComment("");
    setMarkPrintedOpen(true);
  }

  function cancelMarkPrinted() {
    setMarkPrintedOpen(false);
    setMarkPrintedComment("");
  }

  async function confirmMarkPrinted() {
    if (!account || !markPrintedOpen) return;

    const comment = markPrintedComment.trim();
    if (comment.length > PRINT_EVENT_COMMENT_MAX) {
      setError(`Comments must be ${PRINT_EVENT_COMMENT_MAX} characters or fewer.`);
      return;
    }

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const result = await adminApi.markPrinted(instance, account, selectedPrintIds, comment);
      setMarkPrintedOpen(false);
      setMarkPrintedComment("");
      await loadData();
      setNotice(
        result.count === 0
          ? "No cards were marked printed, so no print history entry was recorded."
          : `${result.count} item(s) marked printed.`
      );
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to mark printed."));
    } finally {
      setSaving(false);
    }
  }

  async function removeSelectedFromReprintQueue() {
    if (!account) return;

    if (selectedPrintIds.length === 0) {
      setError("Select at least one item to remove from the reprint queue.");
      return;
    }

    const ok = await requestConfirmation(
      "Remove the selected card reprints from the reprint queue? This does not mark them printed.",
      { title: "Remove from reprint queue", confirmText: "Remove from queue", tone: "danger" }
    );
    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const result = await adminApi.removeFromReprintQueue(instance, account, selectedPrintIds);
      await loadData();
      setNotice(`${result.count} item(s) removed from the reprint queue.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to remove item(s) from the reprint queue."));
    } finally {
      setSaving(false);
    }
  }

  async function removeOneFromReprintQueue(changeRequestId: number, honoreeName: string) {
    if (!account) return;

    const ok = await requestConfirmation(
      `Remove ${honoreeName || "this card"} from the reprint queue? This does not mark it printed.`,
      { title: "Remove from reprint queue", confirmText: "Remove from queue", tone: "danger" }
    );
    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const result = await adminApi.removeFromReprintQueue(instance, account, [changeRequestId]);
      await loadData();
      setNotice(`${result.count} item(s) removed from the reprint queue.`);
    } catch (err) {
      setError(reportableErrorMessage(err, "Unable to remove this item from the reprint queue."));
    } finally {
      setSaving(false);
    }
  }

  function togglePrintSelection(changeRequestId: number) {
    setSelectedPrintIds((current) =>
      current.includes(changeRequestId)
        ? current.filter((id) => id !== changeRequestId)
        : [...current, changeRequestId]
    );
  }

  function toggleSelectAllPrintItems() {
    if (allPrintItemsSelected) {
      setSelectedPrintIds([]);
    } else {
      setSelectedPrintIds(allPrintQueueIds);
    }
  }

  function update<K extends keyof SaveHonoreeChangeRequest>(
    key: K,
    value: SaveHonoreeChangeRequest[K]
  ) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  function renderCardPreview() {
    if (!cardPreview) return null;
    return (
      <div
        className="modalOverlay appConfirmOverlay cardPreviewOverlay"
        role="dialog"
        aria-modal="true"
        aria-labelledby="card-preview-title"
        onClick={closeCardPreview}
      >
        <div className="modalCard appConfirmModal cardPreviewModal" onClick={(event) => event.stopPropagation()} aria-busy={cardPreview.phase === "loading"}>
          <h2 id="card-preview-title">{cardPreview.heading}</h2>
          <p className="helperText">{cardPreview.title}</p>
          {cardPreview.phase === "ready" && cardPreview.url ? (
            <iframe
              ref={cardPreviewFrameRef}
              className="cardPreviewFrame"
              title={cardPreview.heading === printCenterHeading ? `${cardPreview.heading}, ${cardPreview.title}` : `Honoree card for ${cardPreview.title}`}
              src={honoreeCardViewUrl(cardPreview.url)}
            />
          ) : cardPreview.phase === "loading" && cardPreview.progress ? (
            <div className="cardPreviewStatus printBatchStatus" role="status" aria-live="polite">
              <div className="printBatchBody">
                <p className="printBatchCounter">{cardPreview.progress.done}/{cardPreview.progress.total}</p>
                <p className="printBatchPhase">{printBatchProgressText(cardPreview.progress)}</p>
                <progress
                  className="printBatchBar"
                  max={Math.max(1, cardPreview.progress.total)}
                  value={cardPreview.progress.done}
                  aria-label={printBatchProgressText(cardPreview.progress)}
                />
                <p className="printBatchCurrent">
                  {cardPreview.progress.current
                    ? `${cardPreview.progress.phase === "merging" ? "Added" : "Finished"} ${printCardLabel(cardPreview.progress.current)}`
                    : "Regenerating each card with its full artwork…"}
                </p>
                <p className="printBatchCounts">
                  <span>{cardPreview.progress.regenerated} regenerated</span>
                  <span className={cardPreview.progress.failed > 0 ? "printBatchFailedCount" : undefined}>
                    {cardPreview.progress.failed} failed
                  </span>
                </p>
                <p className="printBatchHint">Each card is regenerated on its own request, so large batches don't time out. Keep this window open. Close cancels.</p>
              </div>
            </div>
          ) : cardPreview.phase === "error" && cardPreview.failures?.length ? (
            <div className="cardPreviewStatus error printBatchError" role="alert">
              <div className="printBatchBody">
                <p>{cardPreview.message}</p>
                <ul className="printBatchFailures">
                  {cardPreview.failures.map((failure) => (
                    <li key={`${failure.card.changeRequestId}-${failure.step}`}>
                      <strong>{printCardLabel(failure.card)}</strong>
                      <span>{printFailureStepLabel(failure)}: {failure.message}</span>
                    </li>
                  ))}
                </ul>
              </div>
            </div>
          ) : (
            <p className={`cardPreviewStatus${cardPreview.phase === "error" ? " error" : ""}`} role={cardPreview.phase === "error" ? "alert" : "status"}>
              {cardPreview.phase === "error" ? cardPreview.message : cardPreviewLoadingMessage(cardPreview.heading)}
            </p>
          )}
          <div className="modalActions">
            <button type="button" onClick={downloadCardPreview} disabled={cardPreview.phase !== "ready"}>
              Download
            </button>
            <button type="button" className="secondary" onClick={printCardPreview} disabled={cardPreview.phase !== "ready"}>
              Print
            </button>
            <button type="button" className="secondary" onClick={closeCardPreview}>
              Close
            </button>
          </div>
        </div>
      </div>
    );
  }

  function versionReloadNotice() {
    if (kioskLocked || !versionReloadReady) return null;
    return (
      <div className="versionReloadPrompt" role="status">
        <span>A new version is available.</span>
        <button type="button" onClick={() => window.location.reload()}>Reload</button>
      </div>
    );
  }

  if (chrome.shell === "map") {
    return (
      <main className="kioskLock" data-kiosk-lock="1">
        <PublicFlagMap
          kiosk
          onPreviewCard={(honoreeId, title) => { void openPublicHonoreeCard(honoreeId, title); }}
          onExitKiosk={leaveKiosk}
        />
        {renderCardPreview()}
      </main>
    );
  }

  return (
    <main>
      {versionReloadNotice()}
      <header className={mobileNavOpen ? "hero mobileMenuOpen" : isPublicFlagMapRoute ? "hero publicMapHero" : "hero"}>
        <div className="heroContent">

          <p className="eyebrow">Plano Flags of Honor</p>
          <h1>{isPublicFlagMapRoute ? "Public Flag Map" : publicCopy.title}</h1>
          {isPublicFlagMapRoute ? null : <p className="heroLead">{publicCopy.subtitle}</p>}
        </div>

        <div className="authBox">
          {isAuthenticated ? (
            <>
              <strong>{displayName}</strong>
              <div className="authActions">
                <button type="button" onClick={() => void openProfile()}>
                  Profile
                </button>
                <button type="button" onClick={signOut}>
                  Sign out
                </button>
              </div>
            </>
          ) : (
            <div className="authActions">
              <button type="button" onClick={() => void signIn("login")}>
                Log in
              </button>
              <button type="button" className="secondary" onClick={() => void signIn("create")}>
                Create an account
              </button>
            </div>
          )}
        </div>

          <nav id="hero-navigation" className="heroNav desktopHeroNav" aria-label="Main navigation">
            {siteNav.filter((item) => item.show).map((item) => (
              <a key={item.route} className={activeRoute === item.route ? "isActive" : ""} href={item.route} onClick={(event) => handleRouteLink(event, item.route)}>{item.label}</a>
            ))}
            <a
              className={activeRoute === "/honor-a-hero" ? "isActive" : ""}
              href="/honor-a-hero"
              onClick={(event) => handleRouteLink(event, "/honor-a-hero")}
            >
              {publicCopy.submitHonoree}
            </a>
            <a href="https://planoflagsofhonor.com" target="_blank" rel="noreferrer">
              PlanoFlagsOfHonor.com
            </a>
            {chrome.adminLinks ? (
              <AdminNavMenu items={adminNav} activeRoute={activeRoute} menuId="admin-nav-desktop" layout="bar" onNavigate={handleRouteLink} />
            ) : null}
          </nav>

          <Dialog.Root open={mobileNavOpen} onOpenChange={setMobileNavOpen}>
            <Dialog.Trigger asChild>
              <button type="button" className="mobileNavToggle" aria-label="Open menu">
                <span aria-hidden="true" className="mobileNavIcon">☰</span>
                <span>Menu</span>
              </button>
            </Dialog.Trigger>

            <Dialog.Portal>
              <Dialog.Overlay className="mobileDrawerOverlay" />
              <Dialog.Content className="mobileDrawerContent" aria-describedby="mobile-menu-description">
                <div className="mobileDrawerHeader">
                  <div>
                    <p className="eyebrow">Plano Flags of Honor</p>
                    <Dialog.Title className="mobileDrawerTitle">Menu</Dialog.Title>
                  </div>
                  <Dialog.Close asChild>
                    <button type="button" className="mobileDrawerClose" aria-label="Close menu">
                      ×
                    </button>
                  </Dialog.Close>
                </div>

                <p id="mobile-menu-description" className="srOnly">
                  Mobile navigation menu for the Plano Flags of Honor public search.
                </p>

                <nav className="mobileDrawerNav" aria-label="Mobile navigation">
                  {siteNav.filter((item) => item.show).map((item) => (
                    <a key={item.route} className={activeRoute === item.route ? "isActive" : ""} href={item.route} onClick={(event) => handleRouteLink(event, item.route)}>{item.label}</a>
                  ))}
                  <a
                    className={activeRoute === "/honor-a-hero" ? "isActive" : ""}
                    href="/honor-a-hero"
                    onClick={(event) => handleRouteLink(event, "/honor-a-hero")}
                  >
                    {publicCopy.submitHonoree}
                  </a>
                  <a className="drawerExternalLink" href="https://planoflagsofhonor.com" target="_blank" rel="noreferrer" onClick={() => setMobileNavOpen(false)}>
                    PlanoFlagsOfHonor.com
                  </a>
                  {chrome.adminLinks ? (
                    <AdminNavMenu items={adminNav} activeRoute={activeRoute} menuId="admin-nav-drawer" layout="drawer" onNavigate={handleRouteLink} />
                  ) : null}
                </nav>

                <div className="mobileDrawerAccount">
                  {isAuthenticated ? (
                    <>
                      <span>Signed in as</span>
                      <strong>{displayName}</strong>
                      <button
                        type="button"
                        onClick={() => {
                          setMobileNavOpen(false);
                          void openProfile();
                        }}
                      >
                        Profile
                      </button>
                      <button
                        type="button"
                        onClick={() => {
                          setMobileNavOpen(false);
                          void signOut();
                        }}
                      >
                        Sign out
                      </button>
                    </>
                  ) : (
                    <>
                      <button
                        type="button"
                        onClick={() => {
                          void signIn("login");
                        }}
                      >
                        Log in
                      </button>
                      <button
                        type="button"
                        className="secondary"
                        onClick={() => {
                          void signIn("create");
                        }}
                      >
                        Create an account
                      </button>
                    </>
                  )}
                </div>
              </Dialog.Content>
            </Dialog.Portal>
          </Dialog.Root>
      </header>

          {!isPublicFlagMapRoute ? (
          <>

          {chrome.adminBanner && adminAlertItems.length > 0 ? (
            <section className="adminAlertBanner" aria-label="Administrator notifications">
              <div className="adminAlertBannerText">
                <strong>Admin attention needed</strong>
                <span>
                  {adminAlertTotalCount} item{adminAlertTotalCount === 1 ? "" : "s"} need attention.
                </span>
              </div>

              <div className="adminAlertBannerLinks">
                {adminAlertItems.map((item) => (
                  <a
                    key={item.key}
                    href={item.route}
                    className={activeRoute === item.route ? "isActive" : ""}
                    onClick={(event) => handleRouteLink(event, item.route)}
                  >
                    <strong>{item.count}</strong>
                    <span>{item.label}</span>
                  </a>
                ))}
              </div>
            </section>
          ) : null}

          {flagSpotNotice ? (
            <div
              className="modalOverlay appConfirmOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="flag-spot-notice-title"
            >
              <div className="modalCard appConfirmModal" onClick={(event) => event.stopPropagation()}>
                <p className="eyebrow">Flag Map</p>
                <h2 id="flag-spot-notice-title">No flag spot is available</h2>
                <p className="helperText">{flagSpotNotice}</p>
                <div className="modalActions">
                  <button type="button" onClick={() => { setFlagSpotNotice(""); navigateToRoute("/admin/flag-map"); }}>
                    Open Flag Map
                  </button>
                  <button type="button" className="secondary" onClick={() => setFlagSpotNotice("")}>
                    Close
                  </button>
                </div>
              </div>
            </div>
          ) : null}

          </>
          ) : null}

          {renderCardPreview()}

          {!isPublicFlagMapRoute ? (
          <>

          {(error || notice) && (
            <section className="messageStack" aria-live="polite" aria-atomic="true">
              {error ? (
                <div className="message error" role="alert">
                  <span>
                    {error}
                    {error === PLEASE_SIGN_IN_AGAIN ? (
                      <>
                        {" "}
                        <button type="button" onClick={() => void signIn("login")}>
                          Sign in
                        </button>
                      </>
                    ) : null}
                  </span>
                  <button
                    type="button"
                    className="messageClose"
                    aria-label="Dismiss error message"
                    onClick={() => setError("")}
                  >
                    ×
                  </button>
                </div>
              ) : null}

              {notice ? (
                <div className="message success" role="status">
                  <span>{notice}</span>
                  <button
                    type="button"
                    className="messageClose"
                    aria-label="Dismiss success message"
                    onClick={() => setNotice("")}
                  >
                    ×
                  </button>
                </div>
              ) : null}
            </section>
          )}

          {profileOpen ? (
            <div
              className="modalOverlay appConfirmOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="profile-dialog-title"
              onClick={() => {
                if (!profileSaving) setProfileOpen(false);
              }}
            >
              <div className="modalCard appConfirmModal profileModal" onClick={(event) => event.stopPropagation()}>
                <div>
                  <p className="eyebrow">Your account</p>
                  <h2 id="profile-dialog-title">Profile</h2>
                  <p className="helperText">
                    Name, email, and phone are required. Organization is optional. Your name is shown in the header. Email comes from your sign-in account and stays read-only.
                  </p>
                </div>

                <form className="profileForm" onSubmit={(event) => void saveProfile(event)}>
                  <label>
                    Name
                    <input
                      type="text"
                      name="name"
                      autoComplete="name"
                      maxLength={displayNameMaxLength}
                      value={profileDisplayName}
                      disabled={profileLoading || profileSaving}
                      autoFocus
                      onChange={(event) => {
                        setProfileDisplayName(event.target.value);
                        setProfileNotice("");
                      }}
                      required
                    />
                  </label>
                  <label>
                    Email
                    <input
                      type="email"
                      name="email"
                      value={profileEmail}
                      readOnly
                      aria-readonly="true"
                      required
                    />
                  </label>
                  <label>
                    Phone
                    <input
                      type="tel"
                      name="phone"
                      autoComplete="tel"
                      inputMode="tel"
                      placeholder="214-555-1234"
                      value={profilePhone}
                      disabled={profileLoading || profileSaving}
                      onChange={(event) => {
                        setProfilePhone(event.target.value);
                        setProfileNotice("");
                      }}
                      required
                    />
                  </label>
                  <p className="helperText">Use a 10-digit US number, like 214-555-1234.</p>
                  <label>
                    Organization <span className="helperText">(optional)</span>
                    <input
                      type="text"
                      name="organization"
                      autoComplete="organization"
                      maxLength={organizationMaxLength}
                      value={profileOrganization}
                      disabled={profileLoading || profileSaving}
                      onChange={(event) => {
                        setProfileOrganization(event.target.value);
                        setProfileNotice("");
                      }}
                    />
                  </label>

                  {profileError ? <p className="profileFieldError" role="alert">{profileError}</p> : null}
                  {profileNotice ? <p className="profileSaved" role="status">{profileNotice}</p> : null}

                  <div className="modalActions">
                    <button
                      type="button"
                      className="secondary"
                      disabled={profileSaving}
                      onClick={() => setProfileOpen(false)}
                    >
                      Close
                    </button>
                    <button type="submit" disabled={profileLoading || profileSaving}>
                      {profileSaving ? "Saving..." : "Save profile"}
                    </button>
                  </div>
                </form>
              </div>
            </div>
          ) : null}

          {authPrompt ? (
            <div
              className="modalOverlay appConfirmOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="auth-prompt-title"
              onClick={() => setAuthPrompt(null)}
            >
              <div className="modalCard appConfirmModal" onClick={(event) => event.stopPropagation()}>
                <div>
                  <p className="eyebrow">Account</p>
                  <h2 id="auth-prompt-title">Log in or create an account</h2>
                  <p className="helperText">{publicCopy.loginPrompt}</p>
                </div>
                <div className="modalActions authChoiceButtons">
                  <button type="button" className="secondary" onClick={() => setAuthPrompt(null)}>
                    Cancel
                  </button>
                  <button type="button" className="secondary" onClick={() => void signIn("create", authPrompt)}>
                    Create an account
                  </button>
                  <button type="button" onClick={() => void signIn("login", authPrompt)}>
                    Log in
                  </button>
                </div>
              </div>
            </div>
          ) : null}

          {markPrintedOpen ? (
            <div
              className="modalOverlay appConfirmOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="mark-printed-title"
              onClick={cancelMarkPrinted}
            >
              <div className="modalCard appConfirmModal markPrintedModal" onClick={(event) => event.stopPropagation()}>
                <div>
                  <p className="eyebrow">Print Center</p>
                  <h2 id="mark-printed-title">Mark printed</h2>
                  <p className="helperText">
                    {selectedPrintIds.length} card{selectedPrintIds.length === 1 ? "" : "s"} in this batch.
                  </p>
                  <label className="markPrintedComment" htmlFor="mark-printed-comment">
                    Comments
                    <textarea
                      id="mark-printed-comment"
                      rows={4}
                      maxLength={PRINT_EVENT_COMMENT_MAX}
                      value={markPrintedComment}
                      onChange={(event) => setMarkPrintedComment(event.target.value.slice(0, PRINT_EVENT_COMMENT_MAX))}
                      placeholder="Optional"
                    />
                  </label>
                  <p className="markPrintedCount">{markPrintedComment.length}/{PRINT_EVENT_COMMENT_MAX}</p>
                </div>
                <div className="modalActions">
                  <button type="button" className="secondary" onClick={cancelMarkPrinted}>
                    Cancel
                  </button>
                  <button type="button" onClick={() => void confirmMarkPrinted()} disabled={saving}>
                    Confirm
                  </button>
                </div>
              </div>
            </div>
          ) : null}

          {confirmDialog ? (
            <div
              className="modalOverlay appConfirmOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="app-confirm-title"
              onClick={() => resolveConfirmation(false)}
            >
              <div className="modalCard appConfirmModal" onClick={(event) => event.stopPropagation()}>
                <div>
                  <p className="eyebrow">Confirm action</p>
                  <h2 id="app-confirm-title">{confirmDialog.title}</h2>
                  <p className="helperText">{confirmDialog.message}</p>
                </div>
                <div className="modalActions">
                  <button
                    type="button"
                    className="secondary"
                    onClick={() => resolveConfirmation(false)}
                  >
                    {confirmDialog.cancelText}
                  </button>
                  <button
                    type="button"
                    className={confirmDialog.tone === "danger" ? "dangerButton" : ""}
                    onClick={() => resolveConfirmation(true)}
                  >
                    {confirmDialog.confirmText}
                  </button>
                </div>
              </div>
            </div>
          ) : null}

          {flagPositionDetailPosition ? (
            <div
              className="modalOverlay flagPositionDetailOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="flag-position-detail-title"
              onClick={closeFlagPositionDetail}
            >
              <div className="modalCard flagPositionDetailModal" onClick={(event) => event.stopPropagation()}>
                <div className="sectionHeader">
                  <div>
                    <p className="eyebrow">Flag grid details</p>
                    <h2 id="flag-position-detail-title">{flagPositionDetailPosition.flagGridName}</h2>
                    <p className="helperText">
                      {flagPositionDetailPosition.isReserved
                        ? "This flag grid is reserved and cannot be assigned."
                        : flagPositionDetailPosition.isOpen
                          ? "This flag grid is open and available for assignment."
                          : "This flag grid is currently occupied."}
                    </p>
                  </div>
                  <button type="button" className="secondary subtleRefreshButton" onClick={closeFlagPositionDetail}>
                    Close
                  </button>
                </div>

                <dl className="flagPositionDetailList">
                  <div>
                    <dt>Status</dt>
                    <dd>
                      {flagPositionDetailPosition.isReserved
                        ? "Reserved"
                        : flagPositionDetailPosition.isOpen
                          ? "Open"
                          : "Occupied"}
                    </dd>
                  </div>
                  {flagPositionDetailPosition.honoreeName ? (
                    <div>
                      <dt>Honoree</dt>
                      <dd>{flagPositionDetailPosition.honoreeName}</dd>
                    </div>
                  ) : null}
                  {flagPositionDetailPosition.rank || flagPositionDetailPosition.serviceBranchName ? (
                    <div>
                      <dt>Service</dt>
                      <dd>{[flagPositionDetailPosition.rank, flagPositionDetailPosition.serviceBranchName].filter(Boolean).join(" • ")}</dd>
                    </div>
                  ) : null}
                </dl>

                <div className="modalActions">
                  <button
                    type="button"
                    className="secondary"
                    onClick={closeFlagPositionDetail}
                  >
                    Cancel
                  </button>
                  {flagPositionDetailPosition.isOpen && !flagPositionDetailPosition.isReserved ? (
                    <button
                      type="button"
                      onClick={() => assignFromFlagPositionDetail(flagPositionDetailPosition)}
                    >
                      Assign honoree
                    </button>
                  ) : null}
                  {!flagPositionDetailPosition.isOpen && !flagPositionDetailPosition.isReserved ? (
                    <button
                      type="button"
                      className="dangerButton"
                      disabled={flagPositionBusyId === flagPositionDetailPosition.flagGridId}
                      onClick={() => void clearFlagPosition(flagPositionDetailPosition)}
                    >
                      {flagPositionBusyId === flagPositionDetailPosition.flagGridId ? "Removing..." : "Remove from flag grid"}
                    </button>
                  ) : null}
                </div>
              </div>
            </div>
          ) : null}

          {assignGridHonoree ? (
            <div
              className="modalOverlay assignGridModalOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="assign-grid-title"
              onClick={closeAssignGridHonoreeModal}
            >
              <div className="modalCard assignGridModal" onClick={(event) => event.stopPropagation()}>
                <div className="sectionHeader">
                  <div>
                    <p className="eyebrow">Assign flag grid</p>
                    <h2 id="assign-grid-title">{displayNameWithNickname(assignGridHonoree.fullName, assignGridHonoree.nickname)}</h2>
                    <p className="helperText">
                      Choose an open, non-reserved flag grid. The PDF will regenerate and the card will be added to the reprint queue.
                    </p>
                  </div>
                  <button type="button" className="secondary subtleRefreshButton" onClick={closeAssignGridHonoreeModal}>
                    Close
                  </button>
                </div>

                <div className="assignGridFilters" aria-label="Open flag grid filters">
                  <label>
                    <span className="fieldLabelText">Search open flag grids</span>
                    <input
                      type="search"
                      placeholder="Search grid or section"
                      value={assignGridSearchText}
                      onChange={(event) => setAssignGridSearchText(event.target.value)}
                    />
                  </label>

                  <label>
                    <span className="fieldLabelText">Section</span>
                    <select
                      value={assignGridSectionFilter}
                      onChange={(event) => setAssignGridSectionFilter(event.target.value)}
                    >
                      <option value="">All sections</option>
                      {assignGridSections.map((section) => (
                        <option key={section} value={section}>
                          {section}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>

                <div className="assignGridListHeader">
                  <strong>Available open grids</strong>
                  <span>{filteredAssignableFlagPositions.length} shown</span>
                </div>

                {flagPositionsLoading ? (
                  <p className="emptyState">Loading open flag grids...</p>
                ) : filteredAssignableFlagPositions.length === 0 ? (
                  <p className="emptyState">No open flag grids match the current filters.</p>
                ) : (
                  <div className="assignGridList" role="listbox" aria-label="Available open flag grids">
                    {filteredAssignableFlagPositions.map((position) => (
                      <button
                        key={position.flagGridId}
                        type="button"
                        className={selectedAssignFlagGridId === String(position.flagGridId) ? "assignGridOption isSelected" : "assignGridOption"}
                        onClick={() => setSelectedAssignFlagGridId(String(position.flagGridId))}
                        role="option"
                        aria-selected={selectedAssignFlagGridId === String(position.flagGridId)}
                      >
                        <strong>{position.flagGridName}</strong>
                        <span>Section {position.rowLabel || "Other"}</span>
                      </button>
                    ))}
                  </div>
                )}

                <div className="modalActions">
                  <button
                    type="button"
                    className="secondary"
                    onClick={closeAssignGridHonoreeModal}
                  >
                    Cancel
                  </button>
                  <button
                    type="button"
                    disabled={
                      !selectedAssignFlagGrid ||
                      flagPositionsLoading ||
                      (selectedAssignFlagGrid ? flagPositionBusyId === selectedAssignFlagGrid.flagGridId : false)
                    }
                    onClick={() => void assignSelectedGridToHonoree()}
                  >
                    {selectedAssignFlagGrid && flagPositionBusyId === selectedAssignFlagGrid.flagGridId
                      ? "Assigning..."
                      : "Assign selected grid"}
                  </button>
                </div>
              </div>
            </div>
          ) : null}

          {flagPositionModalPosition ? (
            <div
              className="modalOverlay assignFlagModalOverlay"
              role="dialog"
              aria-modal="true"
              aria-labelledby="assign-flag-position-title"
              onClick={closeAssignFlagPositionModal}
            >
              <div className="modalCard assignFlagModal" onClick={(event) => event.stopPropagation()}>
                <div className="sectionHeader">
                  <div>
                    <p className="eyebrow">Assign flag grid</p>
                    <h2 id="assign-flag-position-title">{flagPositionModalPosition.flagGridName}</h2>
                    <p className="helperText">
                      Select an unassigned honoree. Only honorees without a flag grid are listed.
                    </p>
                  </div>
                  <button type="button" className="secondary subtleRefreshButton" onClick={closeAssignFlagPositionModal}>
                    Close
                  </button>
                </div>

                <label className="assignFlagModalPicker">
                  <span className="fieldLabelText">Unassigned honoree</span>
                  <select
                    value={selectedUnassignedHonoreeId}
                    onChange={(event) => setSelectedUnassignedHonoreeId(event.target.value)}
                    disabled={unassignedHonoreesLoading}
                  >
                    <option value="">
                      {unassignedHonoreesLoading
                        ? "Loading unassigned honorees..."
                        : unassignedHonorees.length === 0
                          ? "No unassigned honorees found"
                          : "Choose an unassigned honoree"}
                    </option>
                    {unassignedHonorees.map((honoree) => (
                      <option key={honoree.id} value={honoree.id}>
                        {displayUnassignedHonoreeName(honoree)}
                        {[honoree.rank, honoree.serviceBranchName].filter(Boolean).length
                          ? ` — ${[honoree.rank, honoree.serviceBranchName].filter(Boolean).join(" • ")}`
                          : ""}
                      </option>
                    ))}
                  </select>
                </label>

                <div className="modalActions">
                  <button
                    type="button"
                    className="secondary"
                    onClick={closeAssignFlagPositionModal}
                  >
                    Cancel
                  </button>
                  <button
                    type="button"
                    disabled={!selectedUnassignedHonoreeId || unassignedHonoreesLoading || flagPositionBusyId === flagPositionModalPosition.flagGridId}
                    onClick={() => void assignSelectedHonoreeToPosition(flagPositionModalPosition)}
                  >
                    {flagPositionBusyId === flagPositionModalPosition.flagGridId ? "Assigning..." : "Assign to flag grid"}
                  </button>
                </div>
              </div>
            </div>
          ) : null}

          {showHowItWorks ? (
            <div className="modalBackdrop" role="presentation" onMouseDown={() => setShowHowItWorks(false)}>
              <section
                className="infoModal"
                role="dialog"
                aria-modal="true"
                aria-labelledby="how-it-works-title"
                onMouseDown={(event) => event.stopPropagation()}
              >
                <div className="modalHeader">
                  <h2 id="how-it-works-title">How this works</h2>
                  <button type="button" className="modalClose" onClick={() => setShowHowItWorks(false)} aria-label="Close how this works">
                    ×
                  </button>
                </div>
                <ol className="modalSteps">
                  <li><strong>{publicCopy.stepSearchTitle}</strong><span>{publicCopy.stepSearchBody}</span></li>
                  <li><strong>{publicCopy.stepSubmitTitle}</strong><span>{publicCopy.stepSubmitBody}</span></li>
                  <li><strong>{publicCopy.stepReviewTitle}</strong><span>{publicCopy.stepReviewBody}</span></li>
                </ol>
              </section>
            </div>
          ) : null}

          {isFindRoute ? (
          <FindSearchFrame
            heading={publicCopy.searchHeading}
            help={publicCopy.searchHelp}
            infoButton={
              <button
                type="button"
                className="infoIconButton"
                onClick={() => setShowHowItWorks(true)}
                aria-label="How this works"
              >
                i
              </button>
            }
          >

            <form className="searchBar nativeSearchBar" onSubmit={searchHonorees}>
              <label className="visuallyHidden" htmlFor="honoree-search">
                Search honorees
              </label>
              <input
                id="honoree-search"
                type="search"
                placeholder={publicCopy.searchPlaceholder}
                value={honoreeSearchText}
                onChange={(e) => {
                  const value = e.target.value;
                  setHonoreeSearchText(value);

                  if (value.trim() === "") {
                    setHonoreeResults([]);
                    setHonoreeSearchPerformed(false);
                  }
                }}
                aria-label="Search honorees"
              />
              <div className="searchActionButtons">
                <button
                  type="submit"
                  className="searchSubmitButton"
                  disabled={searchLoading || honoreeSearchText.trim() === ""}
                >
                  {searchLoading ? "Searching..." : "Search"}
                </button>
                <button
                  type="button"
                  className="secondary searchClearButton"
                  onClick={clearHonoreeSearch}
                  disabled={searchLoading || (honoreeSearchText.trim() === "" && !honoreeSearchPerformed)}
                >
                  Clear
                </button>
              </div>
            </form>

            {showInlineHowItWorks ? (
              <section className="inlineHowItWorks" aria-label="How to submit or suggest a change">
                <div>
                  <span>1</span>
                  <strong>{publicCopy.stepSearchTitle}</strong>
                  <p>{publicCopy.stepSearchBody}</p>
                </div>
                <div>
                  <span>2</span>
                  <strong>{publicCopy.stepSubmitTitle}</strong>
                  <p>{publicCopy.stepSubmitBody}</p>
                </div>
                <div>
                  <span>3</span>
                  <strong>{publicCopy.stepReviewTitle}</strong>
                  <p>{publicCopy.stepReviewBody}</p>
                </div>
              </section>
            ) : null}

            {honoreeSearchPerformed ? (
              honoreeResults.length === 0 ? (
                <p className="emptyState">
                  No matching honoree records found. Try a different spelling or fewer search details. If the honoree isn’t listed, <a className="emptyStateSubmitLink" href="/honor-a-hero" onClick={(event) => { event.preventDefault(); if (!saving) beginNomination(); }}>submit a new record</a>.
                </p>
              ) : (
                <div className="honoreeResults">
                  {honoreeResults.map((honoree) => (
                    <article key={honoree.id} className="honoreeCard">
                      {honoree.imageUrl ? (
                        <img src={honoree.imageUrl} alt={displayNameWithNickname(honoree.fullName, honoree.nickname)} />
                      ) : (
                        <div className="honoreePlaceholder">No photo</div>
                      )}

                      <div>
                        <div className="honoreeTitleRow">
                          <h3>{displayNameWithNickname(honoree.fullName, honoree.nickname)}</h3>
                          {honoree.kia ? <span className="status status-submitted">KIA</span> : null}
                          {chrome.adminLinks && claimantsByHonoreeId[honoree.id]?.length ? (
                            <span className="status claimedStatus">Claimed</span>
                          ) : null}
                        </div>

                        <p>
                          {[honoree.rank, honoree.serviceBranchName].filter(Boolean).join(" • ") || "Service details unavailable"}
                        </p>

                        <dl className="detailGrid">
                          <div>
                            <dt>Flag grid</dt>
                            <dd>{honoree.flagGrid || "—"}</dd>
                          </div>
                          <div>
                            <dt>Submitter</dt>
                            <dd>{honoree.sponsorName || "—"}</dd>
                          </div>
                        </dl>

                        <div className="cardActions">
                          <a
                            className="textLink"
                            href={honoreePdfUrl(honoree.id)}
                            onClick={(event) => {
                              event.preventDefault();
                              void openPublicHonoreeCard(
                                honoree.id,
                                displayNameWithNickname(honoree.fullName, honoree.nickname)
                              );
                            }}
                          >
                            Download / Print Honoree PDF
                          </a>

                          <div className="honoreeActionArea">
                            <button
                              type="button"
                              className="primaryAction"
                              onClick={() => void suggestChange(honoree.id)}
                              disabled={saving}
                            >
                              {publicCopy.suggestChange}
                            </button>

                            {chrome.adminLinks ? (
                              <details
                                className="adminActionsMenu"
                                onBlur={(event) => {
                                  const nextFocus = event.relatedTarget as Node | null;
                                  if (!nextFocus || !event.currentTarget.contains(nextFocus)) {
                                    event.currentTarget.removeAttribute("open");
                                  }
                                }}
                              >
                                <summary>Admin actions</summary>
                                <div className="adminActionsPanel">
                                  <button
                                    type="button"
                                    className="secondary compactButton"
                                    onClick={(event) => {
                                      (event.currentTarget.closest("details") as HTMLDetailsElement | null)?.removeAttribute("open");
                                      beginAdminDirectEdit(honoree);
                                    }}
                                    disabled={saving || regeneratingPdfHonoreeId === honoree.id}
                                  >
                                    Edit + reprint
                                  </button>

                                  <button
                                    type="button"
                                    className="secondary compactButton"
                                    onClick={(event) => {
                                      (event.currentTarget.closest("details") as HTMLDetailsElement | null)?.removeAttribute("open");
                                      queueHonoreeReprint(honoree);
                                    }}
                                    disabled={
                                      saving ||
                                      queueingReprintHonoreeId === honoree.id ||
                                      regeneratingPdfHonoreeId === honoree.id
                                    }
                                  >
                                    {queueingReprintHonoreeId === honoree.id ? "Adding..." : "Add to reprint queue"}
                                  </button>

                                  <button
                                    type="button"
                                    className="secondary compactButton"
                                    onClick={(event) => {
                                      (event.currentTarget.closest("details") as HTMLDetailsElement | null)?.removeAttribute("open");
                                      viewClaimants(honoree);
                                    }}
                                    disabled={claimantBusyHonoreeId === honoree.id}
                                  >
                                    {claimantBusyHonoreeId === honoree.id
                                      ? "Loading claimants..."
                                      : claimantsByHonoreeId[honoree.id]
                                        ? "Hide claimants"
                                        : "View claimants"}
                                  </button>

                                  <button
                                    type="button"
                                    className="secondary compactButton"
                                    onClick={(event) => {
                                      (event.currentTarget.closest("details") as HTMLDetailsElement | null)?.removeAttribute("open");
                                      void beginFlagPositionAssignment(honoree);
                                    }}
                                    disabled={saving}
                                  >
                                    Assign flag grid
                                  </button>

                                  <button
                                    type="button"
                                    className="secondary compactButton"
                                    onClick={(event) => {
                                      (event.currentTarget.closest("details") as HTMLDetailsElement | null)?.removeAttribute("open");
                                      regenerateHonoreePdf(honoree);
                                    }}
                                    disabled={
                                      saving ||
                                      regeneratingPdfHonoreeId === honoree.id ||
                                      queueingReprintHonoreeId === honoree.id
                                    }
                                  >
                                    {regeneratingPdfHonoreeId === honoree.id ? "Generating..." : "Regenerate PDF"}
                                  </button>
                                </div>
                              </details>
                            ) : null}
                          </div>
                        </div>

                        {claimantsByHonoreeId[honoree.id] ? (
                          <div className="claimantPanel">
                            <strong>Claimant visibility</strong>
                            {claimantsByHonoreeId[honoree.id].length === 0 ? (
                              <p>No active claimants were found for this honoree.</p>
                            ) : (
                              <ul>
                                {claimantsByHonoreeId[honoree.id].map((claimant) => (
                                  <li key={claimant.claimId}>
                                    <span>{claimant.claimantName || claimant.claimantEmail}</span>
                                    <small>
                                      {claimant.claimantEmail}
                                      {claimant.claimantPhone ? ` • ${displayUsPhone(claimant.claimantPhone)}` : ""}
                                      {claimant.claimantOrganization ? ` • ${claimant.claimantOrganization}` : ""}
                                      {" • "}
                                      {claimant.claimStatus}
                                      {claimant.latestRequestStatus ? ` • ${claimant.latestRequestStatus}` : ""}
                                      {" • "}
                                      Claimed {formatDate(claimant.createdUtc)}
                                    </small>
                                  </li>
                                ))}
                              </ul>
                            )}
                          </div>
                        ) : null}
                      </div>
                    </article>
                  ))}
                </div>
              )
            ) : null}

            {honoreeSearchPerformed && !searchLoading ? (
              <div className="missingHonoreePrompt">
                <p>
                  {publicCopy.missingLead}{" "}
                  <button type="button" onClick={beginNomination} disabled={saving}>
                    {publicCopy.submitHonoree}
                  </button>
                </p>
                <p className="helperText">{publicCopy.loginRequired}</p>
              </div>
            ) : null}
          </FindSearchFrame>
          ) : null}

          {isMyFlagsRoute && isAuthenticated ? (
            <>
          <section id="my-flags" className="card ownershipCard">
            <div className="sectionHeader">
              <div>
                <p className="eyebrow">Your account</p>
                <h2>{publicCopy.mySubmissions}</h2>
                <p className="helperText">
                  Check honoree records you submitted and changes you suggested. A suggested change does not replace the public record until an administrator approves it.
                </p>
              </div>
              <button type="button" className="secondary subtleRefreshButton" onClick={loadData} disabled={loading}>
                {loading ? "Loading..." : "Refresh"}
              </button>
            </div>

            {myClaims.length === 0 ? (
              <p className="emptyState">You have no submissions yet.</p>
            ) : (
              <div className="ownedFlagGrid">
                {myClaims.map((claim) => {
                  const status = latestRequestStatus(claim);

                  return (
                    <article key={claim.id} className="ownedFlagCard">
                      {submissionPhotoKind(claim) === "pending" && account && claim.latestChangeRequest ? (
                        <AuthorizedThumbnail
                          instance={instance}
                          account={account}
                          url={changeRequestPhotoUrl(claim.latestChangeRequest.id)}
                          photoKey={`${claim.latestChangeRequest.id}:${claim.latestChangeRequest.photoFileName}`}
                          alt={`${claim.honoreeName || "Honoree"} photo`}
                          className="ownedFlagImage"
                          emptyLabel="No photo"
                        />
                      ) : claim.honoreeImageUrl ? (
                        <img
                          className="ownedFlagImage"
                          src={claim.honoreeImageUrl}
                          alt={`${claim.honoreeName || "Honoree"} photo`}
                        />
                      ) : (
                        <div className="ownedFlagImage ownedFlagImagePlaceholder">No photo</div>
                      )}

                      <div className="ownedFlagMain">
                        <p className="eyebrow">Honoree</p>
                        <h3>{claim.honoreeName || "Honoree details pending"}</h3>
                        <p>
                          Flag grid {flagSpotLabel(claim.flagGridName, claim.flagGridId)} • Submission #{claim.id}
                        </p>
                      </div>

                      <div className="ownedFlagMeta">
                        <span className={statusClass(status)}>{ownershipStatusLabel(claim)}</span>
                        <span>Started {formatDate(claim.createdUtc)}</span>
                        {claim.submittedUtc ? <span>Last submitted {formatDate(claim.submittedUtc)}</span> : null}
                      </div>

                      <div className="ownedFlagActions">
                        <button type="button" onClick={() => beginEdit(claim)}>
                          Open
                        </button>
                        <button
                          type="button"
                          className="secondary"
                          onClick={() => void openSubmissionCard(claim)}
                        >
                          Download / Print Honoree PDF
                        </button>
                        <button
                          type="button"
                          className="secondary"
                          onClick={() => unclaimFlag(claim)}
                          disabled={saving}
                        >
                          Withdraw
                        </button>
                      </div>



                      {claim.claimNotice ? (
                        <p className="claimNotice">{claim.claimNotice}</p>
                      ) : null}

                      <div className="miniTimeline" aria-label="Submission timeline">
                        <span>Started {formatDate(claim.createdUtc)}</span>
                        {claim.latestChangeRequest?.submittedUtc ? (
                          <span>Submitted {formatDate(claim.latestChangeRequest.submittedUtc)}</span>
                        ) : null}
                        {claim.latestChangeRequest?.requestStatus ? (
                          <span>Status: {ownershipStatusLabel(claim)}</span>
                        ) : null}
                      </div>
                    </article>
                  );
                })}
              </div>
            )}
          </section>
            </>
          ) : null}

          {isMyFlagsRoute && !isAuthenticated ? (
            <section className="card guestNotice">
              <h2>{publicCopy.mySubmissions}</h2>
              <p>{publicCopy.loginPrompt}</p>
              <div className="authChoiceButtons">
                <button type="button" onClick={() => void signIn("login")}>
                  Log in
                </button>
                <button type="button" className="secondary" onClick={() => void signIn("create")}>
                  Create an account
                </button>
              </div>
            </section>
          ) : null}

          {isHonorHeroRoute && !isAuthenticated ? (
            <section className="card guestNotice">
              <h2>{publicCopy.submitHonoree}</h2>
              <p>{publicCopy.loginPrompt}</p>
              <div className="authChoiceButtons">
                <button type="button" onClick={() => void signIn("login", { action: "submit-honoree" })}>
                  Log in
                </button>
                <button type="button" className="secondary" onClick={() => void signIn("create", { action: "submit-honoree" })}>
                  Create an account
                </button>
              </div>
            </section>
          ) : null}

          {isAdminRoute && !kioskLocked && !isAdmin ? (
            <section className="card guestNotice">
              <h2>Administrator access required</h2>
              <p>Sign in with an account assigned the PFOH.Admin role to use review, duplicates, flag map, and printing tools.</p>
              {!isAuthenticated ? (
                <div className="authChoiceButtons">
                  <button type="button" onClick={() => void signIn("login")}>
                    Log in
                  </button>
                  <button type="button" className="secondary" onClick={() => void signIn("create")}>
                    Create an account
                  </button>
                </div>
              ) : null}
            </section>
          ) : null}

          {isAdminRoute && chrome.adminLinks ? (
            <div id="admin" className="adminSectionStack" aria-label="Administrator tools">
              {isDuplicatesRoute ? (
                <DuplicateMerge
                  instance={instance}
                  account={account ?? null}
                  onSignIn={() => void signIn("login")}
                />
              ) : null}

              {isAdminReviewRoute ? (
              <section className="card adminCard adminReviewCard">
                <div className="sectionHeader">
                  <div>
                    <p className="eyebrow">Administrator</p>
                    <h2>
                      Review submitted changes
                      <span className="countBadge">{pendingReviews.length}</span>
                    </h2>
                  </div>
                  <div className="adminHeaderActions">
                    <button type="button" className="secondary subtleRefreshButton" onClick={loadData} disabled={loading}>
                      {loading ? "Loading..." : "Refresh"}
                    </button>
                    <button type="button" className="secondary exportExcelButton" onClick={exportHonoreesExcel}>
                      Export Excel
                    </button>
                  </div>
                </div>

                {pendingReviews.length > 0 || claimedByMultipleCount > 0 ? (
                  <div className="adminCardSummary">
                    {pendingReviews.length > 0 ? (
                      <span><strong>{pendingReviews.length}</strong> pending review</span>
                    ) : null}
                    {claimedByMultipleCount > 0 ? (
                      <span><strong>{claimedByMultipleCount}</strong> multiple-claim alerts</span>
                    ) : null}
                  </div>
                ) : null}

              {reviewLoadError ? (
                <p className="message error" role="alert">{reviewLoadError}</p>
              ) : pendingReviews.length === 0 ? (
                <p>No submitted changes are waiting for review.</p>
              ) : (
                <div className="tableWrap">
                  <table className="responsiveTable reviewTable">
                    <thead>
                      <tr>
                        <th>Honoree</th>
                        <th>Flag grid</th>
                        <th>Submitted by</th>
                        <th>Submitted</th>
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {pendingReviews.map((item) => (
                        <tr key={item.changeRequestId}>
                          <td data-label="Honoree">
                            <div className="reviewHonoreeCell">
                              {isPendingHonoreePhoto(item.photoFileName) && account ? (
                                <AuthorizedThumbnail
                                  instance={instance}
                                  account={account}
                                  url={changeRequestPhotoUrl(item.changeRequestId)}
                                  photoKey={`${item.changeRequestId}:${item.photoFileName}`}
                                  alt={`${item.honoreeName} pending honoree photo`}
                                  className="reviewPendingPhoto"
                                  emptyLabel={null}
                                />
                              ) : null}
                              <div>
                                <strong>{item.honoreeName}</strong>{" "}
                                <span className={`reviewKindBadge ${item.submissionKind === "Change" ? "isChange" : item.submissionKind === "Withdrawal" ? "isWithdrawal" : "isNew"}`}>
                                  {submissionKindLabel(item.submissionKind)}
                                </span>
                                <br />
                                <span>{[item.rank, item.serviceBranchName].filter(Boolean).join(" • ")}</span>
                                <details className="reviewDetails">
                                  <summary>View details</summary>
                                  <dl>
                                    <div>
                                      <dt>Description</dt>
                                      <dd>{item.description?.trim() || "Not provided"}</dd>
                                    </div>
                                    <div>
                                      <dt>Awards</dt>
                                      <dd>{item.awards?.trim() || "Not provided"}</dd>
                                    </div>
                                    <div>
                                      <dt>Conflicts served</dt>
                                      <dd>{item.conflictsServed?.trim() || "Not provided"}</dd>
                                    </div>
                                    {item.submissionKind === "Change" && item.fieldChanges && item.fieldChanges.length > 0 ? (
                                      <div>
                                        <dt>Changes</dt>
                                        <dd>
                                          <ul className="reviewFieldChanges">
                                            {item.fieldChanges.map((change) => (
                                              <li key={change.label}>
                                                <strong>{change.label}</strong> {fieldChangeText(change)}
                                              </li>
                                            ))}
                                          </ul>
                                        </dd>
                                      </div>
                                    ) : null}
                                  </dl>
                                </details>
                              </div>
                            </div>
                          </td>
                          <td data-label="Flag grid">{flagSpotLabel(item.flagGridName, item.flagGridId)}</td>
                          <td data-label="Submitted by">
                            {item.claimantName || item.claimantEmail}
                            <br />
                            <span>{item.claimantEmail}</span>
                            {item.claimantPhone ? (
                              <>
                                <br />
                                <span>{displayUsPhone(item.claimantPhone)}</span>
                              </>
                            ) : null}
                            {item.claimantOrganization ? (
                              <>
                                <br />
                                <span>{item.claimantOrganization}</span>
                              </>
                            ) : null}
                          </td>
                          <td data-label="Submitted">
                            {item.submissionKind === "Withdrawal"
                              ? `Withdrawn ${formatDate(item.submittedUtc)}`
                              : formatDate(item.submittedUtc)}
                          </td>
                          <td data-label="Actions" className="rowActions">
                            {item.submissionKind === "Withdrawal" ? (
                              <button
                                type="button"
                                className="secondary compactButton"
                                disabled={adminBusyId === item.changeRequestId}
                                onClick={() => void confirmRemovedFromField(item)}
                              >
                                {adminBusyId === item.changeRequestId ? "Saving..." : "Removed from field"}
                              </button>
                            ) : (
                            <>
                            <select
                              className="reviewActionSelect"
                              aria-label={`Review actions for ${item.honoreeName}`}
                              value=""
                              disabled={adminBusyId === item.changeRequestId}
                              onChange={(event) => {
                                const action = event.currentTarget.value;
                                event.currentTarget.value = "";

                                if (action === "approve-reprint") {
                                  void approveReview(item, true);
                                }

                                if (action === "approve-only") {
                                  void approveReview(item, false);
                                }

                                if (action === "reject") {
                                  void rejectReview(item);
                                }
                              }}
                            >
                              <option value="">
                                {adminBusyId === item.changeRequestId
                                  ? adminBusyAction === "reject"
                                    ? "Rejecting..."
                                    : "Approving..."
                                  : "Review actions"}
                              </option>
                              <option value="approve-reprint">Approve + reprint</option>
                              <option value="approve-only">Approve only</option>
                              <option value="reject">Reject</option>
                            </select>
                            <button
                              type="button"
                              className="secondary compactButton reviewPreviewButton"
                              onClick={() => void previewHonoreeCard(item)}
                            >
                              Honoree Card
                            </button>
                            </>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}



              </section>
              ) : null}

              {isFlagMapRoute ? (
                <section id="flag-position-manager" className="flagPositionManager adminStandalonePanel" aria-label="Flag Manager">
                  <div className="sectionHeader">
                    <div>
                      <p className="eyebrow">Flag grids</p>
                      <h3>Flag Manager</h3>
                      <p className="helperText">
                        
                      </p>
                    </div>
                    <div className="flagPositionActions">
                      <button
                        type="button"
                        className="secondary exportExcelButton"
                        onClick={openFlagMapExport}
                        disabled={flagMapExporting}
                      >
                        {flagMapExporting ? "Exporting..." : "Export Excel"}
                      </button>
                      <button
                        type="button"
                        className="secondary subtleRefreshButton"
                        onClick={() => void refreshFlagPositions()}
                        disabled={flagPositionsLoading}
                      >
                        {flagPositionsLoading ? "Loading..." : "Refresh Flag Map"}
                      </button>
                    </div>
                  </div>

                  {flagMapExportOpen ? (
                    <div
                      className="modalOverlay flagMapExportOverlay"
                      role="dialog"
                      aria-modal="true"
                      aria-labelledby="flag-map-export-title"
                      onClick={() => {
                        if (!flagMapExporting) {
                          setFlagMapExportOpen(false);
                        }
                      }}
                    >
                      <div className="modalCard flagMapExportModal" onClick={(event) => event.stopPropagation()}>
                        <div className="sectionHeader">
                          <div>
                            <p className="eyebrow">Flag map</p>
                            <h2 id="flag-map-export-title">Export Excel</h2>
                            <p className="helperText">
                              Choose the fields to include. The sheet lists seats that have an honoree, sorted by section then grid. Open grids and reserved grids with no honoree are left out.
                            </p>
                          </div>
                          <button
                            type="button"
                            className="secondary subtleRefreshButton"
                            onClick={() => setFlagMapExportOpen(false)}
                            disabled={flagMapExporting}
                          >
                            Close
                          </button>
                        </div>

                        <fieldset className="flagMapExportFields">
                          <legend className="srOnly">Export fields</legend>
                          {flagMapExportFields.map((field) => (
                            <label key={field.id} className="checkRow" htmlFor={`flag-map-export-${field.id}`}>
                              <input
                                id={`flag-map-export-${field.id}`}
                                type="checkbox"
                                checked={flagMapExportSelection.includes(field.id)}
                                onChange={() => toggleFlagMapExportField(field.id)}
                                disabled={flagMapExporting}
                              />
                              <span>
                                {field.label}
                                {"hint" in field && field.hint ? (
                                  <span className="flagMapExportHint"> {field.hint}</span>
                                ) : null}
                              </span>
                            </label>
                          ))}
                        </fieldset>

                        {flagMapExportError ? (
                          <p className="message error flagMapExportError" role="alert">{flagMapExportError}</p>
                        ) : null}

                        <div className="modalActions">
                          <button
                            type="button"
                            className="secondary"
                            onClick={() =>
                              setFlagMapExportSelection(flagMapExportFields.map((field) => field.id))
                            }
                            disabled={flagMapExporting}
                          >
                            Select all
                          </button>
                          <button
                            type="button"
                            onClick={() => void confirmFlagMapExport()}
                            disabled={flagMapExporting || flagMapExportSelection.length === 0}
                          >
                            {flagMapExporting ? "Exporting..." : "Export Excel"}
                          </button>
                        </div>
                      </div>
                    </div>
                  ) : null}

                  {flagMapLoadError ? (
                    <p className="message error" role="alert">{flagMapLoadError}</p>
                  ) : null}

                  <p className="helperText flagAssignmentHelp">
                    Click a flag grid to view details, assign open grids, or clear occupied grids.
                  </p>

                  <div className="flagMapToolbar">
                  <div className="flagSeatLegend" aria-label="Flag grid legend and quick filters">
                    <button
                      type="button"
                      className={`legendButton ${flagPositionOccupancyFilter === "all" ? "isActive" : ""}`}
                      onClick={() => setFlagPositionOccupancyFilter("all")}
                    >
                      All ({flagPositions.length})
                    </button>
                    <button
                      type="button"
                      className={`legendButton ${flagPositionOccupancyFilter === "open" ? "isActive" : ""}`}
                      onClick={() => setFlagPositionOccupancyFilter("open")}
                    >
                      <i className="legendOpen" /> Open ({openFlagPositionCount})
                    </button>
                    <button
                      type="button"
                      className={`legendButton ${flagPositionOccupancyFilter === "occupied" ? "isActive" : ""}`}
                      onClick={() => setFlagPositionOccupancyFilter("occupied")}
                    >
                      <i className="legendOccupied" /> Occupied ({occupiedFlagPositionCount})
                    </button>
                    <button
                      type="button"
                      className={`legendButton ${flagPositionOccupancyFilter === "reserved" ? "isActive" : ""}`}
                      onClick={() => setFlagPositionOccupancyFilter("reserved")}
                    >
                      <i className="legendReserved" /> Reserved ({reservedFlagPositionCount})
                    </button>
                  </div>

                  <div className="flagPositionFilters" aria-label="Flag grid filters">
                    <label>
                      <span className="fieldLabelText">Search flag grids</span>
                      <input
                        type="search"
                        placeholder="Search grid, honoree, rank, or branch"
                        value={flagPositionSearchText}
                        onChange={(event) => setFlagPositionSearchText(event.target.value)}
                      />
                    </label>
                    <label>
                      <span className="fieldLabelText">Section</span>
                      <select
                        value={flagPositionSectionFilter}
                        onChange={(event) => setFlagPositionSectionFilter(event.target.value)}
                      >
                        <option value="">All sections</option>
                        {flagPositionSections.map((section) => (
                          <option key={section} value={section}>
                            {section}
                          </option>
                        ))}
                      </select>
                    </label>
                    <button
                      type="button"
                      className="secondary subtleRefreshButton"
                      onClick={() => {
                        setFlagPositionSearchText("");
                        setFlagPositionSectionFilter("");
                        setFlagPositionOccupancyFilter("all");
                      }}
                    >
                      Clear filters
                    </button>
                  </div>
                  </div>

                  <div className="flagSeatMap" role="grid" aria-label="Flag grid seat map">
                    {flagMapLoadError ? null : flagPositionRows.length === 0 ? (
                      <p className="emptyState">No flag grids match the current filters.</p>
                    ) : (
                      flagPositionRows.map((row) => (
                        <div className="flagSeatRow" role="row" key={row.rowLabel}>
                          <div className="flagSeatRowLabel">{row.rowLabel}</div>
                          <div className="flagSeatCells">
                            {row.positions.map((position) => {
                              const isBusy = flagPositionBusyId === position.flagGridId;

                              return (
                                <button
                                  type="button"
                                  key={position.flagGridId}
                                  className={[
                                    "flagSeat",
                                    position.isReserved ? "isReserved" : position.isOpen ? "isOpen" : "isOccupied",
                                    isBusy ? "isBusy" : "",
                                    flagPositionDetailPosition?.flagGridId === position.flagGridId ? "isSelected" : ""
                                  ].filter(Boolean).join(" ")}
                                  role="gridcell"
                                  aria-label={`${position.flagGridName} ${position.isReserved ? "reserved" : position.isOpen ? "open" : `occupied by ${position.honoreeName || "honoree"}`}`}
                                  onClick={() => openFlagPositionDetail(position)}
                                >
                                  <strong>{position.flagGridName}</strong>
                                  {position.isReserved ? (
                                    <span>Reserved</span>
                                  ) : position.isOpen ? (
                                    <span>Open</span>
                                  ) : (
                                    <span>{position.honoreeName || "Assigned"}</span>
                                  )}
                                </button>
                              );
                            })}
                          </div>
                        </div>
                      ))
                    )}
                  </div>
                </section>
              ) : null}


              {isPrintingRoute ? (
              <section className="card adminCard adminPrintingCard" aria-label="Printing">
                <div className="sectionHeader printCenterHeader">
                  <div>
                    <p className="eyebrow">Printing</p>
                    <h2>
                      Print Center
                      <span className="countBadge">{printQueue.length}</span>
                    </h2>
                    <p className="helperText">
                      {printLoadError
                        ? "The honoree print queue could not be loaded."
                        : printQueue.length === 0
                        ? "No approved cards are waiting for reprint."
                        : `${printQueue.length} card${printQueue.length === 1 ? "" : "s"} ready to print.`}
                    </p>
                  </div>

                  <div className="actions printActions">
                    <span className="selectedCount">{selectedPrintIds.length} selected</span>

                    <button
                      type="button"
                      className="secondary"
                      onClick={openSelectedPrintPdf}
                      disabled={saving || selectedPrintIds.length === 0}
                    >
                      Download merged PDF
                    </button>

                    <button
                      type="button"
                      onClick={markSelectedPrinted}
                      disabled={saving || selectedPrintIds.length === 0}
                    >
                      Mark printed
                    </button>
                  </div>
                </div>

              {printLoadError ? (
                <p id="reprint-queue" className="message error" role="alert">{printLoadError}</p>
              ) : printQueue.length === 0 ? (
                <p id="reprint-queue" className="compactEmptyState">No approved cards are waiting for reprint.</p>
              ) : (
                <section id="reprint-queue" className="reprintQueuePanel compactReprintQueuePanel">
                  <div className="tableWrap">
                    <table className="responsiveTable printQueueTable">
                      <thead>
                        <tr>
                          <th>
                            <label className="tableSelectAll">
                              <input
                                type="checkbox"
                                checked={allPrintItemsSelected}
                                onChange={toggleSelectAllPrintItems}
                                disabled={printQueue.length === 0}
                                aria-label="Select all cards for printing"
                              />
                              <span>Select all</span>
                            </label>
                          </th>
                          <th>Honoree</th>
                          <th>Flag grid</th>
                          <th>Approved</th>
                          <th>PDF</th>
                          <th>Actions</th>
                        </tr>
                      </thead>
                      <tbody>
                        {printQueue.map((item) => (
                          <tr key={item.changeRequestId}>
                            <td data-label="Select">
                              <input
                                type="checkbox"
                                checked={selectedPrintIds.includes(item.changeRequestId)}
                                onChange={() => togglePrintSelection(item.changeRequestId)}
                              />
                            </td>
                            <td data-label="Honoree">
                              <strong>{item.honoreeName}</strong>
                              <br />
                              <span>{item.serviceBranchName}</span>
                            </td>
                            <td data-label="Flag grid">{item.flagGridName}</td>
                            <td data-label="Approved">{formatDate(item.approvedUtc)}</td>
                            <td data-label="PDF">
                              {item.honoreeId ? (
                                <a
                                  className="textLink"
                                  href={honoreePdfUrl(item.honoreeId)}
                                  onClick={(event) => {
                                    event.preventDefault();
                                    if (!item.honoreeId) return;
                                    void openPublicHonoreeCard(item.honoreeId, item.honoreeName);
                                  }}
                                >
                                  Open PDF
                                </a>
                              ) : (
                                <span className="pdfWarning">Missing PDF</span>
                              )}
                            </td>
                            <td data-label="Actions">
                              <button
                                type="button"
                                className="secondary compactButton dangerSoftButton"
                                onClick={() => removeOneFromReprintQueue(item.changeRequestId, item.honoreeName)}
                                disabled={saving}
                              >
                                Remove
                              </button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </section>
              )}

              <PrintDuplicateReviews
                instance={instance}
                account={account ?? null}
                onSignIn={() => void signIn("login")}
              />

              <section className="printHistory" aria-label="Print history">
                <h3>Print history</h3>
                <p className="helperText">Times are Colombia Time (COT).</p>
                {printHistoryError ? (
                  <p className="message error" role="alert">{printHistoryError}</p>
                ) : visiblePrintHistory.length === 0 ? (
                  <p className="compactEmptyState">No cards have been marked printed yet.</p>
                ) : (
                  <ul className="printHistoryList">
                    {visiblePrintHistory.map((event) => (
                      <li key={event.batchId}>
                        <span className="printHistoryWhen">{formatPrintEventTime(event.printedUtc)}</span>
                        <span className="printHistoryWho">{event.printedBy}</span>
                        <span className="printHistoryCount">{event.cardCount} card{event.cardCount === 1 ? "" : "s"}</span>
                        {event.comment ? <span className="printHistoryComment">{event.comment}</span> : null}
                      </li>
                    ))}
                  </ul>
                )}
              </section>

              </section>
              ) : null}
            </div>
          ) : null}

          {((selectedClaim && isMyFlagsRoute) || (isNominating && isHonorHeroRoute)) ? (
            <section id={isNominating ? "nominate" : undefined} ref={formCardRef} className="card formCard">
              <div className="sectionHeader">
                <div>
                  <p className="eyebrow">
                    {isNominating
                      ? publicCopy.submitHonoree
                      : selectedClaim?.claimStatus === "AdminDirectEdit"
                        ? "Administrator edit"
                        : publicCopy.suggestChange}
                  </p>
                  <h2>
                    {isNominating
                      ? publicCopy.submitHonoree
                      : selectedClaim?.claimStatus === "AdminDirectEdit"
                        ? `Directly edit ${selectedClaim?.honoreeName || "honoree record"}`
                        : `${publicCopy.suggestChange}${selectedClaim?.honoreeName ? ` for ${selectedClaim.honoreeName}` : ""}`}
                  </h2>
                </div>
                <button
                  type="button"
                  className="secondary"
                  onClick={() => {
                    setSelectedClaim(null);
                    setIsNominating(false);
                    setSelectedPhoto(null);
                    setSelectedPhotoRotation(0);
                    setForm(blankForm);
                  }}
                >
                  Close
                </button>
              </div>

              {selectedClaim?.claimStatus === "AdminDirectEdit" ? (
                <p className="helperText">
                  As an administrator, you can save these changes directly and add the card to the reprint queue.
                </p>
              ) : null}

              <p className="requiredFieldsNote">
                Fields marked <span className="requiredMark" aria-hidden="true">*</span> are required.
              </p>

              <form className="gridForm" onSubmit={isNominating ? submitNomination : submitClaim}>
                <label>
                  <span className="fieldLabelText">
                    First name <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <input
                    ref={firstFormInputRef}
                    required
                    value={form.firstName}
                    onChange={(e) => update("firstName", e.target.value)}
                  />
                </label>

                <label>
                  Middle name
                  <input
                    value={form.middleName ?? ""}
                    onChange={(e) => update("middleName", e.target.value)}
                  />
                </label>

                <label>
                  <span className="fieldLabelText">
                    Last name <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <input
                    required
                    value={form.lastName}
                    onChange={(e) => update("lastName", e.target.value)}
                  />
                </label>

                <label>
                  Suffix
                  <input
                    placeholder="Jr., Sr., III"
                    value={form.suffix ?? ""}
                    onChange={(e) => update("suffix", e.target.value)}
                  />
                </label>

                <label>
                  Nickname
                  <input
                    value={form.nickname ?? ""}
                    onChange={(e) => update("nickname", e.target.value)}
                  />
                </label>

                <label>
                  Rank / title
                  <input
                    value={form.rank ?? ""}
                    onChange={(e) => update("rank", e.target.value)}
                  />
                </label>

                <label>
                  <span className="fieldLabelText">
                    Service branch category <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <select
                    required
                    value={form.serviceBranchCategoryId ?? ""}
                    onChange={(e) => {
                      const categoryId = nullableNumber(e.target.value);
                      setForm((current) => ({
                        ...current,
                        serviceBranchCategoryId: categoryId,
                        serviceBranchId: null
                      }));
                    }}
                  >
                    <option value="">Select category</option>
                    {serviceBranchCategories.map((category) => (
                      <option key={category.id} value={category.id}>
                        {category.serviceBranchCategoryName}
                      </option>
                    ))}
                  </select>
                </label>

                <label>
                  <span className="fieldLabelText">
                    Service branch <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <select
                    required
                    disabled={!form.serviceBranchCategoryId}
                    value={form.serviceBranchId ?? ""}
                    onChange={(e) => update("serviceBranchId", nullableNumber(e.target.value))}
                  >
                    <option value="">
                      {form.serviceBranchCategoryId ? "Select branch" : "Select category first"}
                    </option>
                    {filteredServiceBranches.map((branch) => (
                      <option key={branch.id} value={branch.id}>
                        {branch.serviceBranchName}
                      </option>
                    ))}
                  </select>
                </label>

                <label>
                  Start year
                  <input
                    type="number"
                    inputMode="numeric"
                    value={form.startYear ?? ""}
                    onChange={(e) => update("startYear", nullableNumber(e.target.value))}
                  />
                </label>

                <label>
                  End year
                  <input
                    type="number"
                    inputMode="numeric"
                    value={form.endYear ?? ""}
                    onChange={(e) => update("endYear", nullableNumber(e.target.value))}
                  />
                </label>

                <label className="wide">
                  Conflicts served
                  <textarea
                    rows={3}
                    value={form.conflictsServed ?? ""}
                    onChange={(e) => update("conflictsServed", e.target.value)}
                  />
                </label>

                <label className="wide">
                  Awards
                  <textarea
                    rows={3}
                    value={form.awards ?? ""}
                    onChange={(e) => update("awards", e.target.value)}
                  />
                </label>

                <label className="wide">
                  <span className="fieldLabelText">
                    Honoree description / tribute <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <textarea
                    required
                    rows={6}
                    value={form.description ?? ""}
                    onChange={(e) => update("description", e.target.value)}
                  />
                </label>
                <label className="wide">
                  Honoree photo
                  {(selectedPhotoPreviewUrl ||
                    pendingPhotoObjectUrl ||
                    (!isPendingHonoreePhoto(selectedClaim?.latestChangeRequest?.photoFileName) && currentPhotoSourceUrl())) ? (
                    <img
                      className="honoreePhotoPreview"
                      src={selectedPhotoPreviewUrl || pendingPhotoObjectUrl || currentPhotoSourceUrl()}
                      alt={`${selectedClaim?.honoreeName || "Honoree"} photo`}
                      style={
                        selectedPhotoPreviewUrl
                          ? { transform: `rotate(${selectedPhotoRotation}deg)` }
                          : undefined
                      }
                      onError={(event) => {
                        event.currentTarget.style.display = "none";
                      }}
                    />
                  ) : null}
                  {!selectedPhoto && isPendingHonoreePhoto(selectedClaim?.latestChangeRequest?.photoFileName) ? (
                    <span className="pendingPhotoLabel">Photo received, pending review</span>
                  ) : null}
                  <input
                    type="file"
                    accept="image/*"
                    onChange={(e) => handlePhotoSelected(e.target.files?.[0] ?? null)}
                  />
                  {(selectedPhoto || currentPhotoSourceUrl()) ? (
                    <div className="photoRotationControls" aria-label="Photo rotation controls">
                      <button
                        type="button"
                        className="secondary compactButton"
                        onClick={() => void rotateDisplayedPhoto(-90)}
                      >
                        ↺ Rotate left
                      </button>
                      <button
                        type="button"
                        className="secondary compactButton"
                        onClick={() => void rotateDisplayedPhoto(90)}
                      >
                        Rotate right ↻
                      </button>
                    </div>
                  ) : null}
                  <span className="helperText">
                    {selectedPhoto
                      ? `Selected: ${selectedPhoto.name}${selectedPhotoRotation ? ` • Rotated ${selectedPhotoRotation}°` : ""}`
                      : !selectedPhoto && isPendingHonoreePhoto(selectedClaim?.latestChangeRequest?.photoFileName)
                        ? "Use Rotate left/right to correct it, then save or submit to apply the rotation."
                        : !isNominating && (selectedClaim?.honoreeId || selectedClaim?.honoreeImageUrl)
                          ? "Current photo is shown above. Use Rotate left/right to correct it, then save or submit to apply the rotation."
                          : "Optional. JPG or PNG is best for the printed honoree card."}
                  </span>
                </label>


                <label>
                  <span className="fieldLabelText">
                    Submitter phone <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <input
                    required
                    value={form.submitterPhoneNumber ?? ""}
                    onChange={(e) => update("submitterPhoneNumber", e.target.value)}
                  />
                </label>

                <label>
                  <span className="fieldLabelText">
                    Submitter email <span className="requiredMark" aria-hidden="true">*</span>
                  </span>
                  <input
                    required
                    type="email"
                    value={form.submitterEmailAddress ?? ""}
                    onChange={(e) => update("submitterEmailAddress", e.target.value)}
                  />
                </label>

                <label className="checkRow wide">
                  <input
                    type="checkbox"
                    checked={form.kia}
                    onChange={(e) => update("kia", e.target.checked)}
                  />
                  Killed in action / Gold Star recognition
                </label>

                {selectedClaim?.claimStatus === "AdminDirectEdit" ? null : (
                  <p className="helperText wide">{publicCopy.aboveSubmit}</p>
                )}

                <div className="actions wide">
                  {!isNominating ? (
                    <button type="button" className="secondary" disabled={saving} onClick={saveDraft}>
                      {saving ? "Saving..." : "Save draft"}
                    </button>
                  ) : null}
                  <button type="submit" disabled={saving}>
                    {saving
                      ? submitPhase || "Submitting..."
                      : selectedClaim?.claimStatus === "AdminDirectEdit"
                        ? "Save and queue reprint"
                        : "Submit for review"}
                  </button>
                </div>
              </form>
            </section>
          ) : null}

          </>
          ) : (
            <PublicFlagMap
              onPreviewCard={(honoreeId, title) => { void openPublicHonoreeCard(honoreeId, title); }}
            />
          )}

      <footer className="appFooter" aria-label="Plano Flags of Honor footer">
        <div className="appFooterInner">
          <div className="footerColumn footerExplore">
            <h2>Explore</h2>
            <a href="https://planoflagsofhonor.com/about-pfoh/" target="_blank" rel="noreferrer">
              About Plano Flags of Honor
            </a>
            <a
              href="/honor-a-hero"
              onClick={(event) => {
                event.preventDefault();
                beginNomination();
              }}
            >
              {publicCopy.submitHonoree}
            </a>
            <a href="https://planoflagsofhonor.com/become-a-sponsor/" target="_blank" rel="noreferrer">
              Become a Sponsor
            </a>
          </div>

          <div className="footerColumn footerLoop">
            <h2>Stay in the loop</h2>
            <div className="footerSocials" aria-label="Social links">
              <a href="https://www.facebook.com/planoflagsofhonor" target="_blank" rel="noreferrer" aria-label="Plano Flags of Honor on Facebook">
                f
              </a>
              <a href="https://www.instagram.com/planoflagsofhonor/" target="_blank" rel="noreferrer" aria-label="Plano Flags of Honor on Instagram">
                ◎
              </a>
            </div>
          </div>

          <div className="footerBrand">
            <img src={pfohFooterLogo} alt="Rotary Clubs of Plano and Plano Flags of Honor Rotary Community Corps" />
          </div>

          <div className="footerContact">
            <a href="https://planoflagsofhonor.com/contact-us/" target="_blank" rel="noreferrer">
              Contact Us
            </a>
          </div>

          <div className="footerCredit">
            <span>Built by</span>
            <a href="https://www.kneco.com" target="_blank" rel="noreferrer" aria-label="KNECO, Inc. website">
              <img src={knecoLogoBlue} alt="KNECO, Inc." />
            </a>
          </div>
        </div>
        <p className="appVersionStamp">Version {packageInfo.version}</p>
      </footer>
    </main>
  );
}