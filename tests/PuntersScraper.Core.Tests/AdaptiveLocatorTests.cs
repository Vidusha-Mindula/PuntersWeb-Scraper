using Microsoft.Playwright;
using PuntersScraper.Core.Scraping.Adaptive;
using Xunit;

namespace PuntersScraper.Core.Tests;

/// <summary>
/// One headless Chromium instance shared by every test in the "Playwright" collection below --
/// launching a browser per test would make this suite far slower for no benefit, since each test
/// gets its own <see cref="IPage"/> anyway.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
    }
}

[CollectionDefinition("Playwright")]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>;

/// <summary>
/// Exercises <see cref="AdaptiveLocator"/> against the exact category of realistic website
/// changes the resilience work was scoped around: renamed elements, renamed classes, changed
/// attributes, restructured DOM, and elements moved elsewhere on the page. Each test:
///   1. Renders a "before" page and seeds a fingerprint via a primary locator that matches it.
///   2. Renders an "after" page with one specific change applied -- deliberately chosen so the
///      SAME primary locator (same selector string) now matches nothing.
///   3. Re-runs AdaptiveLocator against the broken primary locator and asserts it still finds the
///      same logical element (verified by its text content, since that's the one thing held
///      constant across every "after" page below).
/// </summary>
[Collection("Playwright")]
public sealed class AdaptiveLocatorTests : IAsyncLifetime
{
    private readonly PlaywrightFixture _fixture;
    private IPage _page = null!;
    private string _storeFile = null!;

    public AdaptiveLocatorTests(PlaywrightFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _page = await _fixture.Browser.NewPageAsync();
        _storeFile = Path.Combine(Path.GetTempPath(), $"adaptive-test-{Guid.NewGuid():N}.json");
    }

    public async Task DisposeAsync()
    {
        await _page.CloseAsync();
        if (File.Exists(_storeFile)) File.Delete(_storeFile);
    }

    private AdaptiveFingerprintStore NewStore() => new(_storeFile);

    [Fact]
    public async Task Relocates_when_the_element_tag_name_changes()
    {
        var store = NewStore();
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><button class="tab-item" data-qa="tab-today" role="tab">Today</button></li>
              <li><button class="tab-item" data-qa="tab-tomorrow" role="tab">Tomorrow</button></li>
            </ul>
            """);

        var seedPrimary = _page.Locator("button.tab-item[data-qa='tab-tomorrow']");
        Assert.NotNull(await AdaptiveLocator.FindAsync(_page, seedPrimary, "date-tab", store: store));

        // Punters swaps the interactive <button> for a plain <div> carrying the same class/attrs/
        // text/role -- a real pattern seen when a site reworks tab markup for styling reasons.
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><div class="tab-item" data-qa="tab-today" role="tab">Today</div></li>
              <li><div class="tab-item" data-qa="tab-tomorrow" role="tab">Tomorrow</div></li>
            </ul>
            """);

        var brokenPrimary = _page.Locator("button.tab-item[data-qa='tab-tomorrow']");
        Assert.Equal(0, await brokenPrimary.CountAsync());

