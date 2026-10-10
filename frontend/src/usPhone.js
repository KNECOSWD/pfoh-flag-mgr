export const usPhoneErrorMessage = "Please enter a 10-digit US mobile number, like 214-555-1234.";

export function normalizeUsPhone(value) {
  const digits = nationalDigits(value);
  return digits ? `+1${digits}` : "";
}

export function formatUsPhone(value) {
  const digits = nationalDigits(value);
  if (!digits) {
    return "";
  }

  return `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
}

export function displayUsPhone(value) {
  const formatted = formatUsPhone(value);
  return formatted || String(value ?? "").trim();
}

function nationalDigits(value) {
  const trimmed = String(value ?? "").trim();
  if (!trimmed || trimmed.length > 32) return "";
  if (!/^[\d\s().+-]+$/.test(trimmed)) return "";
  const plusAt = trimmed.indexOf("+");
  if (plusAt > 0 || (plusAt === 0 && trimmed.indexOf("+", 1) >= 0)) return "";

  let digits = trimmed.replace(/\D/g, "");
  if (digits.length === 11 && digits.startsWith("1")) {
    digits = digits.slice(1);
  }

  if (!/^[2-9]\d{2}[2-9]\d{6}$/.test(digits)) return "";
  if (digits[1] === "1" && digits[2] === "1") return "";
  return digits;
}
