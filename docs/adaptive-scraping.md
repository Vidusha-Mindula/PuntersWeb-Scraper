# Adaptive scraping: surviving website changes

## Why this exists

The ask: make the Punters scraper more resilient to Punters changing its HTML — renamed
elements, renamed classes, changed attributes, restructured markup, elements moved around the
page — instead of the scraper breaking outright the moment a selector no longer matches.

The investigation started from [Scrapling](https://github.com/D4Vinci/Scrapling), a Python
scraping library whose whole pitch is exactly this: an "adaptive"/AutoMatch feature that keeps
finding an element after a page changes, by remembering what the element structurally *looked
like* the last time it was found and searching for the closest match if the original
selector comes up empty.

Two things became clear once both codebases were actually read (not just their READMEs):

1. **Scrapling itself is a Python library with no .NET/Playwright equivalent** — it parses static
   HTML with `lxml`. It can't be dropped into this C# Playwright-based scraper as a dependency.
   What's portable is the *technique*, not the package.
2. **This scraper barely uses CSS/XPath selectors in the first place.** Almost all of its data
   extraction (meeting names, race times, runner odds, form, stats — everything in
   `PuntersScraperService.ReadEmbeddedMeetingsAsync` / `ExtractRaceDetailAsync`) comes from
   reading Punters' own Nuxt 3 / Apollo GraphQL cache directly out of the page's JS state via
   `page.EvaluateAsync`, not from querying HTML elements. There is no HTML-parsing NuGet package
   anywhere in the solution (no AngleSharp, no HtmlAgilityPack) — only Playwright.

So "port Scrapling's AutoMatch" doesn't mean rewriting the scraper into an HTML-selector-driven
one. It means: (a) apply the *same resilience technique* to the small number of places that
genuinely do locate an HTML element, and (b) recognize that this codebase's real analog of
"a selector breaking" is its GraphQL cache **key lookups** breaking, and harden those the same
way.

## How Scrapling's adaptive matching actually works

(For anyone who wants to go read it themselves: `scrapling/parser.py`'s `Selector.relocate`,
`Selector.css`/`.xpath`, and `scrapling/core/utils/_utils.py`'s `_StorageTools.element_to_dict`,
in the [Scrapling repo](https://github.com/D4Vinci/Scrapling).)

1. **Fingerprint, not selector.** The first time an element is found, Scrapling doesn't remember
   the CSS/XPath string that found it — CSS/XPath strings are exactly what breaks. It remembers a
   *structural fingerprint*: tag name, own attributes, own direct text, the chain of tag names
   from the document root down to the element, and the parent's tag/attributes/text plus sibling
   and child tag names. This is saved to a small SQLite database, keyed by (site domain,
   caller-chosen identifier).
2. **Relocate by similarity, not by re-querying.** If the original selector later finds nothing,
   Scrapling scans *every* element currently on the page, builds the same fingerprint for each,
   and scores each one against the remembered fingerprint — a weighted average of
   `difflib.SequenceMatcher` ratios across each field (tag match, text similarity, attribute
   similarity, path similarity, parent/sibling similarity). Whatever scores highest above a
   threshold (default 40%) is returned as the relocated element.
3. **Cold start is required.** There is nothing to relocate *from* until an element has been
   successfully found and its fingerprint saved at least once. This is not a workaround-able
   limitation — it's inherent to "remember what worked, adapt when it stops working."

## What was built here

`src/PuntersScraper.Core/Scraping/Adaptive/`:

- **`AdaptiveFingerprintStore.cs`** — the persistence layer. A single JSON file under
  `%LOCALAPPDATA%\PuntersScraper\adaptive_fingerprints.json`, keyed by `"{domain}|{identifier}"`.
  This mirrors `PuntersScraper.App`'s existing `AppSettings` on-disk convention (a small JSON blob
  in the same folder) rather than adding a database dependency for what's a handful of key/value
  entries — Scrapling's choice of SQLite made sense for a general-purpose library; it would be
  overkill here.
- **`AdaptiveLocator.cs`** — the fingerprint capture, scoring, and relocation logic, run as
  in-page JavaScript via Playwright's `EvaluateAsync` (there's no server-side parsed-DOM tree to
  walk in this codebase, only the live page, so the scan has to happen inside the browser).

### The API

```csharp
var tabByRole = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = tabLabel, Exact = true });
var tab = await AdaptiveLocator.FindAsync(page, tabByRole, $"date-tab:{tabLabel}", progress);
if (tab is null)
{
    throw new PuntersScrapeException($"Could not find a '{tabLabel}' date tab ...");
}
await tab.First.ClickAsync(...);
```

`FindAsync` tries `primary` first — if that finds something, it snapshots and saves its
fingerprint (best-effort; a snapshot failure never breaks the current run) and returns it
unchanged, so on a normal day when nothing has changed this is a no-op wrapper around the
existing locator. Only when `primary` finds **zero** elements does it look up a previously-saved
fingerprint for that `(domain, identifier)` and, if one exists, scan the page and try to relocate
it. If it relocates something above the similarity threshold, the matched element is tagged with
a temporary `data-adaptive-match` attribute and handed back as an ordinary `ILocator` — the caller
doesn't need to know or care that a fallback path was taken (beyond the `progress` message logged
for visibility).

