# Changelog

## 2.0.20

- Remove from queue in the Print Center still succeeds when the database rejects the cancelled-claim update. The card leaves the queue, and the server records a warning with the change-request id, the claim id, and the database error. The warning does not include names or other personal details.
- On a phone, the Flag Map grid fills the height of the map, so the empty band under the field is gone. The Sections control is only as wide as its label. On a wide screen the map is unchanged.

## 2.0.19

- On the public Flag Map, Search honorees is in the toolbar instead of a row above it. On a wide screen the search sits between the zoom buttons and the Sections list. On a phone, the zoom buttons and Sections stay on the first line and the search fills the second. The map uses the space that row used to take. Search results are unchanged, and the legend stays under the field.
- Remove from queue in the Print Center succeeds. It had been returning an error and leaving the card in the queue.
- Mark printed with no matching cards records nothing. Print history does not show an entry when the count is 0.

## 2.0.18

- In the Print Center, Mark printed opens a window that shows how many cards are in the batch and an optional comment. Confirm records one print event: who marked it, when, the batch, the card count, and the comment. Cancel leaves the queue as it was. Print history lists those events, newest first, with the date and time in Colombia Time (COT).
- The PlanoFlagsOfHonor.com link in the phone menu now fits fully at narrow widths. The rest of the navigation is unchanged.

## 2.0.17

- Signed-in administrators find the Admin menu at the end of the navigation, after PlanoFlagsOfHonor.com, on desktop and in the phone menu. The menu still contains Review, Print Center, and Flag Manager. Signed-out visitors and the kiosk do not see it.

## 2.0.16

- In the Print Center, the line under the heading after a merged PDF is ready now ends at "140 cards selected, merged."
- Signed-in administrators open Review, Print Center, and Flag Manager from one Admin menu, on desktop and in the phone menu. Search, Flag Map, and the rest of the public navigation are unchanged, and signed-out visitors do not see the Admin menu. Each admin page keeps its own address.

## 2.0.15

- In the Print Center, the merged PDF now has a readable name instead of a long code. The PDF viewer's header shows, for example, "PFOH Reprint Cards - 2026-10-20 - 140 cards", using today's date in Bogotá time and the number of cards in the PDF. Download saves the file with the same name, ending in .pdf.
- The public Flag Map at /flag-map now has a Flag Map link in the site navigation, right after Search, on desktop and in the phone menu. It still works signed out, and what it shows is unchanged. For admins, the Flag Map item in the admin navigation is now called Flag Manager. It still opens the same admin page, and only admins see it. That page's own heading now also reads Flag Manager.
- Search engines may now index the public Flag Map page at /flag-map. robots.txt no longer blocks it, and the page no longer asks not to be indexed. The Flag Map's data requests under /api/public/flag-map still ask search engines not to index them. Every other page is unchanged.

## 2.0.14

- In the Print Center, Download merged PDF now regenerates every selected card before merging, using the same Regenerate PDF step as the admin button. A card that already has a stored PDF is regenerated too, so every page in the merged PDF has its full background, seal, and logos. Before, a stored card without its artwork could end up in the merged PDF. The progress screen counts "Regenerating card 37/140" and then "Merging card 37/140". There is still no time limit, Close still cancels, and if any card fails, the Print Center names it and builds no partial PDF.
- A card PDF is never saved or shown without its background and Rotary logo. If that artwork can't be loaded, the card is not created and the error says so. Regenerate PDF reports it as a failed upload, and the public card link returns an error naming the missing artwork instead of a bare card.
- The old server-side merge, which could save cards without their artwork, has been removed. The Print Center hasn't used it since 2.0.13.

## 2.0.13

- In the Print Center, Download merged PDF no longer fails on large batches with "signal is aborted without reason". Each selected card is now checked on its own, and a progress screen counts up as it goes, for example 37/140. Any card whose PDF is missing is generated first, the same way Regenerate PDF does it, and then every card is merged into one PDF in the browser. There is no time limit on the batch. Close cancels it.
- If a card can't be prepared, no card is skipped and no partial PDF is built. The Print Center lists each failed card by honoree name and number, with the step that failed and the server's message. Fix or remove those cards and try again.
- CI runs once per change: on pull requests, and on pushes to main. Pushes to feature branches no longer start a second, duplicate run. Deploys are unchanged.

## 2.0.12

