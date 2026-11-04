import { useEffect, useLayoutEffect, useState } from "react";
import { ensureBrowserTelemetry, subscribeBrowserRoute } from "./browserTelemetry.js";
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
  const shown = cookieNoteVisible(choice, path);

  useEffect(() => subscribeBrowserRoute(setPath), []);

  useLayoutEffect(() => {
    const nodes = () => [
      document.body,
      ...document.querySelectorAll("main"),
      ...document.querySelectorAll(".publicFlagMap")
    ];
    const clear = () => {
      for (const node of nodes()) {
        if (!(node instanceof HTMLElement)) continue;
        node.style.paddingBottom = node.dataset.cookieNoteBase ?? "";
        delete node.dataset.cookieNoteBase;
        delete node.dataset.cookieNotePad;
        if (node.dataset.cookieNoteMax !== undefined) {
          node.style.maxHeight = node.dataset.cookieNoteMax;
          node.style.overflowY = node.dataset.cookieNoteOverflow ?? "";
          delete node.dataset.cookieNoteMax;
          delete node.dataset.cookieNoteOverflow;
        }
      }
    };
    if (!shown) {
      clear();
      return undefined;
    }
    const note = document.getElementById("cookie-note");
    if (!note) return undefined;
    const apply = () => {
      const height = Math.ceil(note.getBoundingClientRect().height);
      for (const node of nodes()) {
        if (!(node instanceof HTMLElement)) continue;
        if (node.dataset.cookieNoteBase === undefined) node.dataset.cookieNoteBase = node.style.paddingBottom || "";
        const previous = Number(node.dataset.cookieNotePad || "0");
        const computed = parseFloat(getComputedStyle(node).paddingBottom) || 0;
        const base = Math.max(0, computed - previous);
        node.dataset.cookieNotePad = String(height);
        node.style.paddingBottom = `${base + height}px`;
        if (node.tagName === "MAIN") {
          if (node.dataset.cookieNoteMax === undefined) {
            node.dataset.cookieNoteMax = node.style.maxHeight;
            node.dataset.cookieNoteOverflow = node.style.overflowY;
          }
          node.style.maxHeight = "100dvh";
          node.style.overflowY = "auto";
        }
      }
    };
    apply();
    const observer = new ResizeObserver(apply);
    observer.observe(note);
    window.addEventListener("resize", apply);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", apply);
      clear();
    };
  }, [shown]);

  if (!shown) return null;

  return (
    <div id="cookie-note" className="toast-container position-fixed bottom-0 start-0 end-0 p-1 w-100 pe-none">
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
  );
}