        var relocated = await AdaptiveLocator.FindAsync(_page, brokenPrimary, "date-tab", store: store);
        Assert.NotNull(relocated);
        Assert.Equal("Tomorrow", await relocated!.TextContentAsync());
    }

    [Fact]
    public async Task Relocates_when_the_css_class_is_renamed()
    {
        var store = NewStore();
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><button class="tab-item" data-qa="tab-today" role="tab">Today</button></li>
              <li><button class="tab-item" data-qa="tab-tomorrow" role="tab">Tomorrow</button></li>
            </ul>
            """);

        var seedPrimary = _page.Locator(".tab-item[data-qa='tab-tomorrow']");
        Assert.NotNull(await AdaptiveLocator.FindAsync(_page, seedPrimary, "date-tab", store: store));

        // A CSS refactor renames the class but everything else about the element -- tag, other
        // attributes, text, position -- stays put.
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><button class="tab-item-v2" data-qa="tab-today" role="tab">Today</button></li>
              <li><button class="tab-item-v2" data-qa="tab-tomorrow" role="tab">Tomorrow</button></li>
            </ul>
            """);

        var brokenPrimary = _page.Locator(".tab-item[data-qa='tab-tomorrow']");
        Assert.Equal(0, await brokenPrimary.CountAsync());

        var relocated = await AdaptiveLocator.FindAsync(_page, brokenPrimary, "date-tab", store: store);
        Assert.NotNull(relocated);
        Assert.Equal("Tomorrow", await relocated!.TextContentAsync());
    }

    [Fact]
    public async Task Relocates_when_the_locating_attribute_is_renamed()
    {
        var store = NewStore();
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><button class="tab-item" data-qa="tab-today" role="tab">Today</button></li>
              <li><button class="tab-item" data-qa="tab-tomorrow" role="tab">Tomorrow</button></li>
            </ul>
            """);

        var seedPrimary = _page.Locator("button[data-qa='tab-tomorrow']");
        Assert.NotNull(await AdaptiveLocator.FindAsync(_page, seedPrimary, "date-tab", store: store));

        // The test-hook attribute itself is renamed (data-qa -> data-cy), a common churn point
        // when a site switches test frameworks -- tag/class/text/role are untouched.
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><button class="tab-item" data-cy="tab-today" role="tab">Today</button></li>
              <li><button class="tab-item" data-cy="tab-tomorrow" role="tab">Tomorrow</button></li>
            </ul>
            """);

        var brokenPrimary = _page.Locator("button[data-qa='tab-tomorrow']");
        Assert.Equal(0, await brokenPrimary.CountAsync());

        var relocated = await AdaptiveLocator.FindAsync(_page, brokenPrimary, "date-tab", store: store);
        Assert.NotNull(relocated);
        Assert.Equal("Tomorrow", await relocated!.TextContentAsync());
    }

    [Fact]
    public async Task Relocates_when_an_extra_wrapper_changes_the_dom_structure()
    {
        var store = NewStore();
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><button class="tab-item" role="tab">Tomorrow</button></li>
            </ul>
            """);

        var seedPrimary = _page.Locator("ul.tabs > li > button.tab-item");
        Assert.NotNull(await AdaptiveLocator.FindAsync(_page, seedPrimary, "date-tab", store: store));

        // A styling wrapper is inserted between <li> and <button> -- the button's own tag/class/
        // text/role are all unchanged, but it's no longer a direct child of <li>.
        await _page.SetContentAsync("""
            <ul class="tabs">
              <li><span class="tab-item-wrapper"><button class="tab-item" role="tab">Tomorrow</button></span></li>
            </ul>
            """);

        var brokenPrimary = _page.Locator("ul.tabs > li > button.tab-item");
        Assert.Equal(0, await brokenPrimary.CountAsync());

        var relocated = await AdaptiveLocator.FindAsync(_page, brokenPrimary, "date-tab", store: store);
        Assert.NotNull(relocated);
        Assert.Equal("Tomorrow", await relocated!.TextContentAsync());
    }

    [Fact]
    public async Task Relocates_when_the_element_moves_to_a_different_part_of_the_page()
    {
        var store = NewStore();
        await _page.SetContentAsync("""
            <nav id="top-nav"><ul class="tabs">
              <li><button class="tab-item" data-qa="tab-today" role="tab">Today</button></li>
              <li><button class="tab-item" data-qa="tab-tomorrow" role="tab">Tomorrow</button></li>
            </ul></nav>
            <div id="other-place"></div>
            """);

        var seedPrimary = _page.Locator("#top-nav button.tab-item[data-qa='tab-tomorrow']");
        Assert.NotNull(await AdaptiveLocator.FindAsync(_page, seedPrimary, "date-tab", store: store));

        // The exact same element (tag/class/attrs/text all identical) is relocated to a
        // completely different container elsewhere on the page.
        await _page.SetContentAsync("""
            <nav id="top-nav"><ul class="tabs">
              <li><button class="tab-item" data-qa="tab-today" role="tab">Today</button></li>
            </ul></nav>
            <div id="other-place"><button class="tab-item" data-qa="tab-tomorrow" role="tab">Tomorrow</button></div>
            """);

        var brokenPrimary = _page.Locator("#top-nav button.tab-item[data-qa='tab-tomorrow']");
        Assert.Equal(0, await brokenPrimary.CountAsync());

        var relocated = await AdaptiveLocator.FindAsync(_page, brokenPrimary, "date-tab", store: store);
        Assert.NotNull(relocated);
        Assert.Equal("Tomorrow", await relocated!.TextContentAsync());
    }

    [Fact]
    public async Task Returns_null_on_a_cold_start_with_no_prior_fingerprint()
    {
        // If the primary locator has never once succeeded for this identifier, there is nothing
        // to relocate from -- this is the documented "cold start" limitation, not a bug.
        var store = NewStore();
        await _page.SetContentAsync("<button class=\"tab-item\">Tomorrow</button>");

        var neverSeeded = _page.Locator("button.does-not-exist");
        var result = await AdaptiveLocator.FindAsync(_page, neverSeeded, "never-seeded", store: store);

        Assert.Null(result);
    }
}