- On the public Flag Map, every row number 1–40 on both sides and every section letter YY to AA now shows at every zoom level, including the whole-field view. Labels are no longer skipped. Each label sits exactly at the centre of its row or column, using the same position maths as the spots, so the aisle between Z and M and the aisle between rows 20 and 21 show at their true size. When the field is small, the labels get smaller instead. All row numbers share one size, and all letters share another, picked so that no two neighbouring labels overlap. On the 1440-wide map, rows are 13px and letters 14px. On the 390-wide phone map, letters are about 7.5px and rows about 6.7px. On the 320-wide phone map, letters are about 5.4px and rows about 5px. The label strips, the whole-field view, zoom limits, edge snapping, the toolbar, the flag card, Esc, and kiosk mode are unchanged.

## 2.0.11

- On the public Flag Map, the row numbers 1–40 and the section letters YY to AA now show at every zoom level, including the whole-field view. When the rows or columns are too small for every label, the labels keep a readable size and only some of them show: the first and last row and the first and last letter always show, plus every second, fifth or tenth row and every second or third letter, so labels never overlap. Each label still lines up with its row or column.
- In the whole-field view, the navy label strips now sit just outside the field instead of at the far edges of the map, so the labels are next to the rows and columns they name. As soon as the field is bigger than the map, the strips go back to the map edges as before. Taps still pass through the strips, and the flag card, toolbar, list, and key still sit above them. Label sizes when zoomed in, the whole-field view, zoom limits, edge snapping, the toolbar, the flag card, Esc, and kiosk mode are unchanged.

## 2.0.10

- On the public Flag Map, the row numbers on the right edge now sit centred in their navy strip, with the same space on each side as the numbers on the left edge, instead of pressing against the right edge of the map. The section letters along the bottom keep a small gap from the corners, so the first and last letters no longer touch a corner. Label sizes, the whole-field view, zoom limits, and edge snapping are unchanged.
- The Preview card and Close buttons on the Flag Map's flag card always stay visible at the bottom of the card. The photo, name, bio, awards, and QR code scroll above them, so a long bio no longer pushes the buttons out of reach. On phones the buttons clear the home indicator. This works on desktop, phone, and the kiosk. The buttons' labels, sizes, order, and Esc behavior are unchanged.

## 2.0.9

- On the public Flag Map, the pinned row numbers and section letters each sit on a solid navy strip along the left, right, and bottom edges of the map, so they are readable when zoomed in instead of sitting on top of the white spot cards. The map moves under the strips. Each number still lines up with its row and each letter with its column, and a label whose row or column is out of view still hides. Taps pass through the strips, and the flag card, toolbar, list, and key still sit above them. Label sizes are unchanged. Kiosk mode, Esc, and the toolbar list are unchanged.
- The whole-field view, zoom limits, and edge snapping now keep the map inside the label strips. The outer sections YY and AA and the Pavilion are no longer hidden under the strips at 320, 390, or 1440 wide, or on the kiosk. A section letter whose column is behind a side strip hides, and no row number draws into the bottom corners.
- The public Flag Map toolbar spans the full width of the map, the same width as the search box, on desktop and phone. Zoom out, Zoom in, Full screen, and Reset view sit together on the left, and the Sections or Spots list sits at the far right. On a phone the list fills the space to the right of the buttons and stays on one row, with its text cut short if needed. Button sizes, labels, keyboard arrows, and the kiosk look are unchanged, and the key does not move.

## 2.0.8

- On the public Flag Map in full screen, each press of Esc closes only the topmost open thing. An open flag card closes first. Next, the search suggestions or the in-view list close, and a pending jump is canceled. Only when nothing is open does Esc leave full screen. The toolbar's full-screen button and kiosk mode are unchanged.
- The row numbers 1–40 stay on the left and right edges of the map, and the section letters stay along the bottom edge, however far the map is panned or zoomed. Each number lines up with its row and each letter with its column. A label whose row or column is out of view hides. The labels never block a tap on a spot, and the flag card, toolbar, list, and key sit above them. Label sizes and the letter-only look are unchanged, and the kiosk works the same way.
- The Sections or Spots list is part of the map toolbar, in the same group as Zoom out, Zoom in, Full screen, and Reset view, on one row. It works as before: sorted, limited to what is in view, and choosing an entry zooms to it. On a narrow phone the list text may be cut short, but the toolbar stays on one row.

## 2.0.7

- Section labels on the public Flag Map field show only their letter, such as Y, AA, or Z. The flag-number range no longer sits under the letter. Every letter keeps the same size and weight. The Service Flags label and the in-view list entries, such as "C 21–40", are unchanged.
- The public Flag Map opens in its full-screen view, with the site header and footer hidden. The toolbar's Exit full screen button and Esc leave it, and Full screen brings it back. Kiosk mode and its lockdown are unchanged.

