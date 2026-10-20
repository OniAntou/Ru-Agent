---
name: Ru Agent documentation
description: An operator's service manual for a machine you operate under permission modes.
colors:
  ru-green: "#00e676"
  ru-green-ink: "#04140b"
  header-night: "#070b09"
  graphite-ground: "#0b100e"
  graphite-surface: "#111815"
  graphite-surface-raised: "#17211d"
  graphite-rule: "#26332d"
  graphite-rule-strong: "#3d4f46"
  bone-ink: "#e6ece7"
  bone-ink-2: "#a9b6af"
  bone-ink-3: "#86958d"
  link-mint: "#4ff0a1"
  daylight-ground: "#eef2ef"
  daylight-ink: "#0f1613"
  daylight-link: "#006b36"
  hazard-note: "#7cc4ff"
  hazard-caution: "#ffb224"
  hazard-warning: "#ff8a3d"
  hazard-danger: "#ff6b5e"
typography:
  plate:
    fontFamily: "Barlow Condensed, Arial Narrow, sans-serif"
    fontWeight: 700
    letterSpacing: "0.14em"
  display:
    fontFamily: "Barlow Condensed, Arial Narrow, sans-serif"
    fontSize: "clamp(2.2rem, 4.4vw, 3.4rem)"
    fontWeight: 700
    lineHeight: 1.02
  body:
    fontFamily: "Atkinson Hyperlegible, Segoe UI, sans-serif"
    fontSize: "1.0625rem"
    lineHeight: 1.62
  code:
    fontFamily: "JetBrains Mono, Cascadia Mono, Consolas, monospace"
    fontSize: "0.86em"
rounded:
  none: "0"
spacing:
  plate-pad: "1rem"
  measure: "72ch"
components:
  button-primary:
    backgroundColor: "{colors.ru-green}"
    textColor: "{colors.ru-green-ink}"
    rounded: "{rounded.none}"
    padding: "0.62rem 1.15rem"
  hazard-plate-caution:
    backgroundColor: "{colors.hazard-caution}"
    textColor: "{colors.graphite-ground}"
    rounded: "{rounded.none}"
    padding: "0.28rem 1rem"
---

# Design System: Ru Agent documentation

## Overview

**Creative North Star: "The Service Manual"**

Ru is a machine you operate under permission modes, and a service manual is the format made for that:
numbered chapters, procedures with visible steps, specifications in tables, and a hazard hierarchy that
says what can hurt you before you do it. The site refuses two defaults: the near-black hacker page with
one neon accent, and the friendly SaaS documentation page of rounded cards.

**Key Characteristics:**
- Chapter-section numbering carries wayfinding (2-4 is chapter 2, section 4) in the rail, in each page's
  plate line and in the generated contents list.
- Hazard plates (NOTE, CAUTION, WARNING, DANGER) are the only coloured devices besides Ru green.
- Ru green means "engaged / OK / primary action": the install button, the open page's number chip and a
  ticked procedure step. It is the only brand ink.
- Dark by default for the night desk with a terminal beside the browser; a cool daylight manual otherwise.
- Hairline rules and flat plates; no shadows, gradients or glass.

## Colors

Strategy: restrained. Graphite-green neutrals carry the page; one brand green; amber and red appear
only as hazard grades. Every text pair was computed against WCAG AA (lowest body pair 5.1:1).

### Primary
- **Ru Green** (#00e676): the logo's own ink. Fills the primary button, the open page's number chip and
  ticked steps; dark ink (#04140b) sits on it.

### Neutral
- **Graphite Ground** (#0b100e) with **Surface** (#111815) and **Raised** (#17211d): page, plate and
  inline-code grounds. Tinted toward green so the brand ink sits in the same family.
- **Bone Ink** (#e6ece7), **2** (#a9b6af), **3** (#86958d): text, secondary, and labels.
- **Rule** (#26332d) and **Rule Strong** (#3d4f46): hairlines and plate frames.
- **Daylight** (#eef2ef ground, #0f1613 ink, #006b36 link): the light manual; the header bar stays dark
  in both modes so the wordmark always reads.

### Hazard grades
- **Note** (#7cc4ff), **Caution** (#ffb224), **Warning** (#ff8a3d), **Danger** (#ff6b5e): a filled label
  strip over a framed body. Light mode uses deeper inks with white labels.

### Named Rules
**The One Ink Rule.** Green means engaged or primary and nothing decorative; hazard colours appear only
on hazard plates.

## Typography

**Plate and display font:** Barlow Condensed (Arial Narrow fallback)
**Body font:** Atkinson Hyperlegible (Segoe UI fallback)
**Code font:** JetBrains Mono (Cascadia Mono, Consolas)

**Character:** a signage-grade condensed grotesque for the manual's labels and headings against a face
designed for legibility, so long reference pages stay readable. Monospace is for code only.

### Hierarchy
- **Display** (700, clamp(2.2rem, 4.4vw, 3.4rem), 1.02): page titles.
- **Headline** (700, 1.55rem, uppercase, 0.06em): section heads over a hairline.
- **Title** (600, 1.25rem, uppercase): subsections.
- **Body** (400, 1.0625rem, 1.62): reading text at a 72ch measure.
- **Label** (700, 0.82 to 0.9rem, 0.14 to 0.18em, uppercase): plates, table heads, rail chapters.

### Named Rules
**The Real Content Rule.** A label plate exists only over content that needs the label: no decorative
eyebrows over ordinary sections.

## Layout

Three columns on wide screens: a 15.5rem chapter rail, a reading column at 72ch, and an on-page contents
margin. The cover is a two-column spread (identity, then the procedure plate) at up to 76rem; everything
stacks below 60em. Spacing is generous between sections and tight inside a plate.

## Elevation & Depth

Flat. Depth is a plate: a filled label strip over a framed body, and a hairline. There are no shadows.

## Shapes

Square corners and 1px rules. Procedure steps are 1.7rem square numeral boxes; hazard and procedure
plates are rectangles with a filled strip.

## Components

### Buttons
- **Shape:** square, 1px frame.
- **Primary:** Ru green fill, dark ink; hover inverts to bone.
- **Secondary:** transparent, bone frame; hover fills.

### Procedure steps
Numbered square boxes. Each step can be ticked (the box fills green and shows a check); the state is kept
per page in localStorage and everything reads correctly without script.

### Hazard plates
Admonitions and plain quoted paragraphs render as plates: label strip (NOTE, CAUTION, WARNING, DANGER) over
a framed body tinted from the grade.

### Navigation
The rail lists chapters as plates ("CH 2 HOW IT WORKS") and pages by section number (2-4); the open page's
number becomes a green chip. Each page opens with a plate line (chapter, title, number).

### Specification tables
A heavy top rule, hairline rows, condensed uppercase heads, first column bold.

## Do's and Don'ts

### Do:
- **Do** let every device carry real content: real presets, real limits, real commands.
- **Do** put the plain-language limit (no sandbox, unsigned Preview) where the visitor acts.
- **Do** keep the cover's installation command fully visible; wrap it rather than scroll it.

### Don't:
- **Don't** use a coloured side border on callouts or list items; the plate is the grammar.
- **Don't** set body copy in monospace.
- **Don't** add shadows, gradients or glass.
