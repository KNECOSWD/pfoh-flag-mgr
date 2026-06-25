import { useEffect, useMemo, useRef, useState } from "react";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import {
  AdminClaimantSummary,
  AdminFlagPosition,
  AdminUnassignedHonoree,
  AdminPrintQueueItem,
  AdminReviewItem,
  ApiError,
  FlagClaim,
  HonoreeSearchResult,
  SaveHonoreeChangeRequest,
  ServiceBranch,
  ServiceBranchCategory,
  adminApi,
  flagClaimApi,
  honoreeApi,
  honoreePdfUrl,
  honoreePhotoUrl,
  lookupApi
} from "./api";
import { loginRequest } from "./authConfig";
import knecoLogoBlue from "./assets/kneco-logo-blue.png";

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
  const latest = claim.latestChangeRequest?.requestStatus;

  if (latest === "Submitted") return "Awaiting admin review";
  if (latest === "Approved") return "Approved";
  if (latest === "Rejected") return "Needs revision";
  if (latest === "Draft") return "Draft saved";

  return "Managed by you";
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
    phone: profileClaimString(claims, [
      "phone_number",
      "phone",
      "phoneNumber",
      "mobile_phone",
      "mobilePhone",
      "telephoneNumber",
      "signInNames.phoneNumber",
      "extension_phoneNumber",
      "extension_PhoneNumber",
      "extension_mobilePhone",
      "extension_MobilePhone"
    ])
  };
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

  const [isAdmin, setIsAdmin] = useState(false);
  const [pendingReviews, setPendingReviews] = useState<AdminReviewItem[]>([]);
  const [printQueue, setPrintQueue] = useState<AdminPrintQueueItem[]>([]);
  const [selectedPrintIds, setSelectedPrintIds] = useState<number[]>([]);
  const [adminBusyId, setAdminBusyId] = useState<number | null>(null);
  const [regeneratingPdfHonoreeId, setRegeneratingPdfHonoreeId] = useState<number | null>(null);
  const [queueingReprintHonoreeId, setQueueingReprintHonoreeId] = useState<number | null>(null);
  const [claimantsByHonoreeId, setClaimantsByHonoreeId] = useState<Record<number, AdminClaimantSummary[]>>({});
  const [claimantBusyHonoreeId, setClaimantBusyHonoreeId] = useState<number | null>(null);
  const [flagPositions, setFlagPositions] = useState<AdminFlagPosition[]>([]);
  const [flagPositionHonoree, setFlagPositionHonoree] = useState<HonoreeSearchResult | null>(null);
  const [flagPositionBusyId, setFlagPositionBusyId] = useState<number | null>(null);
  const [flagPositionsLoading, setFlagPositionsLoading] = useState(false);
  const [showFlagPositionManager, setShowFlagPositionManager] = useState(false);
  const [flagPositionSearchText, setFlagPositionSearchText] = useState("");
  const [flagPositionSectionFilter, setFlagPositionSectionFilter] = useState("");
  const [flagPositionOccupancyFilter, setFlagPositionOccupancyFilter] = useState<"all" | "open" | "occupied">("all");
  const [flagPositionModalPosition, setFlagPositionModalPosition] = useState<AdminFlagPosition | null>(null);
  const [unassignedHonorees, setUnassignedHonorees] = useState<AdminUnassignedHonoree[]>([]);
  const [selectedUnassignedHonoreeId, setSelectedUnassignedHonoreeId] = useState("");
  const [unassignedHonoreesLoading, setUnassignedHonoreesLoading] = useState(false);

  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [showHowItWorks, setShowHowItWorks] = useState(false);
  const [mobileNavOpen, setMobileNavOpen] = useState(false);

  const displayName = useMemo(
    () => account?.name || account?.username || "Supporter",
    [account]
  );

  const submitterContact = useMemo(
    () => getAccountSubmitterContact(account),
    [account]
  );

  const allPrintQueueIds = useMemo(
    () => printQueue.map((item) => item.changeRequestId),
    [printQueue]
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

  const flagPositionSections = useMemo(
    () =>
      [...new Set(flagPositions.map((position) => position.rowLabel || "Other"))]
        .sort((left, right) => left.localeCompare(right, undefined, { numeric: true })),
    [flagPositions]
  );

  const filteredFlagPositions = useMemo(() => {
    const query = flagPositionSearchText.trim().toLowerCase();

    return flagPositions.filter((position) => {
      if (flagPositionOccupancyFilter === "open" && !position.isOpen) {
        return false;
      }

      if (flagPositionOccupancyFilter === "occupied" && position.isOpen) {
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
      .sort(([left], [right]) => left.localeCompare(right, undefined, { numeric: true }))
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

  const openFlagPositionCount = useMemo(
    () => flagPositions.filter((position) => position.isOpen).length,
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

  async function signIn() {
    await instance.loginRedirect(loginRequest);
  }

  async function signOut() {
    await instance.logoutRedirect();
  }

  async function loadAdminData() {
    if (!account) return;

    try {
      const [pending, queue, positions] = await Promise.all([
        adminApi.pending(instance, account),
        adminApi.printQueue(instance, account),
        adminApi.flagPositions(instance, account)
      ]);

      setIsAdmin(true);
      setPendingReviews(pending);
      setPrintQueue(queue);
      setFlagPositions(positions);
      setSelectedPrintIds((current) =>
        current.filter((id) => queue.some((item) => item.changeRequestId === id))
      );
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        setIsAdmin(false);
        setPendingReviews([]);
        setPrintQueue([]);
        setFlagPositions([]);
        return;
      }

      throw err;
    }
  }

  async function loadData() {
    if (!account) return;

    setLoading(true);
    setError("");

    try {
      const [claims, branches, categories] = await Promise.all([
        flagClaimApi.mine(instance, account),
        lookupApi.serviceBranches(instance, account),
        lookupApi.serviceBranchCategories(instance, account)
      ]);

      setMyClaims(claims);
      setServiceBranches(branches);
      setServiceBranchCategories(categories);

      await loadAdminData();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to load flag data.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    if (isAuthenticated && account) {
      loadData();
    }

    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAuthenticated, account?.homeAccountId]);

  useEffect(() => {
    const timers: number[] = [];

    if (error) {
      timers.push(window.setTimeout(() => setError(""), 8000));
    }

    if (notice) {
      timers.push(window.setTimeout(() => setNotice(""), 5000));
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
      await loadClaimantsForSearchResults(results);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to search honorees.");
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
        currentForm.submitterPhoneNumber?.trim() || submitterContact.phone,
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
      submitterPhoneNumber: draft?.submitterPhoneNumber ?? "",
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
  }

  function beginNomination() {
    if (!account) {
      void signIn();
      return;
    }

    setSelectedPhoto(null);
    setSelectedPhotoRotation(0);
    setSelectedClaim(null);
    setIsNominating(true);
    setForm(applySubmitterProfileDefaults(blankForm));
    setNotice("");
    setError("");
  }

  async function claimSearchResult(honoree: HonoreeSearchResult) {
    if (!account) {
      await signIn();
      return;
    }

    const ok = window.confirm(
      `Claim ${displayNameWithNickname(honoree.fullName, honoree.nickname)}'s flag record? You will be able to submit corrections or updates for review.`
    );

    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const claim = await flagClaimApi.claimHonoree(instance, account, honoree.id);
      await loadData();
      const coClaimNotice = claim.claimNotice
        ? ` ${claim.claimNotice}`
        : "";
      setNotice(`${displayNameWithNickname(honoree.fullName, honoree.nickname)}'s flag record has been claimed. Review the prefilled details below and submit any changes.${coClaimNotice}`);
      beginEdit(claim);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to claim this honoree's flag record.");
    } finally {
      setSaving(false);
    }
  }


  async function unclaimFlag(claim: FlagClaim) {
    if (!account) return;

    const ok = window.confirm(
      `Unclaim ${claim.honoreeName || "this flag"}? It will be removed from your claimed flags.`
    );

    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice(`Unclaiming ${claim.honoreeName || "flag"}...`);

    try {
      await flagClaimApi.unclaim(instance, account, claim.id);

      if (selectedClaim?.id === claim.id) {
        setSelectedClaim(null);
        setForm(blankForm);
        setSelectedPhoto(null);
        setSelectedPhotoRotation(0);
      }

      await loadData();
      setNotice(`${claim.honoreeName || "Flag"} was unclaimed.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to unclaim this flag.");
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

    const ok = window.confirm(
      `Edit ${displayNameWithNickname(honoree.fullName, honoree.nickname)}'s flag record directly and queue a card reprint after saving?`
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
      setError(err instanceof Error ? err.message : "Unable to start admin edit.");
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
      setError(err instanceof Error ? err.message : "Unable to load claimants for this honoree.");
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

    const ok = window.confirm(
      `Add ${displayNameWithNickname(honoree.fullName, honoree.nickname)} to the card reprint queue?`
    );

    if (!ok) return;

    setQueueingReprintHonoreeId(honoree.id);
    setError("");
    setNotice(`Adding ${displayNameWithNickname(honoree.fullName, honoree.nickname)} to the reprint queue...`);

    try {
      await adminApi.queueHonoreeReprint(instance, account, honoree.id);
      await loadData();
      setNotice(`${displayNameWithNickname(honoree.fullName, honoree.nickname)} was added to the reprint queue.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to add honoree to the reprint queue.");
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

    const ok = window.confirm(
      `Generate a new PDF for ${displayNameWithNickname(honoree.fullName, honoree.nickname)} and overwrite the existing stored PDF?`
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
      setError(err instanceof Error ? err.message : "Unable to regenerate PDF.");
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
      setError(err instanceof Error ? err.message : "Unable to load the current photo for rotation.");
    }
  }

  async function fetchCurrentPhotoAsFile() {
    const sourceUrl = currentPhotoSourceUrl();

    if (!sourceUrl) {
      return null;
    }

    const response = await fetch(sourceUrl, { cache: "no-store" });

    if (!response.ok) {
      throw new Error("Unable to load the current photo for rotation.");
    }

    const blob = await response.blob();
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
    setError("");
    setNotice("");

    try {
      const formToSubmit = applySubmitterProfileDefaults(form);
      const photoForUpload = await getSelectedPhotoForUpload();
      setForm(formToSubmit);
      await flagClaimApi.nominate(instance, account, formToSubmit, photoForUpload);
      await loadData();
      setNotice("Nomination submitted for admin review and claimed under your account.");
      setIsNominating(false);
      setSelectedPhoto(null);
      setSelectedPhotoRotation(0);
      setForm(blankForm);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to submit nomination.");
    } finally {
      setSaving(false);
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
      setError(err instanceof Error ? err.message : "Unable to save draft.");
    } finally {
      setSaving(false);
    }
  }

  async function submitClaim(event: React.FormEvent) {
    event.preventDefault();

    if (!account || !selectedClaim) return;

    const isAdminDirectEdit = selectedClaim.claimStatus === "AdminDirectEdit";

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const formToSubmit = applySubmitterProfileDefaults(form);
      const photoForUpload = await getSelectedPhotoForUpload();
      setForm(formToSubmit);
      await flagClaimApi.saveDraft(instance, account, selectedClaim.id, formToSubmit, photoForUpload);

      if (isAdminDirectEdit) {
        await flagClaimApi.applyAdminEditReprint(instance, account, selectedClaim.id);
        await loadData();
        setNotice("Admin edit applied and added to the reprint queue.");
      } else {
        await flagClaimApi.submit(instance, account, selectedClaim.id);
        await loadData();
        setNotice("Honoree information submitted for review.");
      }

      setSelectedClaim(null);
      setSelectedPhoto(null);
      setSelectedPhotoRotation(0);
      setForm(blankForm);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to submit changes.");
    } finally {
      setSaving(false);
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
      setError(err instanceof Error ? err.message : "Unable to load flag positions.");
    } finally {
      setFlagPositionsLoading(false);
    }
  }

  function beginFlagPositionAssignment(honoree: HonoreeSearchResult) {
    setFlagPositionHonoree(honoree);
    setShowFlagPositionManager(true);
    setNotice(`Select an open flag position for ${displayNameWithNickname(honoree.fullName, honoree.nickname)}.`);

    window.setTimeout(() => {
      document.getElementById("flag-position-manager")?.scrollIntoView({
        behavior: "smooth",
        block: "start"
      });
    }, 75);
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
      setNotice(`Select an open flag position for ${displayNameWithNickname(selected.fullName, selected.nickname)}.`);
    }
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
      setError(err instanceof Error ? err.message : "Unable to load unassigned honorees.");
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
      setError("Choose an unassigned honoree before assigning this flag position.");
      return;
    }

    const honoreeName = displayUnassignedHonoreeName(selectedHonoree);
    const ok = window.confirm(`Assign ${honoreeName} to flag position ${position.flagGridName}?`);

    if (!ok) return;

    setFlagPositionBusyId(position.flagGridId);
    setError("");
    setNotice(`Assigning ${honoreeName} to ${position.flagGridName}...`);

    try {
      await adminApi.assignFlagPosition(instance, account, position.flagGridId, honoreeId);
      const [positions, results, unassigned] = await Promise.all([
        adminApi.flagPositions(instance, account),
        honoreeSearchPerformed ? honoreeApi.search(honoreeSearchText, 25) : Promise.resolve(honoreeResults),
        adminApi.unassignedHonorees(instance, account)
      ]);

      setFlagPositions(positions);
      setUnassignedHonorees(unassigned);

      if (honoreeSearchPerformed) {
        setHonoreeResults(results);
        setClaimantsByHonoreeId({});
        await loadClaimantsForSearchResults(results);
      }

      setFlagPositionHonoree(null);
      closeAssignFlagPositionModal();
      setNotice(`${honoreeName} was assigned to ${position.flagGridName}.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to assign flag position.");
      setNotice("");
    } finally {
      setFlagPositionBusyId(null);
    }
  }

  async function clearFlagPosition(position: AdminFlagPosition) {
    if (!account || position.isOpen) return;

    const honoreeName = position.honoreeName || "this honoree";
    const ok = window.confirm(`Remove ${honoreeName} from flag position ${position.flagGridName}?`);

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
      setError(err instanceof Error ? err.message : "Unable to remove the honoree from this flag position.");
      setNotice("");
    } finally {
      setFlagPositionBusyId(null);
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
      setError(err instanceof Error ? err.message : "Unable to export honorees.");
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
    setError("");
    setNotice(`${requiresCardReprint ? "Approving and queuing reprint" : "Approving"} ${item.honoreeName}...`);

    try {
      await adminApi.approve(instance, account, item.changeRequestId, requiresCardReprint);
      await loadData();
      setNotice(`${item.honoreeName} approved${requiresCardReprint ? " and added to the card reprint queue" : ""}.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to approve item.");
      setNotice("");
    } finally {
      setAdminBusyId(null);
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
    setError("");
    setNotice(`Rejecting ${item.honoreeName}...`);

    try {
      await adminApi.reject(instance, account, item.changeRequestId, reviewNotes.trim());
      await loadData();
      setNotice(`${item.honoreeName} rejected.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to reject item.");
      setNotice("");
    } finally {
      setAdminBusyId(null);
    }
  }

  async function downloadSelectedPrintPdf() {
    if (!account) return;

    if (selectedPrintIds.length === 0) {
      setError("Select at least one item to print.");
      return;
    }

    setSaving(true);
    setError("");
    setNotice("");

    try {
      await adminApi.downloadMergedPrintPdf(instance, account, selectedPrintIds);
      setNotice("Merged PDF downloaded. Send the downloaded PDF to the printer.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to download merged PDF.");
    } finally {
      setSaving(false);
    }
  }

  async function markSelectedPrinted() {
    if (!account) return;

    if (selectedPrintIds.length === 0) {
      setError("Select at least one item to mark printed.");
      return;
    }

    const ok = window.confirm("Mark the selected card reprints as printed?");
    if (!ok) return;

    setSaving(true);
    setError("");
    setNotice("");

    try {
      const result = await adminApi.markPrinted(instance, account, selectedPrintIds);
      await loadData();
      setNotice(`${result.count} item(s) marked printed.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to mark printed.");
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

  return (
    <main>
      <header className={mobileNavOpen ? "hero mobileMenuOpen" : "hero"}>
        <div className="heroContent">

          <p className="eyebrow">Plano Flags of Honor</p>
          <h1>Find a Flag</h1>
          <p className="heroSubtitle">
            Search Plano Flags of Honor honoree records and view flag details.
          </p>
        </div>

        <div className="authBox">
          {isAuthenticated ? (
            <>
              <strong>{displayName}</strong>
              <button type="button" onClick={signOut}>
                Sign out
              </button>
            </>
          ) : (
            <button type="button" onClick={signIn}>
              Register / sign in
            </button>
          )}
        </div>

          <button
            type="button"
            className="mobileNavToggle"
            onClick={() => setMobileNavOpen((current) => !current)}
            aria-expanded={mobileNavOpen}
            aria-controls="hero-navigation"
          >
            Menu
            <span aria-hidden="true">{mobileNavOpen ? "▲" : "▼"}</span>
          </button>

          <nav
            id="hero-navigation"
            className={mobileNavOpen ? "heroNav isOpen" : "heroNav"}
            aria-label="Main navigation"
          >
            <a href="#search" onClick={() => setMobileNavOpen(false)}>Find a flag</a>
            {isAuthenticated ? <a href="#my-flags" onClick={() => setMobileNavOpen(false)}>My flags</a> : null}
            {isAdmin ? <a href="#admin" onClick={() => setMobileNavOpen(false)}>Admin</a> : null}
            {isAdmin ? <a href="#reprint-queue" onClick={() => setMobileNavOpen(false)}>Reprint queue</a> : null}
            {isAdmin ? <a href="#flag-position-manager" onClick={() => setMobileNavOpen(false)}>Flag positions</a> : null}
            <a
              href="#nominate"
              onClick={(event) => {
                event.preventDefault();
                setMobileNavOpen(false);
                beginNomination();
              }}
            >
              Nominate a honoree
            </a>
            <a href="https://planoflagsofhonor.com" target="_blank" rel="noreferrer" onClick={() => setMobileNavOpen(false)}>
              PlanoFlagsOfHonor.com
            </a>
          </nav>
      </header>

          {(error || notice) && (
            <section className="messageStack" aria-live="polite" aria-atomic="true">
              {error ? (
                <div className="message error" role="alert">
                  <span>{error}</span>
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
                    <p className="eyebrow">Assign flag position</p>
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
                    {flagPositionBusyId === flagPositionModalPosition.flagGridId ? "Assigning..." : "Assign to position"}
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
                  <h2 id="how-it-works-title">How Find a Flag works</h2>
                  <button type="button" className="modalClose" onClick={() => setShowHowItWorks(false)} aria-label="Close how this works">
                    ×
                  </button>
                </div>
                <ol className="modalSteps">
                  <li><strong>Search</strong><span>Find an existing honoree by name, branch, rank, submitter, or flag grid.</span></li>
                  <li><strong>Review</strong><span>Open the honoree PDF or review the record details.</span></li>
                  <li><strong>Claim or nominate</strong><span>Sign in to claim a record, submit an update, or nominate someone who is missing.</span></li>
                  <li><strong>Admin review</strong><span>Administrators approve changes and add cards to the reprint queue when needed.</span></li>
                </ol>
              </section>
            </div>
          ) : null}

          <section id="search" className="card searchCard">
            <div className="sectionHeader searchHeader">
              <div>
                <p className="eyebrow">Find an existing honoree</p>
                <h2>
                  Honoree search
                  <button
                    type="button"
                    className="infoIconButton"
                    onClick={() => setShowHowItWorks(true)}
                    aria-label="How this works"
                  >
                    i
                  </button>
                </h2>
                <p className="helperText">Search by name, branch, rank, submitter, or flag grid.</p>
              </div>
            </div>

            <form className="searchBar nativeSearchBar" onSubmit={searchHonorees}>
              <label className="visuallyHidden" htmlFor="honoree-search">
                Search honorees
              </label>
              <input
                id="honoree-search"
                type="search"
                placeholder="Search name, branch, rank, or flag grid"
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
              {honoreeSearchText ? (
                <button
                  type="button"
                  className="nativeSearchClear"
                  onClick={clearHonoreeSearch}
                  aria-label="Clear search"
                  title="Clear search"
                >
                  ×
                </button>
              ) : null}
              <button type="submit" className="visuallyHidden">
                Search
              </button>
            </form>

            {honoreeSearchPerformed ? (
              honoreeResults.length === 0 ? (
                <p className="emptyState">
                  No honorees found. A signed-in user can nominate a veteran or first responder for admin review.
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
                          {isAdmin && claimantsByHonoreeId[honoree.id]?.length ? (
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
                          <a className="textLink" href={honoreePdfUrl(honoree.id)} target="_blank" rel="noreferrer">
                            Open honoree PDF
                          </a>

                          <div className="honoreeActionArea">
                            <button
                              type="button"
                              className="primaryAction"
                              onClick={() => claimSearchResult(honoree)}
                              disabled={saving}
                            >
                              {isAuthenticated ? "Claim this flag" : "Sign in to claim"}
                            </button>

                            {isAdmin ? (
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
                                      beginFlagPositionAssignment(honoree);
                                    }}
                                    disabled={saving}
                                  >
                                    Assign flag position
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
          </section>


          {!isAuthenticated ? (
            <section className="card guestNotice">
              <h2>Search is open to everyone</h2>
              <p>
                You can search and view honoree flag records without signing in. Sign in or register when you are ready to claim a flag record, submit updates, or nominate a veteran or first responder.
              </p>
              <button type="button" onClick={signIn}>
                Register / sign in
              </button>
            </section>
          ) : null}

          {isAuthenticated ? (
            <>
          <section id="my-flags" className="card ownershipCard">
            <div className="sectionHeader">
              <div>
                <p className="eyebrow">Your account</p>
                <h2>My claimed flags</h2>
                <p className="helperText">
                  These are the flag records you manage, including nominations you submitted. You can submit updates at any time; admins review and approve changes before they are published or reprinted.
                </p>
              </div>
              <button type="button" className="secondary subtleRefreshButton" onClick={loadData} disabled={loading}>
                {loading ? "Loading..." : "Refresh"}
              </button>
            </div>

            {myClaims.length === 0 ? (
              <p className="emptyState">You have not claimed a flag record yet.</p>
            ) : (
              <div className="ownedFlagGrid">
                {myClaims.map((claim) => {
                  const status = latestRequestStatus(claim);

                  return (
                    <article key={claim.id} className="ownedFlagCard">
                      {claim.honoreeImageUrl ? (
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
                          Flag grid {claim.flagGridName || `Grid ${claim.flagGridId}`} • Claim #{claim.id}
                        </p>
                      </div>

                      <div className="ownedFlagMeta">
                        <span className={statusClass(status)}>{ownershipStatusLabel(claim)}</span>
                        <span>Claimed {formatDate(claim.createdUtc)}</span>
                        {claim.submittedUtc ? <span>Last submitted {formatDate(claim.submittedUtc)}</span> : null}
                      </div>

                      <div className="ownedFlagActions">
                        <button type="button" onClick={() => beginEdit(claim)}>
                          Manage flag
                        </button>
                        <button
                          type="button"
                          className="secondary"
                          onClick={() => unclaimFlag(claim)}
                          disabled={saving}
                        >
                          Unclaim
                        </button>
                      </div>



                      {claim.claimNotice ? (
                        <p className="claimNotice">{claim.claimNotice}</p>
                      ) : null}

                      <div className="miniTimeline" aria-label="Claim timeline">
                        <span>Claimed {formatDate(claim.createdUtc)}</span>
                        {claim.latestChangeRequest?.submittedUtc ? (
                          <span>Submitted {formatDate(claim.latestChangeRequest.submittedUtc)}</span>
                        ) : null}
                        {claim.latestChangeRequest?.requestStatus ? (
                          <span>Status: {claim.latestChangeRequest.requestStatus}</span>
                        ) : null}
                      </div>
                    </article>
                  );
                })}
              </div>
            )}
          </section>

          {isAdmin ? (
            <section id="admin" className="card adminCard">
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

              <div className="adminStats" aria-label="Administrator dashboard summary">
                <div className="statCard">
                  <strong>{pendingReviews.length}</strong>
                  <span>Pending review</span>
                </div>
                <div className="statCard">
                  <strong>{printQueue.length}</strong>
                  <span>Ready to print</span>
                </div>
                <div className="statCard">
                  <strong>{selectedPrintIds.length}</strong>
                  <span>Selected</span>
                </div>
                <div className="statCard">
                  <strong>{claimedByMultipleCount}</strong>
                  <span>Multiple-claim alerts</span>
                </div>
                <div className="statCard">
                  <strong>{openFlagPositionCount}</strong>
                  <span>Open flag positions</span>
                </div>
              </div>

              {showFlagPositionManager ? (
                <section id="flag-position-manager" className="flagPositionManager" aria-label="Flag position manager">
                  <div className="sectionHeader">
                    <div>
                      <p className="eyebrow">Flag positions</p>
                      <h3>Flag position manager</h3>
                      <p className="helperText">
                        Select a honoree from search results, then choose an open flag position. Occupied positions can be cleared by an administrator.
                      </p>
                    </div>
                    <div className="flagPositionActions">
                      <button
                        type="button"
                        className="secondary subtleRefreshButton"
                        onClick={() => void refreshFlagPositions()}
                        disabled={flagPositionsLoading}
                      >
                        {flagPositionsLoading ? "Loading..." : "Refresh map"}
                      </button>
                      <button
                        type="button"
                        className="secondary subtleRefreshButton"
                        onClick={() => {
                          setShowFlagPositionManager(false);
                          setFlagPositionHonoree(null);
                        }}
                      >
                        Hide
                      </button>
                    </div>
                  </div>

                  <div className="flagPositionSummary">
                    <span><strong>{flagPositions.length}</strong> total positions</span>
                    <span><strong>{openFlagPositionCount}</strong> open</span>
                    {flagPositionHonoree ? (
                      <span>
                        Assigning <strong>{displayNameWithNickname(flagPositionHonoree.fullName, flagPositionHonoree.nickname)}</strong>
                      </span>
                    ) : (
                      <span>Choose “Assign flag position” from an honoree search result to add someone to an open position.</span>
                    )}
                  </div>

                  <p className="helperText flagAssignmentHelp">
                    Click Assign on an open flag position to choose from honorees who do not currently have a flag grid.
                  </p>

                  <div className="flagSeatLegend" aria-label="Flag position legend and quick filters">
                    <button
                      type="button"
                      className={`legendButton ${flagPositionOccupancyFilter === "all" ? "isActive" : ""}`}
                      onClick={() => setFlagPositionOccupancyFilter("all")}
                    >
                      All
                    </button>
                    <button
                      type="button"
                      className={`legendButton ${flagPositionOccupancyFilter === "open" ? "isActive" : ""}`}
                      onClick={() => setFlagPositionOccupancyFilter("open")}
                    >
                      <i className="legendOpen" /> Open
                    </button>
                    <button
                      type="button"
                      className={`legendButton ${flagPositionOccupancyFilter === "occupied" ? "isActive" : ""}`}
                      onClick={() => setFlagPositionOccupancyFilter("occupied")}
                    >
                      <i className="legendOccupied" /> Occupied
                    </button>
                  </div>

                  <div className="flagPositionFilters" aria-label="Flag position filters">
                    <label>
                      <span className="fieldLabelText">Search positions</span>
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

                  <div className="flagPositionSummary filteredFlagPositionSummary">
                    <span><strong>{filteredFlagPositions.length}</strong> shown</span>
                    <span><strong>{visibleOpenFlagPositionCount}</strong> shown open</span>
                  </div>

                  <div className="flagSeatMap" role="grid" aria-label="Flag position seat map">
                    {flagPositionRows.length === 0 ? (
                      <p className="emptyState">No flag positions match the current filters.</p>
                    ) : (
                      flagPositionRows.map((row) => (
                        <div className="flagSeatRow" role="row" key={row.rowLabel}>
                          <div className="flagSeatRowLabel">{row.rowLabel}</div>
                          <div className="flagSeatCells">
                            {row.positions.map((position) => {
                              const isBusy = flagPositionBusyId === position.flagGridId;

                              return (
                                <div
                                  key={position.flagGridId}
                                  className={[
                                    "flagSeat",
                                    position.isOpen ? "isOpen" : "isOccupied"
                                  ].filter(Boolean).join(" ")}
                                  role="gridcell"
                                  aria-label={`${position.flagGridName} ${position.isOpen ? "open" : `occupied by ${position.honoreeName || "honoree"}`}`}
                                >
                                  <strong>{position.flagGridName}</strong>
                                  {position.isOpen ? (
                                    <>
                                      <span>Open</span>
                                      <button
                                        type="button"
                                        className="seatAction"
                                        disabled={!position.isOpen || isBusy}
                                        onClick={() => void openAssignFlagPositionModal(position)}
                                      >
                                        {isBusy ? "Assigning..." : "Assign"}
                                      </button>
                                    </>
                                  ) : (
                                    <>
                                      <span>{position.honoreeName || "Assigned"}</span>
                                      <small>
                                        {[position.rank, position.serviceBranchName].filter(Boolean).join(" • ") || "Occupied"}
                                      </small>
                                      <button
                                        type="button"
                                        className="seatAction clearSeatAction"
                                        disabled={isBusy}
                                        onClick={() => void clearFlagPosition(position)}
                                      >
                                        {isBusy ? "Removing..." : "Remove"}
                                      </button>
                                    </>
                                  )}
                                </div>
                              );
                            })}
                          </div>
                        </div>
                      ))
                    )}
                  </div>
                </section>
              ) : (
                <section id="flag-position-manager" className="flagPositionManager collapsedFlagPositionManager" aria-label="Flag position manager">
                  <div className="sectionHeader">
                    <div>
                      <p className="eyebrow">Flag positions</p>
                      <h3>Flag position manager</h3>
                      <p className="helperText">
                        Add honorees only to open positions and remove honorees from occupied positions.
                      </p>
                    </div>
                    <button
                      type="button"
                      className="secondary exportExcelButton"
                      onClick={() => setShowFlagPositionManager(true)}
                    >
                      Open position map
                    </button>
                  </div>
                </section>
              )}

              <details className="printCenterIntro compactPrintCenter">
                <summary>
                  <div>
                    <p className="eyebrow">Print Center</p>
                    <h3>Reprint workflow</h3>
                  </div>
                  <div className="pdfHealth">
                    <strong>{printQueueMissingPdfCount === 0 ? "PDFs look ready" : `${printQueueMissingPdfCount} PDF warning(s)`}</strong>
                    <span>{selectedPrintQueueItems.length} selected for the next batch</span>
                  </div>
                </summary>
                <p className="printCenterDetails">
                  Add records to the queue, verify PDFs, select the cards, download one merged PDF, then mark printed after the physical cards are accepted.
                </p>
              </details>

              {pendingReviews.length === 0 ? (
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
                            <strong>{item.honoreeName}</strong>
                            <br />
                            <span>{[item.rank, item.serviceBranchName].filter(Boolean).join(" • ")}</span>
                          </td>
                          <td data-label="Flag grid">{item.flagGridName || item.flagGridId}</td>
                          <td data-label="Submitted by">
                            {item.claimantName || item.claimantEmail}
                            <br />
                            <span>{item.claimantEmail}</span>
                          </td>
                          <td data-label="Submitted">{formatDate(item.submittedUtc)}</td>
                          <td data-label="Actions" className="rowActions stackedActions">
                            <button
                              type="button"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => approveReview(item, true)}
                            >
                              {adminBusyId === item.changeRequestId ? "Approving..." : "Approve + reprint"}
                            </button>
                            <button
                              type="button"
                              className="secondary"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => approveReview(item, false)}
                            >
                              {adminBusyId === item.changeRequestId ? "Approving..." : "Approve only"}
                            </button>
                            <button
                              type="button"
                              className="danger"
                              disabled={adminBusyId === item.changeRequestId}
                              onClick={() => rejectReview(item)}
                            >
                              {adminBusyId === item.changeRequestId ? "Rejecting..." : "Reject"}
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              <div id="reprint-queue" className="sectionHeader printHeader">
                <div>
                  <p className="eyebrow">Card printing</p>
                  <h2>
                    Reprint queue
                    <span className="countBadge">{printQueue.length}</span>
                  </h2>
                </div>
                <div className="actions printActions">
                  <span className="selectedCount">{selectedPrintIds.length} selected</span>

                  <button
                    type="button"
                    className="secondary"
                    onClick={downloadSelectedPrintPdf}
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

              {printQueue.length === 0 ? (
                <p>No approved cards are waiting for reprint.</p>
              ) : (
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
                              <a className="textLink" href={honoreePdfUrl(item.honoreeId)} target="_blank" rel="noreferrer">
                                Open PDF
                              </a>
                            ) : (
                              <span className="pdfWarning">Missing PDF</span>
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

          {selectedClaim || isNominating ? (
            <section id={isNominating ? "nominate" : undefined} ref={formCardRef} className="card formCard">
              <div className="sectionHeader">
                <div>
                  <p className="eyebrow">
                    {isNominating
                      ? "New nomination"
                      : selectedClaim?.claimStatus === "AdminDirectEdit"
                        ? "Administrator edit"
                        : `Claim #${selectedClaim?.id}`}
                  </p>
                  <h2>
                    {isNominating
                      ? "Nominate a veteran or first responder"
                      : selectedClaim?.claimStatus === "AdminDirectEdit"
                        ? `Directly edit ${selectedClaim?.honoreeName || "honoree record"}`
                        : `Manage flag record for ${selectedClaim?.flagGridName || `grid ${selectedClaim?.flagGridId}`}`}
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

              <p className="helperText">
                {isNominating
                  ? "Submit the honoree information for admin review. The nomination will be listed under your claimed flags, and you will be recorded as the submitter."
                  : selectedClaim?.claimStatus === "AdminDirectEdit"
                    ? "As an administrator, you can save these changes directly and add the card to the reprint queue without claiming the flag."
                    : "You can submit updates to this flag record whenever needed. Changes go to the Plano Flags of Honor admin team for approval before publishing and reprinting."}
              </p>

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
                    (!isNominating && selectedClaim?.honoreeId
                      ? honoreePhotoUrl(selectedClaim.honoreeId)
                      : selectedClaim?.honoreeImageUrl)) ? (
                    <img
                      className="honoreePhotoPreview"
                      src={selectedPhotoPreviewUrl || currentPhotoSourceUrl()}
                      alt={`${selectedClaim?.honoreeName || "Current honoree"} photo`}
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

                <div className="actions wide">
                  {!isNominating ? (
                    <button type="button" className="secondary" disabled={saving} onClick={saveDraft}>
                      {saving ? "Saving..." : "Save draft"}
                    </button>
                  ) : null}
                  <button type="submit" disabled={saving}>
                    {saving
                      ? "Submitting..."
                      : isNominating
                        ? "Submit nomination"
                        : selectedClaim?.claimStatus === "AdminDirectEdit"
                          ? "Save and queue reprint"
                          : "Submit changes for review"}
                  </button>
                </div>
              </form>
            </section>
          ) : null}


            </>
          ) : null}

      <footer className="siteCredit" aria-label="Application credit">
        <span>Built by</span>
        <a href="https://www.kneco.com" target="_blank" rel="noreferrer" aria-label="KNECO, Inc. website">
          <img src={knecoLogoBlue} alt="KNECO, Inc." />
        </a>
      </footer>
    </main>
  );
}