## 2.0.6

- The public Flag Map's in-view list is sorted. Zoomed out, sections go by letter, A, AA, B, C through Y, YY, Z, with a letter's 1–20 block before its 21–40 block. Service Flags sit with Z, first, as Z 1–10, before Z 11–20. Zoomed in, spots go by letter and then by number, so A-2 comes before A-10 and AA-3 before B-1. Everything else about the list is unchanged.

## 2.0.5

- The public Flag Map shows the row numbers 1 through 40 on the right side of the field as well as the left. Each number sits on the same line as its row on both sides.
- Column Z sits right next to N, the same distance apart as any two columns. The only aisle between columns is the one between Z and M.
- The "Sections" label is now a list of what is on screen. Zoomed out it lists each section block in view, such as "C 21–40", and Service Flags. Once the zoom shows flag numbers it lists each flag spot in view, such as "A-14 · Occupied" with the honoree name, or "A-14 · Open". The closed list shows the kind and how many, such as "Sections (57)". Choosing a section zooms to it, and choosing a spot zooms to that spot and outlines it in gold. With the keyboard, the arrow keys move through the list and Enter goes there. The list works on a phone, a desktop, and the kiosk. It updates once a zoom or a drag stops, so it never lists a view passed on the way.
- The Pavilion bar under Z is four times wider and twice as tall. The opening fit still holds the whole field, the row numbers, and the Pavilion.
- Zoom out, Zoom in, Full screen or Exit full screen, and Reset view are one toolbar of icon buttons. Each button has a name for screen readers and a tooltip on hover and keyboard focus. The arrow keys, Home, and End move between the buttons. The kiosk keeps its Home and Back buttons next to the toolbar, without Reset view.
- The Occupied and Open key sits under the bottom-right corner of the field, so it never covers a spot.
- Every section label on the zoomed-out field uses the same size, weight, and placement, including YY, W, M, AA, both Z blocks, and Service Flags. The letter sits at the top of its block with the flag numbers under it, sized so the widest name fits the narrowest column. The numbers go on one line when they fit, or as three lines such as "21", "–", "40". In columns too narrow for a two-letter name across, every block stacks its letter and numbers one character per line, and each block keeps room for two letters, so the numbers start at the same height in every column. The Service Flags label uses the flag-number size. When columns are narrower than about 7 pixels, every block shows its letter only.

## 2.0.4

- On a 320-pixel phone, the fully zoomed-out Flag Map labels every section block. A block too narrow for the letter and flag numbers side by side shows the letter with the numbers stacked under it, one character per line. YY and AA stack their two letters. A short block such as Z 11–20 makes its letter a little smaller so the numbers still fit, and on the live 40-row field every one of the 56 sections shows its letter and flag numbers. The Service Flags block shows "Service Flags" turned on its side, or "Service" when the full name does not fit. Every label stays inside its block, and the edge of a block that narrow is drawn thinner to leave room for the letter. On a taller field, such as 60 rows, the fit can make a column narrower than 5 pixels. The letter still draws there, sized to the whole width of the block over an inset edge, and Service Flags still shows "Service", so no section is ever blank. A block that narrow shows its letter only. Wider windows keep the 2.0.3 labels, except that W and M are no longer clipped at the edge of their block.
- From the names zoom through the closest zoom, a zoom from the buttons, a section click, a submitter fit, a wheel zoom, or a pinch ends with every spot that is more than half on screen fully inside the window, 12 pixels from each edge, on the left, right, top, and bottom. The view moves less than one spot. When no pan can do that, the zoom also changes by up to 6 percent, without changing the zoom level. On a phone the closest zoom stops where one spot still fits with that padding. A desktop and the kiosk keep the full zoom. Dragging, the arrow keys, and opening a flag are unchanged.

## 2.0.3