This is wired into the two real DOM-locator call sites in `PuntersScraperService.cs`:

- The date-tab lookup (`GetByRole(AriaRole.Tab, ...)`, used when scraping a day other than today).
- The "Show All Form" button lookup (`GetByRole(AriaRole.Button, ...)`, used to trigger
  full-form loading).

(The third DOM lookup in that file, `page.Locator("#w")` for the Cloudflare Turnstile widget, was
deliberately left alone — that ID is assigned by Cloudflare's own script, not by Punters, so
Punters changing its site has no bearing on it, and adaptively relocating into a *different*
Cloudflare-controlled element would be actively unsafe.)

### Beyond the DOM: the Apollo cache keys

The more realistic fragility point for this specific site is Punters' Nuxt Apollo cache. Its keys
are things like `meetings({"date":"2026-08-24"})`, `event(...)`, `selections(...)` — this
scraper reads them with `Object.keys(rootQuery).find(k => k.startsWith('meetings('))`, which
breaks exactly like a CSS selector breaks if Punters ever renames or reshapes the underlying
GraphQL operation.

The same "don't just fail, look for the closest match" idea was applied there too:
`findKeyLike(keys, prefix)` (inlined in `ReadEmbeddedMeetingsAsync` and `ExtractRaceDetailAsync`'s
scripts) tries the exact prefix match first — the normal case — and only falls back to a
bigram-similarity match against the other keys' prefixes if nothing starts with the expected
string. This needs no fingerprint/cold-start step, because the "identity" here (the query name
itself, `meetings`/`event`/etc.) doesn't depend on a prior run the way an arbitrary DOM element's
structural context does.

## Deviations from Scrapling

Two changes were made after testing surfaced real problems with a literal port of Scrapling's
scoring, not stylistic preferences:

- **Weighted, not flat-averaged, similarity score.** Scrapling averages every fingerprint field
  (tag, text, attributes, path, parent tag, parent attributes, siblings, ...) with equal weight.
  Under an equal-weighted average, testing an "element moved to a different part of the page"
  scenario found that a *neighboring* element which stayed in its original structural position
  could outscore the *actual* element that moved but kept its own tag/class/attributes/text
  completely intact — because 3+ purely positional fields (path, parent tag, parent attributes)
  all favored the neighbor, and that outweighed the moved element's perfect identity match. This
  codebase weights "what the element itself is" (tag, text, attributes) at 3x versus "where it
  currently sits" (path, parent, siblings, children) — because surviving relocation is one of the
  scenarios this exists for, and an element that moved will *always* score badly on position by
  definition. See `AdaptiveLocatorTests.Relocates_when_the_element_moves_to_a_different_part_of_the_page`
  for the exact case that forced this change.
