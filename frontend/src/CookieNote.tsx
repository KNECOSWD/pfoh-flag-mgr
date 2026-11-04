import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ensureBrowserTelemetry, loadTelemetryConfig, subscribeBrowserRoute } from "./browserTelemetry.js";
import {
  COOKIE_NOTE_ACCEPT,
  COOKIE_NOTE_DECLINE,
  COOKIE_NOTE_TEXT,
  acceptCookieNote,
  browserStorage,
  cookieNoteChoice,
  cookieNoteVisible,
  declineCookieNote
} from "./cookieNote.js";

export function CookieNote() {
  const [choice, setChoice] = useState(() => cookieNoteChoice(browserStorage()));
  const [path, setPath] = useState(() => (typeof window === "undefined" ? "/" : window.location.pathname));
  const [telemetryEnabled, setTelemetryEnabled] = useState(false);
  const [spacerHeight, setSpacerHeight] = useState(0);
  const [mapHost, setMapHost] = useState<HTMLElement | null>(null);
  const noteRef = useRef<HTMLDivElement>(null);
  const shown = cookieNoteVisible(choice, path, telemetryEnabled);

  useEffect(() => subscribeBrowserRoute(setPath), []);

  useEffect(() => {
    let cancelled = false;
    void loadTelemetryConfig(undefined, import.meta.env.VITE_API_BASE_URL ?? "").then((config) => {
      if (!cancelled) setTelemetryEnabled(Boolean(config.enabled));
    });
    return () => {
      cancelled = true;
    };
  }, []);

  useLayoutEffect(() => {
    if (!shown) {
      setMapHost(null);
      return undefined;
    }
    const host = document.querySelector(".publicFlagMap");
    setMapHost(host instanceof HTMLElement ? host : null);
    return undefined;
  }, [shown, path]);

  useLayoutEffect(() => {
    const note = noteRef.current;
    if (!shown || !note) return undefined;
    const apply = () => setSpacerHeight(Math.ceil(note.getBoundingClientRect().height));
    apply();
    const observer = new ResizeObserver(apply);
    observer.observe(note);
    return () => observer.disconnect();
  }, [shown]);

  if (!shown || typeof document === "undefined") return null;

  const note = (
    <>
      <div id="cookie-note-spacer" className="flex-shrink-0 w-100" style={{ height: spacerHeight }} aria-hidden="true" />
      <div ref={noteRef} id="cookie-note" className="toast-container position-fixed bottom-0 start-0 end-0 p-1 w-100 pe-none">
        <div className="toast show w-100 border-0 pe-auto" role="status" aria-live="polite" aria-atomic="true">
          <div className="toast-body d-flex flex-column gap-1 p-2">
            <p className="mb-0 small lh-sm">{COOKIE_NOTE_TEXT}</p>
            <div className="d-flex flex-row align-items-center gap-2">
              <button
                type="button"
                className="btn btn-outline-secondary btn-sm"
                onClick={() => {
                  acceptCookieNote(browserStorage());
                  setChoice("accepted");
                  void ensureBrowserTelemetry({
                    apiBase: import.meta.env.VITE_API_BASE_URL ?? ""
                  });
                }}
              >
                {COOKIE_NOTE_ACCEPT}
              </button>
              <button
                type="button"
                className="btn btn-link btn-sm py-0"
                onClick={() => {
                  declineCookieNote(browserStorage());
                  setChoice("declined");
                }}
              >
                {COOKIE_NOTE_DECLINE}
              </button>
            </div>
          </div>
        </div>
      </div>
    </>
  );

  return (
    <>
      {createPortal(note, document.body)}
      {mapHost ? createPortal(
        <div className="flex-shrink-0 flex-grow-0 w-100" style={{ height: spacerHeight }} aria-hidden="true" />,
        mapHost
      ) : null}
    </>
  );
}