- Public Flag Map spots never overlap at any zoom, on a desktop, a phone, or the kiosk. Each spot uses the 2.0.0 size, 148 by 108 with a 12 gap, and the global button height no longer stretches a spot past its row.
- Zoomed out, the field shows each section by letter and the flag numbers in it, such as "C 21–40". The next step in shows each spot with only its flag number, such as "AA-11", filled when the spot is occupied and outlined with a dashed edge when it is open. A key under the search box names Occupied and Open. Closer in, the honoree name is fitted inside the spot on up to two lines. Closest adds the branch seal and rank once fewer than 10 flags are on screen. The level is set by how large a spot is on screen, so a desktop and a phone reach each level at the same spot size.
- Text inside a spot stays between 9 and 20 pixels on screen at every zoom. It is shortened with an ellipsis and never drawn past the spot edge. A long hyphenated surname can wrap at the hyphen. Row, column, section, and Pavilion labels are also sized for the screen, and they are left out when they cannot fit.
- The opening fit holds the whole grid, including the row numbers, the column letters, and the Pavilion marker, with padding on every side. The map cannot be zoomed out past that fit. A pan stops with the edge of the field just inside the window, so the left column and every other edge can always be reached, and the field cannot be dragged out of view.
- The flag detail panel sits above everything on the page at every size, including the site header and menu, the search bar, the zoom and full-screen controls, and the footer. On a phone it fills the screen over a dimmed backdrop. On a desktop and the kiosk the search box and toolbar narrow to leave room for it. Close and Escape work as before. A card preview opened from the panel, and the kiosk exit code pad, still open on top of it. Opening a flag zooms close enough for the seal and keeps the flag clear of the panel.
- Section blocks use one flat fill with no gradient.
- Z1–Z10 are the Service Flags. That area is now one flat, solid block labeled "Service Flags" at every zoom, with no per-spot flag numbers. It is no longer part of the Z section block; the rest of Z, from Z11 up, is its own section.
- Column Z has the same visible gap on both sides, between N and Z and between Z and M, the same width as the aisle between rows 20 and 21.
- Zoomed-in text is drawn sharp again after an animated zoom.

## 2.0.2

- Public Flag Map search suggestions sit on top of the field on a desktop, a phone, and the kiosk.
- Zoomed out, each zone is labeled with its letter and the flag numbers actually in that zone, such as "C 1–20", because the aisle and the reserved block split a column. The colored circles are gone.
- Zooming in shows each spot as occupied or open, and the honoree name only when it fits inside the spot. Spots stay inside their own box at every zoom. Branch seals still wait until fewer than 10 flags are on screen.
- Full screen hides the site header and footer so the map fills the window. Escape and the Full screen button leave it. Browsers that support the Fullscreen API use it, and others, including iOS, use the same full-window layout. Kiosk lockdown is unchanged.
- Submitter suggestions wait about 150 ms, then answer from a cached index of name tokens. The privacy limits are unchanged: display name only, 3-character minimum, cap of 8, rate limit, no-store, opaque token, and no names in the log.

## 2.0.1

- The public Flag Map loads one field snapshot instead of a separate request for every section. That snapshot is cached in memory for a short time and dropped when a honoree, flag spot, or branch changes.
- Branch seals are served as 128 and 256 pixel WebP images, with a PNG fallback, and they can be cached for a year. The Merchant Marine seal uses the stored file that ships with the app when the storage account does not have it.
- Honoree photos on the map are reduced to a display size. Their cache header stays no-cache, no-store, must-revalidate.
- HTML, CSS, JavaScript, and JSON responses can be compressed with Brotli or gzip. Hashed files under /assets can be cached for a year. The flag map page itself stays uncached and preloads the section list and the search index.
- The search index no longer repeats the separate name parts. Search still matches the display name, including a middle name or suffix when those words are in it.
- On the public Flag Map, the small navy, gold, and cream flag mark next to the title is gone. Other pages are unchanged.
- The field is drawn as you see it from the pavilion: row 1 is nearest the bottom of the screen, an aisle sits between rows 20 and 21, and the columns run YY through Z to AA from left to right. Every spot on that field map is drawn, including empty seats and the reserved Z01–Z09 block, plus any extra spot the data actually has. Row and column labels stay readable. A pavilion marker sits below Z01.
- The public Flag Map opens with the whole field inside the window, from row 40 down through row 1 and the pavilion marker. The fit leaves room for the site header and the bottom of the page on a desktop window, a phone, and the kiosk.
- An address under /api that the app does not provide returns 404 and is not cached. It does not return the flag map page.
- The public Flag Map search box still suggests honorees. It also suggests submitters and claimers as "All flags submitted by First Last" once the query has at least 3 letters. Choosing one fits every flag for that display name and plays the wave. The same search works signed out, including on the kiosk. Suggestions are capped, rate limited by the visitor address with no kiosk exemption, and are not cached. Email, phone, and account ids are not shown or logged. A suggestion is opened with a short-lived token.
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
