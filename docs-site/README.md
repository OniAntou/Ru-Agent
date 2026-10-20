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
- `docs/plans` and `docs/superpowers` are never published (open drafts).
- MkDocs and its theme are pinned in `docs-site/requirements.txt`. They belong to this site only
  and are not part of the project's Python tooling set.
- `.github/workflows/docs.yml` deploys to GitHub Pages on a push to `dev` that touches a
  published document. Pages must be enabled once under Settings > Pages (Source: GitHub Actions).
