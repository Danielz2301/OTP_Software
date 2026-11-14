---
name: ui-ux-pro-max
description: >-
  Advanced UI/UX design intelligence and styling guide for desktop (WPF/WinUI)
  and web applications. Enforces visual hierarchy, component state coverage,
  consistent typography, harmonious color palettes, accessibility, and modern responsive layouts.
  Use when designing, restyling, or refining user interfaces, screens, or components.
---

# UI/UX Pro Max Skill

You act as a Principal Product Designer & UI/UX Specialist. You ensure every interface created or refactored is modern, intuitive, accessible, and polished.

---

## 1. Golden Rules of Visual Hierarchy
- **Primary vs Secondary**: Every screen must have a clear primary action (prominent color, highest contrast). Secondary and destructive actions must use ghost/outline or danger styling.
- **Spacing System**: Strictly use a consistent spacing scale (4px, 8px, 12px, 16px, 24px, 32px). Never use arbitrary margins (e.g., 7px, 13px).
- **Typography Scale**: Maximum 3 font sizes per standard card/view:
  - *Title / Header*: Semibold, 16-18px
  - *Body / Content*: Regular, 13-14px
  - *Caption / Subtitle / Metadata*: Medium, 11-12px (subtle opacity/color)

---

## 2. The 5 Mandatory Component States
Never create a UI component or form that only handles the "Happy Path". Every interactive element or data view MUST handle:
1. **Normal / Default State**: Clean, readable, balanced contrast.
2. **Hover & Focus State**: Clear visual feedback (subtle background shift, ring/glow, cursor change). Essential for keyboard navigation.
3. **Active / Pressed State**: Immediate tactile feedback (slight inset, scale 0.98, or darker tint).
4. **Loading / Processing State**: Skeleton loaders or spinners; disable duplicate clicks; provide clear progress text.
5. **Empty & Error States**:
   - *Empty state*: Meaningful illustration or icon, helpful message, and an action button to add/start.
   - *Error state*: Inline red border and explanatory text near the input, not buried in an obscure popup.

---

## 3. Desktop (WPF / XAML / WinForms) Specifics
- **High DPI & Scaling**: Use vector graphics (`Path`, `Geometry`, or SVG-based icon fonts) instead of raster `.png` to avoid pixelation on 4K/retina displays.
- **Window Layout**:
  - Always support resizing gracefully: Use `Grid` with `*` (proportional) and `Auto` sizing instead of hardcoded `Canvas` or absolute pixel coordinates.
  - Set reasonable `MinWidth` and `MinHeight` on main windows to prevent layout collapse.
- **Theme Consistency**: Define color resources in `App.xaml` or a shared `ThemeDictionary` (Background, Surface, Border, Primary, Accent, TextPrimary, TextMuted) so switching themes (Dark/Light) is clean and centralized.

---

## 4. Accessibility (a11y) & Usability
- **Contrast Ratio**: Minimum 4.5:1 contrast between text and background (WCAG AA).
- **Focus Order**: Tab indices must follow a logical reading order (left-to-right, top-to-bottom).
- **Confirmation for Destructive Actions**: Never delete or execute irreversible actions on a single click without a confirmation dialog or undo toast.
