export const notRecorded = "Not recorded";

/**
 * @param {string | null | undefined} value
 */
export function formatDeactivated(value) {
  if (!value) return notRecorded;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return notRecorded;
  return date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

/**
 * @param {{ canReactivate?: boolean, slotStatus?: string }} row
 */
export function needsFlagChoice(row) {
  return Boolean(row?.canReactivate) && (row.slotStatus === "taken" || row.slotStatus === "reserved" || row.slotStatus === "missing");
}

/**
 * @param {{ formerFlagGridName?: string | null, slotStatus?: string, slotDetail?: string | null }} row
 */
export function conflictLead(row) {
  const flag = row.formerFlagGridName ? `Flag ${row.formerFlagGridName}` : "The former flag";
  if (row.slotStatus === "reserved") return `${flag} is reserved. Choose an open flag.`;
  if (row.slotStatus === "missing") return `${flag} is no longer on the field. Choose an open flag.`;
  if (row.slotStatus === "taken") {
    return row.slotDetail
      ? `${flag} is not open. ${row.slotDetail} Choose an open flag.`
      : `${flag} is not open. Choose an open flag.`;
  }
  return "Choose an open flag.";
}

/**
 * @param {{
 *   name: string,
 *   canReactivate: boolean,
 *   slotStatus: string,
 *   formerFlagGridId?: number | null,
 *   formerFlagGridName?: string | null,
 *   slotDetail?: string | null
 * }} row
 * @param {{ id: number, flagGridName: string } | null | undefined} chosen
 */
export function restorationPlan(row, chosen) {
  if (!row.canReactivate) {
    return {
      blocked: true,
      flagGridId: null,
      flagName: null,
      sentence: "",
      message: row.slotDetail || "This honoree cannot be restored from this page."
    };
  }

  if (row.slotStatus === "none") {
    return {
      blocked: false,
      flagGridId: null,
      flagName: null,
      sentence: `Restore ${row.name} as an active honoree with no flag position.`
    };
  }

  if (row.slotStatus === "free" && row.formerFlagGridId) {
    return {
      blocked: false,
      flagGridId: row.formerFlagGridId,
      flagName: row.formerFlagGridName,
      sentence: `Restore ${row.name} to flag ${row.formerFlagGridName}.`
    };
  }

  if (!chosen) {
    return {
      blocked: true,
      flagGridId: null,
      flagName: null,
      sentence: "",
      message: conflictLead(row)
    };
  }

  return {
    blocked: false,
    flagGridId: chosen.id,
    flagName: chosen.flagGridName,
    sentence: `Restore ${row.name} to flag ${chosen.flagGridName}.`
  };
}

/**
 * @param {Array<{ sentence?: string }>} plans
 */
export function confirmRestorationMessage(plans) {
  return plans.map((plan) => plan.sentence).filter(Boolean).join(" ");
}
