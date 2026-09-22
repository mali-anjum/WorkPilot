// Design system interop — spec 0003. Cookie backed theme (no localStorage: Auto render
// mode's server-prerendered first paint has no JS interop available, so the theme must be
// readable server side from the request before any HTML is emitted).

export function setTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    document.cookie = `workpilot-theme=${theme}; path=/; max-age=31536000; SameSite=Lax`;
}

// Document level listener so Cmd/Ctrl+K opens the command palette from anywhere in the
// shell, not just when a specific element is focused (a Blazor @onkeydown can't do that).
export function registerCommandPaletteShortcut(dotNetRef) {
    function handler(e) {
        const isMac = navigator.platform.toUpperCase().includes('MAC');
        const modifierHeld = isMac ? e.metaKey : e.ctrlKey;
        if (modifierHeld && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OnShortcut');
        }
    }
    document.addEventListener('keydown', handler);
    return {
        dispose: () => document.removeEventListener('keydown', handler),
    };
}

// Minimal focus trap for Modal/CommandPalette: moves focus into the dialog on open,
// cycles Tab/Shift+Tab within it, and restores focus to the trigger on close.
export function trapFocus(dialogEl) {
    const previouslyFocused = document.activeElement;
    const focusable = () => Array.from(
        dialogEl.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])')
    ).filter(el => !el.disabled && el.offsetParent !== null);

    const first = focusable()[0];
    (first ?? dialogEl).focus();

    function handler(e) {
        if (e.key !== 'Tab') return;
        const items = focusable();
        if (items.length === 0) return;
        const firstEl = items[0];
        const lastEl = items[items.length - 1];
        if (e.shiftKey && document.activeElement === firstEl) {
            e.preventDefault();
            lastEl.focus();
        } else if (!e.shiftKey && document.activeElement === lastEl) {
            e.preventDefault();
            firstEl.focus();
        }
    }
    dialogEl.addEventListener('keydown', handler);

    return {
        dispose: () => {
            dialogEl.removeEventListener('keydown', handler);
            if (previouslyFocused instanceof HTMLElement) {
                previouslyFocused.focus();
            }
        },
    };
}
