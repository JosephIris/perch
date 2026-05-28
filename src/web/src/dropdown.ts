// Custom dropdown (combobox) — replaces the native <select> because
// WebView2 renders a native select's option popup with an OS-white
// background that CSS can't reach, which looks broken on our dark Fluent
// surfaces. This one is plain DOM we fully own and style via tokens.
//
// Behaviour mirrors a native select closely enough for a settings form:
//   - click the trigger (or Space/Enter/Arrow when focused) to open
//   - click an option, or Up/Down + Enter, to choose
//   - Esc or click-outside closes without changing the value
//   - the popup is fixed-positioned under the trigger and flips up if it
//     would overflow the bottom of the viewport
//
// Keyboard handling stops propagation while open so the host dialog's own
// Esc-to-close doesn't also fire — a single Esc closes only the popup.

export interface DropdownOption {
  value: string;
  label: string;
}

export class Dropdown {
  readonly element: HTMLElement;       // the trigger button wrapper
  private readonly trigger: HTMLButtonElement;
  private readonly labelEl: HTMLElement;
  private popup: HTMLElement | null = null;
  private options: DropdownOption[] = [];
  private _value = "";
  private activeIndex = 0;

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "dropdown";

    this.trigger = document.createElement("button");
    this.trigger.type = "button";
    this.trigger.className = "dropdown__trigger";
    this.trigger.setAttribute("aria-haspopup", "listbox");
    this.trigger.setAttribute("aria-expanded", "false");

    this.labelEl = document.createElement("span");
    this.labelEl.className = "dropdown__label";
    this.trigger.appendChild(this.labelEl);

    const chevron = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    chevron.setAttribute("class", "dropdown__chevron");
    chevron.setAttribute("width", "12");
    chevron.setAttribute("height", "12");
    chevron.setAttribute("viewBox", "0 0 24 24");
    chevron.setAttribute("fill", "none");
    chevron.setAttribute("stroke", "currentColor");
    chevron.setAttribute("stroke-width", "2");
    chevron.setAttribute("stroke-linecap", "round");
    chevron.setAttribute("stroke-linejoin", "round");
    const cpath = document.createElementNS("http://www.w3.org/2000/svg", "path");
    cpath.setAttribute("d", "M6 9l6 6 6-6");
    chevron.appendChild(cpath);
    this.trigger.appendChild(chevron);

    this.element.appendChild(this.trigger);

    this.trigger.addEventListener("click", (ev) => {
      ev.stopPropagation();
      this.toggle();
    });
    this.trigger.addEventListener("keydown", (ev) => this.onTriggerKey(ev));
  }

  get value(): string { return this._value; }

  focus(): void { this.trigger.focus(); }

  setOptions(options: DropdownOption[], value: string): void {
    this.options = options;
    this._value = value;
    this.renderLabel();
  }

  dispose(): void { this.closePopup(); }

  private renderLabel(): void {
    const sel = this.options.find((o) => o.value === this._value);
    this.labelEl.textContent = sel ? sel.label : (this.options[0]?.label ?? "");
  }

  private toggle(): void {
    if (this.popup) this.closePopup();
    else this.openPopup();
  }

  private openPopup(): void {
    if (this.popup) return;
    this.activeIndex = Math.max(0, this.options.findIndex((o) => o.value === this._value));

    const popup = document.createElement("div");
    popup.className = "dropdown__popup";
    popup.setAttribute("role", "listbox");

    this.options.forEach((opt, i) => {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "dropdown__option";
      item.setAttribute("role", "option");
      item.textContent = opt.label;
      if (opt.value === this._value) item.classList.add("dropdown__option--selected");
      if (i === this.activeIndex) item.classList.add("dropdown__option--active");
      item.addEventListener("click", (ev) => {
        ev.stopPropagation();
        this.choose(i);
      });
      item.addEventListener("mousemove", () => this.setActive(i));
      popup.appendChild(item);
    });

    document.body.appendChild(popup);
    this.popup = popup;
    this.trigger.setAttribute("aria-expanded", "true");
    this.position();

    setTimeout(() => {
      document.addEventListener("mousedown", this.onOutside, true);
      document.addEventListener("keydown", this.onPopupKey, true);
    }, 0);
  }

  private position(): void {
    if (!this.popup) return;
    const r = this.trigger.getBoundingClientRect();
    this.popup.style.minWidth = `${r.width}px`;
    this.popup.style.left = `${r.left}px`;
    // Measure, then decide whether to drop down or flip up.
    const ph = this.popup.getBoundingClientRect().height;
    const below = window.innerHeight - r.bottom;
    if (below < ph + 8 && r.top > ph + 8) {
      this.popup.style.top = `${Math.max(8, r.top - ph - 4)}px`;
    } else {
      this.popup.style.top = `${r.bottom + 4}px`;
    }
  }

  private closePopup(): void {
    if (!this.popup) return;
    this.popup.remove();
    this.popup = null;
    this.trigger.setAttribute("aria-expanded", "false");
    document.removeEventListener("mousedown", this.onOutside, true);
    document.removeEventListener("keydown", this.onPopupKey, true);
  }

  private choose(i: number): void {
    const opt = this.options[i];
    if (opt) {
      this._value = opt.value;
      this.renderLabel();
    }
    this.closePopup();
    this.trigger.focus();
  }

  private setActive(i: number): void {
    this.activeIndex = i;
    if (!this.popup) return;
    const items = this.popup.querySelectorAll<HTMLElement>(".dropdown__option");
    items.forEach((el, idx) => el.classList.toggle("dropdown__option--active", idx === i));
  }

  private onTriggerKey(ev: KeyboardEvent): void {
    if (this.popup) return; // popup handler owns keys while open
    if (ev.key === "Enter" || ev.key === " " || ev.key === "ArrowDown" || ev.key === "ArrowUp") {
      ev.preventDefault();
      this.openPopup();
    }
  }

  private onOutside = (ev: MouseEvent): void => {
    if (this.popup && !this.popup.contains(ev.target as Node) && !this.element.contains(ev.target as Node)) {
      this.closePopup();
    }
  };

  private onPopupKey = (ev: KeyboardEvent): void => {
    if (!this.popup) return;
    // Swallow everything while open so the host dialog doesn't also react
    // (e.g. a single Esc should close only the popup, not the dialog).
    if (ev.key === "Escape") {
      ev.preventDefault(); ev.stopPropagation();
      this.closePopup();
      this.trigger.focus();
    } else if (ev.key === "ArrowDown") {
      ev.preventDefault(); ev.stopPropagation();
      this.setActive(Math.min(this.options.length - 1, this.activeIndex + 1));
      this.scrollActiveIntoView();
    } else if (ev.key === "ArrowUp") {
      ev.preventDefault(); ev.stopPropagation();
      this.setActive(Math.max(0, this.activeIndex - 1));
      this.scrollActiveIntoView();
    } else if (ev.key === "Enter" || ev.key === " ") {
      ev.preventDefault(); ev.stopPropagation();
      this.choose(this.activeIndex);
    } else if (ev.key === "Tab") {
      // Let focus move on, but close first.
      this.closePopup();
    }
  };

  private scrollActiveIntoView(): void {
    const el = this.popup?.querySelectorAll<HTMLElement>(".dropdown__option")[this.activeIndex];
    el?.scrollIntoView({ block: "nearest" });
  }
}
