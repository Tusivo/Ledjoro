# Legal posture and clean-room policy

*This is a project policy, not legal advice. Have an IP lawyer review before public release or commercial use.*

## Why open-source and non-commercial does not by itself protect us
Copyright, trademark, patent and contract claims do not depend on charging money. The goal is to **not do the things that create claims**, and to be able to prove it.

## What this project is allowed to do
- Implement **functionality** (double-entry bookkeeping, invoices, reports). Ideas, methods and functions are not protected by copyright; many compatible products exist.
- Read and write **documented or open interchange formats**: IIF (documented text format), OFX/QFX/QBO bank files (open standard; “QBO” is plain OFX), qbXML (published SDK schema), QuickBooks Online API JSON shapes (published), CSV exports. Interoperability is generally defensible (e.g. *Google v. Oracle*, *Sega v. Accolade*).
- Use **generic UI patterns** (sidebar navigation, tables, forms, dashboards) that are common to all business software.
- Say truthfully that Ledjoro **“imports QuickBooks-format files”** (nominative use of a trademark to describe compatibility).

## What this project must not do
| Don’t | Why |
|---|---|
| Use “QuickBooks”, “QB”, “Intuit”, their logos, colours-as-branding, icons, illustrations, screenshots, sample data, help text or marketing copy in the product, docs, repo or domain | Trademark / copyright / trade-dress |
| Copy Intuit screens, layouts pixel-for-pixel, icon sets, wording of menus or messages | Copyright / trade dress |
| Read or write proprietary `.QBW` / `.QBB` company files, or decompile/inspect Intuit binaries | Undocumented format; licence-agreement (EULA) bans on reverse engineering; DMCA §1201 risk |
| Use Intuit’s APIs/SDKs in ways their developer terms forbid (e.g. building a competing hosted service on their API, scraping the UI) | Contract claims need no copyright infringement |
| Import tax IDs or other personal data we don’t need | Privacy / security |
| Claim affiliation, certification or endorsement | Trademark / false association |
| Copy code from Intuit samples/SDKs or from any project with an incompatible licence | Copyright / licence conflict |

## Clean-room practice
1. **Specs only:** implement from public format specifications and from our own synthetic test files. Do not paste Intuit text/samples into the repo.
2. **Research ≠ source:** `docs/research/quickbooks-advanced-reference.md` is *background research for planning* (feature names and general behaviour from public pages). It must **not** be copied into product UI text, docs or marketing. Screenshots reviewed during research are not stored in the repo.
3. **Original UI:** our own name, palette, copy, icons and layout. Where a screen resembles a common industry pattern, keep a short note in the PR explaining that the pattern is generic and the expression (colours, copy, icons, layout details) is our own.
4. **Record provenance:** each importer lists the public spec/behaviour it implements (see `docs/FORMATS.md`); contributors confirm they did not use Intuit confidential or EULA-restricted material.
5. **Third-party code:** the project is dependency-free; any new dependency needs licence review (AGPL-compatible) and a security review.
6. **Patents:** we cannot rule out third-party software patents. For commercial distribution, commission a freedom-to-operate review.
7. **Takedown plan:** if Intuit (or anyone) objects, respond promptly via counsel, and be ready to rename/restyle — which is why branding and UI are fully original and separable.

## Licence
AGPL-3.0-or-later. Contributors license their work under the same terms (consider a DCO sign-off).
