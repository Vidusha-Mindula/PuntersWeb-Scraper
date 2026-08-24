using Microsoft.Playwright;

namespace PuntersScraper.Core.Scraping.Adaptive;

/// <summary>
/// Fallback element finder for the handful of spots in <see cref="PuntersScraperService"/> that
/// locate an element by CSS/ARIA role rather than by reading Punters' own embedded data cache
/// (see the class doc comment on <see cref="PuntersScraperService"/> for why almost everything
/// else doesn't need this). Ported from the Python library Scrapling's "adaptive"/AutoMatch
/// feature -- see docs/adaptive-scraping.md for the full design writeup and how this maps back
/// to Scrapling's own implementation (scrapling/parser.py's Selector.relocate, scrapling/core/
/// utils/_utils.py's element_to_dict).
///
/// How it works: the first time <paramref name="primary"/> (an ordinary role/CSS locator) finds
/// the element, its structural "fingerprint" -- tag name, attributes, direct text, position among
/// parent/siblings/children -- is snapshotted and remembered on disk, keyed by (site domain,
/// caller-supplied identifier). If a LATER run's primary locator finds nothing -- e.g. Punters
/// renamed the element's accessible name or restructured the markup around it -- every element on
/// the page is scored against the remembered fingerprint and the best match above a similarity
/// threshold is returned instead of failing outright.
///
/// This only ever has something to fall back on if a fingerprint was captured on a PRIOR
/// successful run against the SAME identifier+domain -- there is nothing to adapt from on a
/// completely cold cache (see docs/adaptive-scraping.md, "Cold start").
/// </summary>
public static class AdaptiveLocator
{
    public const int DefaultThresholdPercent = 40;

    /// <summary>
    /// Tries <paramref name="primary"/> first; if that finds nothing, tries to relocate the
    /// element from a previously-saved fingerprint. Returns null if neither succeeds (the caller
    /// keeps whatever it already does in that case -- throw, skip, degrade).
    /// </summary>
    public static async Task<ILocator?> FindAsync(
        IPage page,
        ILocator primary,
        string identifier,
        IProgress<string>? progress = null,
        int thresholdPercent = DefaultThresholdPercent,
        AdaptiveFingerprintStore? store = null)
    {
        store ??= AdaptiveFingerprintStore.Shared;
        var domain = new Uri(page.Url).Host;

        if (await primary.CountAsync() > 0)
        {
            await SaveFingerprintBestEffortAsync(primary.First, domain, identifier, store);
            return primary;
        }

        var fingerprintJson = store.Retrieve(domain, identifier);
        if (fingerprintJson is null) return null; // cold start: nothing to adapt from yet

        var matchMarker = await page.EvaluateAsync<string?>(
            RelocateScript, new { fingerprint = fingerprintJson, threshold = thresholdPercent });
        if (matchMarker is null) return null;

        progress?.Report(
            $"Adaptive relocation matched '{identifier}' by structural similarity -- Punters " +
            "may have changed this element's markup since it was last seen. Using the match.");
        return page.Locator($"[data-adaptive-match=\"{matchMarker}\"]");
    }

    private static async Task SaveFingerprintBestEffortAsync(
        ILocator element, string domain, string identifier, AdaptiveFingerprintStore store)
    {
        try
        {
            var json = await element.EvaluateAsync<string>(FingerprintScript);
            store.Save(domain, identifier, json);
        }
        catch
        {
            // Best-effort only: failing to snapshot never breaks the CURRENT run (the primary
            // locator already found the element) -- it just means no fallback will be available
            // if a FUTURE run's primary locator fails.
        }
    }

    // Shared building blocks (fingerprint shape + similarity scoring) duplicated between the two
    // scripts below because each runs as its own self-contained Playwright EvaluateAsync call --
    // matching this file's existing pattern elsewhere (see ReadEmbeddedMeetingsAsync et al.,
    // which duplicate their own JS helpers the same way rather than sharing a JS module).
    private const string JsFingerprintHelpers = """
        function jsAttrs(e) {
            const out = {};
            for (const a of e.attributes) { if (a.value && a.value.trim()) out[a.name] = a.value; }
            return out;
        }
        function jsDirectText(e) {
            let out = '';
            for (const node of e.childNodes) { if (node.nodeType === 3) out += node.textContent; }
            return out.trim();
        }
        function jsPath(e) {
            const p = [];
            let cur = e;
            while (cur) { p.unshift(cur.tagName.toLowerCase()); cur = cur.parentElement; }
            return p;
        }
        function jsFingerprint(e) {
            const fp = { tag: e.tagName.toLowerCase(), attributes: jsAttrs(e), text: jsDirectText(e), path: jsPath(e) };
            const parent = e.parentElement;
            if (parent) {
                fp.parentTag = parent.tagName.toLowerCase();
                fp.parentAttributes = jsAttrs(parent);
                fp.siblings = Array.from(parent.children).filter(c => c !== e).map(c => c.tagName.toLowerCase());
            }
            fp.children = Array.from(e.children).map(c => c.tagName.toLowerCase());
            return fp;
        }
        """;

    private const string FingerprintScript = $$"""
        el => {
            {{JsFingerprintHelpers}}
            return JSON.stringify(jsFingerprint(el));
        }
        """;

