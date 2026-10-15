# Changelog

## 1.2.0

- **Download / Print Honoree PDF** on a honoree record and on My submissions opens the honoree card viewer. The same viewer is used from the review queue. The title is Honoree Card, the subtitle is the honoree name, and the viewer can show, download, or print the card.
- The red Administrator label is gone from that viewer, including for administrators. The review queue button is labeled Honoree Card.

## 1.1.0

- My submissions can open **Download / Print Honoree PDF**. Pending cards use the submitter's pending details and pending photo. Approved cards use the current honoree. Only the submitter and an administrator can open it.
- `UX_FlagClaims_ActiveFlagGrid` is unique on `FlagClaims(FlagGridId)` only when the status is Claimed, Submitted, or Approved and `FlagGridId` is not null, so more than one spot-less submission can exist.
- Startup cleanup also releases spots held by rejected or draft new submissions. Rejected and unclaimed claims do not block automatic spot assignment.
- A failed startup cleanup is logged at error level with the exception text. The site still starts.
- `GET /api/version` returns `{version, commit, builtAt}`. The footer shows the version. The version lives in `VERSION`.

## 1.0.0

- Pending honoree photos are stored and shown to the owner and administrators during review. Approval stops if that photo cannot be moved onto the honoree. ([#10](https://github.com/KNECOSWD/pfoh-flag-mgr/pull/10))
- Administrator review stays available when one pending row fails. Thumbnails last until the image unmounts. Administrators can preview a pending card. Withdrawing a submission marks it Withdrawn and removes its pending photo.
- A newer submission supersedes older pending requests for the same card. Startup cleanup keeps the newest and releases spots held by pending new submissions.
- New submissions do not take a flag spot until an administrator approves them. Approval assigns the next open spot, or tells the administrator when none is free.
- The review list marks New submission or Change and shows the field differences.
- Search and lookups do not wait on a token, and the profile is loaded once per account.
- ([#11](https://github.com/KNECOSWD/pfoh-flag-mgr/pull/11))
