import { useEffect, useState } from "react";
import { subscribeTelemetryEnabled, telemetryIsEnabled } from "./browserTelemetry.js";
import {
  COOKIE_NOTE_BUTTON,
  COOKIE_NOTE_TEXT,
  browserStorage,
  cookieNoteIsDismissed,
  dismissCookieNote
} from "./cookieNote.js";

export function CookieNote() {
  const [enabled, setEnabled] = useState(telemetryIsEnabled);
  const [dismissed, setDismissed] = useState(() => cookieNoteIsDismissed(browserStorage()));

  useEffect(() => subscribeTelemetryEnabled(setEnabled), []);

  if (!enabled || dismissed) return null;

  return (
    <div id="cookie-note" className="toast-container position-fixed bottom-0 start-0 end-0 p-3 w-100 pe-none">
      <div className="toast show w-100 pe-auto" role="status" aria-live="polite" aria-atomic="true">
        <div className="toast-body d-flex flex-column flex-sm-row flex-wrap align-items-sm-center gap-2">
          <p className="mb-0 flex-grow-1">{COOKIE_NOTE_TEXT}</p>
          <button
            type="button"
            className="btn btn-primary btn-sm align-self-end align-self-sm-center"
            onClick={() => {
              dismissCookieNote(browserStorage());
              setDismissed(true);
            }}
          >
            {COOKIE_NOTE_BUTTON}
          </button>
        </div>
      </div>
    </div>
  );
}