    // Similarity scoring: a weighted average of per-field ratios, mirroring Scrapling's
    // Selector.__calculate_similarity_score (parser.py). Where Scrapling uses Python's
    // difflib.SequenceMatcher, this uses a character-bigram Dice coefficient instead -- an
    // approximation, not a byte-for-byte port (no equivalent exists in a browser without pulling
    // in a library), but the same shape: 1.0 for identical strings, 0.0 for totally unrelated
    // ones, smoothly in between for partial overlap. See docs/adaptive-scraping.md for the
    // rationale and a worked comparison against Scrapling's own numbers.
    private const string JsScoringHelpers = """
        function jsDiceRatio(a, b) {
            a = String(a ?? ''); b = String(b ?? '');
            if (a === b) return 1;
            if (!a.length || !b.length) return 0;
            const bigrams = s => { const out = []; for (let i = 0; i < s.length - 1; i++) out.push(s.slice(i, i + 2)); return out; };
            const ba = bigrams(a), bb = bigrams(b).slice();
            if (!ba.length || !bb.length) return 0;
            let matches = 0;
            for (const g of ba) {
                const idx = bb.indexOf(g);
                if (idx !== -1) { matches++; bb.splice(idx, 1); }
            }
            return (2 * matches) / (ba.length + bb.length);
        }
        function jsSeqRatio(arrA, arrB) {
            return jsDiceRatio((arrA || []).join(''), (arrB || []).join(''));
        }
        function jsDictDiff(d1, d2) {
            // Per-key comparison rather than Scrapling's "join all values, compare as one
            // string" approach (see docs/adaptive-scraping.md, "Deviations from Scrapling") --
            // joining first lets an unrelated matching attribute value mask a mismatched one
            // (e.g. a shared class="tab-item" hides a completely different data-qa value), which
            // made a decoy sibling outscore the real, moved element in testing.
            const keys1 = Object.keys(d1 || {});
            if (!keys1.length) return Object.keys(d2 || {}).length ? 0 : 1;
            let total = 0;
            for (const k of keys1) {
                total += Object.prototype.hasOwnProperty.call(d2 || {}, k) ? jsDiceRatio(d1[k], (d2 || {})[k]) : 0;
            }
            return total / keys1.length;
        }
        function jsSimilarityScore(orig, cand) {
            // Two weight tiers, not Scrapling's flat equal-weight average (see
            // docs/adaptive-scraping.md, "Deviations from Scrapling"): what the element ITSELF is
            // (tag/text/attributes) counts 3x compared to where it currently SITS
            // (path/parent/siblings/children). An element that was deliberately relocated -- one
            // of the resilience scenarios this exists for -- will always score badly on
            // positional context by definition; identity has to be able to outweigh that, or
            // relocation degenerates into "find whatever stayed where the old element used to
            // be". A 2x split still let an unmoved neighbor with a similar tag/class beat a truly
            // relocated exact match in testing (three whole context checks -- path, parentTag,
            // parentAttributes -- flipping to the neighbor's favor outweighed a 2x identity
            // edge); 3x carries a real, tested margin, see AdaptiveLocatorTests's
            // "moves_to_a_different_part_of_the_page" case.
            let total = 0, weight = 0;
            function add(w, v) { total += w * v; weight += w; }

            add(3, orig.tag === cand.tag ? 1 : 0);
            if (orig.text) add(3, jsDiceRatio(orig.text, cand.text));
            add(3, jsDictDiff(orig.attributes, cand.attributes));
            for (const key of ['class', 'id', 'href', 'src']) {
                if (orig.attributes && orig.attributes[key] !== undefined) {
                    add(3, jsDiceRatio(orig.attributes[key], (cand.attributes || {})[key]));
                }
            }

            add(1, jsSeqRatio(orig.path, cand.path));
            if (orig.parentTag) {
                add(1, orig.parentTag === cand.parentTag ? 1 : 0);
                add(1, jsDictDiff(orig.parentAttributes, cand.parentAttributes));
            }
            if (orig.siblings && orig.siblings.length) add(1, jsSeqRatio(orig.siblings, cand.siblings));
            if (orig.children && orig.children.length) add(1, jsSeqRatio(orig.children, cand.children));

            return weight ? (total / weight) * 100 : 0;
        }
        """;

    // Exhaustive scan of every element on the page, same tradeoff Scrapling makes (relocate()
    // walks XPath(".//*")): only ever runs when the primary locator already found nothing, so
    // the O(n) cost is paid on the already-broken path, not the normal one.
    private const string RelocateScript = $$"""
        ({ fingerprint, threshold }) => {
            const original = JSON.parse(fingerprint);
            {{JsFingerprintHelpers}}
            {{JsScoringHelpers}}

            let best = null, bestScore = -1;
            for (const el of document.querySelectorAll('*')) {
                const s = jsSimilarityScore(original, jsFingerprint(el));
                if (s > bestScore) { bestScore = s; best = el; }
            }

            if (!best || bestScore < threshold) return null;
            const marker = 'adaptive-' + Math.random().toString(36).slice(2);
            best.setAttribute('data-adaptive-match', marker);
            return marker;
        }
        """;
}
