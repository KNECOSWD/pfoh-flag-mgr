# Event kiosk setup

The event kiosk runs **signed out**. The public flag map does not need a login. Open it in a fresh browser profile that is not signed in to Plano Flags of Honor, then let the operating system keep that window in kiosk mode.

## 1. Set the exit PIN before the event

Matthew sets `PublicFlagMap:KioskExitPin` on the API app settings before the event. There is no default PIN, and the PIN is not in the website bundle. If this setting is empty, the kiosk cannot be left from the page. Closing the browser is the only way out.

Optionally set `PublicFlagMap:UnlimitedIps` to the kiosk machine's public address so that machine is not counted against the public map limit. Leave it empty unless you need that exemption.

## 2. Open a signed-out kiosk window

Use a fresh browser profile that is signed out.

- Chrome: `chrome --kiosk "https://<site>/flag-map?kiosk=1"`
- Edge: `msedge --kiosk "https://<site>/flag-map?kiosk=1"`
- Or Windows Assigned Access pointed at that same URL

`?kiosk=1` turns kiosk mode on for the browser tab. It stays on for that tab after a reload, even if the address later drops the query string. The page shows only the flag map: no site header, menu, sign-in, footer, or other links.

Check that the window does not show Log in, and that it does not redirect to a Microsoft sign-in page.

Visitors can look up flags they submitted from the Flags I submitted tab. That lookup does not ask them to sign in.

## Leaving the page

Press and hold the blank corner at the bottom left for about 3 seconds, then enter the PIN. The server checks it. A wrong PIN stays on the map, and too many tries are refused for a minute. The correct PIN leaves kiosk mode and opens Search (`/find`).

## If an admin is signed in

Kiosk mode still hides admin tools and does not call admin APIs, and the exit PIN still works. A signed-in admin token in a public browser is a risk that hiding buttons does not remove. Use a signed-out profile for the event.
