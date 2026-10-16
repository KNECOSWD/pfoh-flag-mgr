# Changelog

## 2.0.1

- The public Flag Map loads one field snapshot instead of a separate request for every section. That snapshot is cached in memory for a short time and dropped when a honoree, flag spot, or branch changes.
- Branch seals are served as 128 and 256 pixel WebP images, with a PNG fallback, and they can be cached for a year. The Merchant Marine seal uses the stored file that ships with the app when the storage account does not have it.
- Honoree photos on the map are reduced to a display size. Their cache header stays no-cache, no-store, must-revalidate.
- HTML, CSS, JavaScript, and JSON responses can be compressed with Brotli or gzip. Hashed files under /assets can be cached for a year. The flag map page itself stays uncached and preloads the section list and the search index.
- The search index no longer repeats the separate name parts. Search still matches the display name, including a middle name or suffix when those words are in it.
- On the public Flag Map, the small navy, gold, and cream flag mark next to the title is gone. Other pages are unchanged.
- The field is drawn as you see it from the pavilion: row 1 is nearest the bottom of the screen, an aisle sits between rows 20 and 21, and the columns run YY through Z to AA from left to right. Every spot on that field map is drawn, including empty seats and the reserved Z01–Z09 block, plus any extra spot the data actually has. Row and column labels stay readable. A pavilion marker sits below Z01.
- Flags I submitted appears only when you are signed in and your saved name can identify you. Choosing it fits every flag tied to that sign-in and plays the wave on each of them. The response does not include your name. The signed-out event kiosk has no current submitter, so that tab stays hidden there.
- The honoree photo in the flag detail keeps its own shape. The whole picture stays visible, the height is capped, and a navy background fills the space beside it on the desktop, phone, and kiosk.
- Branch seals and logos are not drawn or requested unless fewer than 10 flags are on screen. Farther out, each flag is a colored dot. The seal fades in once that close view is reached. During a wheel or pinch, the map only moves the existing picture and redraws when the gesture ends.

## 2.0.0

- A public Flag Map is available at /flag-map by direct link. It is not in the navigation, footer, or home page. The page asks search engines not to index it.
- The map is one field you can pan and zoom. Zoomed out, it shows each section and how many flags are filled. Zooming in fades in the branch seal, then the name, then the rank. A section click zooms to that section. A flag click opens the detail, and Preview card opens the Honoree Card window.
- Search suggests honorees as you type. Choosing one zooms to that flag, outlines it in gold, and gives a short gold glow. A click on a section fits that section. A click on a flag opens it. Dragging the field does not. The wheel zoom stays smooth on a large field.
- Kiosk mode at /flag-map?kiosk=1 fills the screen for a touchscreen at the event. The first touch asks for full screen. Home and Back are on the page. Search uses a large field and large results so an on-screen keyboard works. After about a minute with no touch, the map returns to the overview and drifts slowly across the field until someone touches it.
- A link such as /flag-map?flag=14 opens that flag zoomed in, with the detail panel. The panel shows the photo when the record has one, the public details, and a QR code for that flag's link. The QR code uses the normal address, /flag-map?flag=<id>, so a phone does not open kiosk mode. A link to a flag that is not on the map leaves the overview up and says so.
- The public flag map counts requests separately for each visitor. Behind the site front end, that address is the one the front end recorded, not an address the browser can invent. The event kiosk stays under that limit: it loads the map once and does not ask again while the field is drifting. PublicFlagMap:UnlimitedIps can exempt the kiosk machine. It is empty unless the setting is filled in.
- Kiosk mode stays on for the browser tab after the address loses `kiosk=1`. The page is only the flag map. Other pages, the Back button, and links off the map return to it. After about a minute, search, the open flag, and the zoom reset, then the field drifts. Leaving it takes a corner press and the PIN in `PublicFlagMap:KioskExitPin`. There is no default PIN. The event browser should be signed out. Setup is in `docs/kiosk-setup.md`.
- Flags I submitted looks up a flag from the first and last name of the person who submitted or claimed it. The lookup is sent in the request body. Those names are used only to match. They are not shown, they are not logged, and they are not in the honoree search list. A value that looks like an email, a handle, or a name with digits is not used. The submission form says your name can be used for that lookup at the event kiosk.
- Pinch zoom on a narrow screen updates the field once per frame. Kiosk buttons, search tabs, and the corner control are at least 56 pixels.
- The admin Flag Map is unchanged.

## 1.5.2

- Print Center uses the Honoree Card viewer. Open PDF on a row and Download merged PDF for the selection open that window immediately, show a loading message until the PDF is ready, then offer Download and Print. The merged view is titled Print Center and shows how many cards are selected, such as "28 cards selected". A single card uses the honoree name. If the PDF cannot be opened, the message stays in the window.

## 1.5.1

- Conflicts served and awards keep their stored line breaks. Each item is drawn on its own line. When that list would crowd the bio, the same items are joined with semicolons instead of being cut off mid-name. A section that still does not fit is left off the card.

## 1.5.0

- The Honoree Card window opens as soon as you click. It shows a loading message until the card is ready, then shows the card in that same window. If the card cannot be opened, the message stays in the window.
- When a honoree has conflicts served or awards, those lines are drawn under the bio in smaller left-aligned type. The bio is laid out first. A section is included only when the whole wrapped line fits; otherwise that section is left off the card. Empty fields are not shown.

## 1.4.0

- The honoree card bio, the block under the name, dates, and branch, is left-aligned on every line. A short line such as "Our Hero!" stays together. The name, dates, and branch stay centered.

## 1.3.0

- Withdrawing an approved honoree adds a **Withdrawal** item to the review queue, next to New submission and Change. The item shows the honoree, flag spot, submitter, and withdrawn date. The spot stays assigned until an administrator confirms **Removed from field**, which releases the spot and deactivates the record without deleting it.
- Withdrawing a pending submission that is not yet approved still leaves the queue immediately.

## 1.2.0

- **Download / Print Honoree PDF** on a honoree record and on My submissions opens the honoree card viewer. The same viewer is used from the review queue. The title is Honoree Card, the subtitle is the honoree name, and the viewer can show, download, or print the card.
- The red Administrator label is gone from that viewer, including for administrators. The review queue button is labeled Honoree Card.
- The Honoree Card viewer uses most of the screen so the whole card fits on the page. Phones open it full screen.

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
