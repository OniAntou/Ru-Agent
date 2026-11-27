# Documentation site

The site is built from the Markdown that already lives in the repository (the root README,
`docs/`, and a few package READMEs). `build.py` stages a copy that keeps the repository layout, so
relative links between documents keep working, and rewrites any link that leaves that set
(source files, for example) into the matching page on GitHub.

```powershell
python -m venv .venv-docs
.venv-docs\Scripts\python.exe -m pip install -r docs-site/requirements.txt
.venv-docs\Scripts\python.exe docs-site/build.py --out site
```

- The menu is generated: the sections are listed in `build.py`, and the Decisions section reads
  `docs/adr` so a new ADR needs no edit.
- `docs/guide/` holds the task-oriented user guides (install, first task, permissions, terminal UI,
  teaching Ru, subagents, troubleshooting). They are written for users and must stay short and true to the
  behaviour documented in `docs/operations/` and the CLI README; the reference chapters keep the detail.
- GitHub alert blockquotes (`> [!WARNING]`) are converted to the site's hazard plates while staging
  (`NOTE`/`TIP` become a note plate, `IMPORTANT` a caution plate, `WARNING` a warning plate and `CAUTION` a
  danger plate), so the same Markdown reads correctly on GitHub and on the site.
- `docs/plans` and `docs/superpowers` are never published (open drafts).
- MkDocs and its theme are pinned in `docs-site/requirements.txt`. They belong to this site only
  and are not part of the project's Python tooling set.
- `.github/workflows/docs.yml` deploys to GitHub Pages on a push to `dev` that touches a
  published document. Pages must be enabled once under Settings > Pages (Source: GitHub Actions).
- `--work` must point to a dedicated scratch directory. The builder refuses the repository root,
  the source `docs/` tree and an existing staged `docs/` directory it does not own; it records
  ownership in `.ru-docs-work`. The default `docs-site/.build` remains supported.

## Look and feel

The site is "The Service Manual": numbered chapters (the rail, each page's plate line and the
contents list share the same `2-4` numbers), procedures with tick-off steps, a hazard hierarchy for
NOTE / CAUTION / WARNING plates, Ru green as the only brand ink, dark by default with a daylight mode.

| File | Role |
| --- | --- |
| `PRODUCT.md`, `DESIGN.md` | Product truth and the visual system (tokens, rules). Read them before changing the look. |
| `theme/assets/manual.css` | The whole stylesheet; the direction contract is at its top. |
| `theme/assets/manual.js` | Tick-off steps (state kept in localStorage). |
| `theme/overrides/main.html` | The per-page plate line with the chapter and section number. |
| `theme/overrides/home.html`, `home.md` | The cover ("The Desk"): its own header and footer, with `home.md` holding the landing markup. The chapter list lives in the site navigation, not on the cover. |
| `theme/assets/landing.css`, `landing.js` | The cover's stylesheet and behaviour (replay beats, reveals, copy button); the manual pages never load them. |
| `theme/assets/ru-app.css`, `ru-app.js` | The HTML rebuild of the desktop window that the cover shows and plays, measured against real captures of the app. |
| `theme/assets/shots/` | Real captures of the desktop app: the no-script fallback and the social preview image. |

The cover repeats the install command, the permission modes and facts from the README, so keep `home.md`
in step when those change. When the desktop app's look changes, re-measure `ru-app.css` against a fresh
capture. Fonts (Barlow Condensed, Atkinson Hyperlegible, Inter, JetBrains Mono) load from Google Fonts
at view time.
