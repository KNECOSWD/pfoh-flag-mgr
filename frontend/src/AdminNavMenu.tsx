import { type KeyboardEvent, type MouseEvent, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

export type AdminNavItem<Route extends string = string> = {
  route: Route;
  label: string;
};

/**
 * The signed-in Admin menu. Real links keep each admin route's address.
 * Arrow keys move through the menu; Enter and Space follow the link; Escape closes it.
 */
export function AdminNavMenu<Route extends string>({
  items,
  activeRoute,
  menuId,
  layout,
  onNavigate
}: {
  items: AdminNavItem<Route>[];
  activeRoute: string;
  menuId: string;
  layout: "bar" | "drawer";
  onNavigate: (event: MouseEvent<HTMLAnchorElement>, route: Route) => void;
}) {
  const [open, setOpen] = useState(false);
  const [panelBox, setPanelBox] = useState<{ top: number; left: number } | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const itemRefs = useRef<Array<HTMLAnchorElement | null>>([]);
  const pendingFocus = useRef<number | null>(null);
  const onAdminPage = items.some((item) => item.route === activeRoute);

  useEffect(() => {
    setOpen(false);
  }, [activeRoute]);

  useEffect(() => {
    if (!open) return;
    if (pendingFocus.current != null) {
      itemRefs.current[pendingFocus.current]?.focus();
      pendingFocus.current = null;
    }

    const place = () => {
      if (layout !== "bar") return;
      const rect = buttonRef.current?.getBoundingClientRect();
      if (!rect) return;
      const width = 184;
      setPanelBox({
        top: rect.bottom + 6,
        left: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8))
      });
    };
    place();
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target as Node;
      if (rootRef.current?.contains(target) || panelRef.current?.contains(target)) return;
      setOpen(false);
    };
    document.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open, layout]);

  function openAt(index: number) {
    pendingFocus.current = index;
    setOpen(true);
    if (open) itemRefs.current[index]?.focus();
  }

  function onButtonKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      openAt(0);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      openAt(items.length - 1);
    } else if (event.key === "Escape" && open) {
      event.preventDefault();
      setOpen(false);
    }
  }

  function onItemKeyDown(event: KeyboardEvent<HTMLAnchorElement>, index: number) {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      itemRefs.current[(index + 1) % items.length]?.focus();
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      itemRefs.current[(index - 1 + items.length) % items.length]?.focus();
    } else if (event.key === "Home") {
      event.preventDefault();
      itemRefs.current[0]?.focus();
    } else if (event.key === "End") {
      event.preventDefault();
      itemRefs.current[items.length - 1]?.focus();
    } else if (event.key === "Escape") {
      event.preventDefault();
      setOpen(false);
      buttonRef.current?.focus();
    } else if (event.key === "Tab") {
      setOpen(false);
    }
  }

  const panel = (
    <div
      ref={panelRef}
      id={menuId}
      className={layout === "bar" ? "adminNavPanel isBar" : "adminNavPanel"}
      role="menu"
      hidden={layout === "drawer" ? !open : undefined}
      style={layout === "bar" && panelBox ? { top: panelBox.top, left: panelBox.left } : undefined}
    >
      {items.map((item, index) => (
        <a
          key={item.route}
          ref={(node) => { itemRefs.current[index] = node; }}
          role="menuitem"
          className={activeRoute === item.route ? "isActive" : ""}
          href={item.route}
          aria-current={activeRoute === item.route ? "page" : undefined}
          onClick={(event) => {
            setOpen(false);
            onNavigate(event, item.route);
          }}
          onKeyDown={(event) => onItemKeyDown(event, index)}
        >
          {item.label}
        </a>
      ))}
    </div>
  );

  return (
    <div ref={rootRef} className={open ? "adminNavMenu isOpen" : "adminNavMenu"}>
      <button
        ref={buttonRef}
        type="button"
        className={onAdminPage ? "adminNavButton isActive" : "adminNavButton"}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={menuId}
        onClick={() => setOpen((current) => !current)}
        onKeyDown={onButtonKeyDown}
      >
        Admin
        <span aria-hidden="true" className="adminNavCaret">▾</span>
      </button>
      {layout === "drawer" ? panel : open ? createPortal(panel, document.body) : null}
    </div>
  );
}