- **Per-key attribute comparison, not join-then-compare.** Scrapling's attribute-dict scoring
  joins all of an element's attribute *values* into one sequence and compares that sequence as a
  whole. That let one attribute that happened to match (e.g. a shared `class="tab-item"`) mask a
  completely different value in another attribute (e.g. `data-qa="tab-today"` vs
  `data-qa="tab-tomorrow"`) — the shared substring inflated the combined similarity score. This
  port instead scores each of the original element's attribute keys individually against the
  candidate and averages those, so a single meaningfully different attribute value can't be
  diluted by other attributes simply agreeing.
- **Approximate string similarity, not `difflib.SequenceMatcher`.** There's no equivalent of
  Python's `difflib` in a browser without pulling in a library. Both the DOM relocator and the
  Apollo-cache key matcher use a character-bigram Dice coefficient instead — same shape (1.0 for
  identical strings, 0.0 for unrelated ones, smooth in between), not a byte-for-byte match of
  Scrapling's numbers.

## Limitations (several inherited directly from Scrapling's own documented ones)

- **Cold start.** Nothing can be relocated until the primary locator has succeeded at least once
  and its fingerprint has been saved. The very first run against a brand-new identifier, or the
  first run after Punters changes something *before* a successful scrape ever captured a
  fingerprint for it, gets no adaptive help — the caller's existing behavior (throw / skip /
  degrade) is all that's left, unchanged from before this work.
- **Persisted per (domain, identifier), and only ever the latest snapshot.** Saving overwrites;
  there's no history. If Punters changes an element twice in a row with no successful scrape in
  between, only the fingerprint from the more recent successful run is available to relocate
  from.
- **A big enough combined change still won't relocate.** If an element's tag, attributes, text,
  *and* position all change substantially in the same release, its similarity score can
  legitimately fall below the 40% threshold and relocation will fail — correctly, since at that
  point "is this really the same element" stops being answerable from structure alone. This is
  inherent to a similarity-threshold approach, not a bug to fix.
- **Ambiguity isn't specially resolved.** If two elements on the page are structurally
  near-identical to the fingerprint, whichever is enumerated first by `querySelectorAll('*')` at
  the top score wins. For the two call sites this is wired into (a uniquely-labelled date tab, a
  uniquely-labelled button), this hasn't been an issue in testing, but it's not guarded against
  generically.
- **It only helps the two DOM lookups it's wired into, plus the four Apollo-cache key lookups.**
  It does not make the rest of the Apollo-cache-reading JS resilient to Punters changing the
  *shape* of the data it returns (e.g. renaming a field like `racePrizeMoneyValue` inside an
  already-found object) — that's a different, harder problem (schema drift inside already-located
  data) that a structural-fingerprint approach doesn't address at all, and was out of scope here.
- **Performance cost is paid only on the already-broken path.** Relocation scans every element on
  the page and fingerprints each one, which is O(n) with a non-trivial per-node cost — but it only
  ever runs when the primary locator has already found zero elements, so a normal, unbroken run
  never pays for it.

## Tests

`tests/PuntersScraper.Core.Tests/AdaptiveLocatorTests.cs` drives a real headless Chromium page
through the five change categories called out in the original ask, each as a before/after pair
where the "after" page is deliberately constructed so the *original* selector string matches
nothing, and asserts `AdaptiveLocator` still finds the right element by checking its text content:

| Test | Change simulated |
|---|---|
| `Relocates_when_the_element_tag_name_changes` | `<button>` → `<div>`, everything else unchanged |
| `Relocates_when_the_css_class_is_renamed` | `tab-item` → `tab-item-v2` |
| `Relocates_when_the_locating_attribute_is_renamed` | `data-qa` → `data-cy` |
| `Relocates_when_an_extra_wrapper_changes_the_dom_structure` | a `<span>` wrapper inserted between the element and its parent, breaking a direct-child selector |
| `Relocates_when_the_element_moves_to_a_different_part_of_the_page` | the element (unchanged) relocated into an entirely different container elsewhere on the page |
| `Returns_null_on_a_cold_start_with_no_prior_fingerprint` | documents the cold-start limitation above as a contract, not a bug |

Run with:

```bash
dotnet test tests/PuntersScraper.Core.Tests/PuntersScraper.Core.Tests.csproj
```

(Requires Playwright's Chromium browser to already be installed locally —
`playwright install chromium` — same requirement the main app already has.)
