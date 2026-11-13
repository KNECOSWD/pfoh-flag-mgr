// Keep this equal to PFOH.Api.Models.HonoreeBioLimits.MaxLength.
export const HONOREE_BIO_MAX_LENGTH = 200;

export function honoreeBioLimitMessage(max = HONOREE_BIO_MAX_LENGTH) {
  return `Bio must be ${max} characters or fewer.`;
}

export function honoreeBioShortenMessage(max = HONOREE_BIO_MAX_LENGTH) {
  return `Bio must be shortened to ${max} characters before saving.`;
}
