using System.Text.Json.Serialization;

namespace TleReportingDashboard.Web.Services;

// App-wide theme (single row in RPT_app_theme). Admins tune the 14 named
// tokens via Admin → Theme; MainLayout reads the current value once per
// render and injects a `:root { --token: value; }` block so every page's
// chrome CSS resolves through `var(--…)`.
//
// Single global theme by design — per-company overrides and dark mode are
// out of v1 scope. The service layer is ready for either: it'd add a
// per-company column or a second row keyed by mode, neither of which
// require breaking changes to AppTheme.
public interface IThemeService
{
    // Returns the active theme. Cached after the first DB read; cache
    // invalidates on SaveAsync. Always returns a non-null value — falls
    // back to the seed (`AppTheme.Default`) when the DB row is missing
    // (pre-migration env, transient read failure).
    Task<AppTheme> GetAsync(CancellationToken ct = default);

    // Persists the supplied theme as the active one. Replaces the whole
    // payload — no partial-update / merge semantics, since the admin UI
    // always submits all 14 tokens together.
    Task SaveAsync(AppTheme theme, string? updatedBy, CancellationToken ct = default);
}

// Wire-format for the theme JSON. Field names are camelCase so the JSON
// stays compact + readable when admins inspect the row directly. All
// values are CSS color strings (hex or any browser-accepted form);
// validation is intentionally light — admins are the only writers, the
// admin UI uses MudColorPicker which always produces well-formed values.
public sealed class AppTheme
{
    [JsonPropertyName("surfacePage")]    public string SurfacePage    { get; set; } = "#FAFAFA";
    [JsonPropertyName("surfaceToolbar")] public string SurfaceToolbar { get; set; } = "#3C4043";
    [JsonPropertyName("surfaceCard")]    public string SurfaceCard    { get; set; } = "#FFFFFF";
    [JsonPropertyName("surfaceStrip")]   public string SurfaceStrip   { get; set; } = "#E8EAED";

    [JsonPropertyName("textPrimary")]    public string TextPrimary    { get; set; } = "#202124";
    [JsonPropertyName("textSecondary")]  public string TextSecondary  { get; set; } = "#5F6368";
    [JsonPropertyName("textMuted")]      public string TextMuted      { get; set; } = "#9AA0A6";
    [JsonPropertyName("textOnToolbar")]  public string TextOnToolbar  { get; set; } = "#E8EAED";

    [JsonPropertyName("borderDefault")]  public string BorderDefault  { get; set; } = "#DADCE0";
    [JsonPropertyName("borderSubtle")]   public string BorderSubtle   { get; set; } = "#E8EAED";

    [JsonPropertyName("accentPrimary")]  public string AccentPrimary  { get; set; } = "#1A73E8";
    [JsonPropertyName("accentSuccess")]  public string AccentSuccess  { get; set; } = "#1E8E3E";
    [JsonPropertyName("accentWarning")]  public string AccentWarning  { get; set; } = "#F9AB00";
    [JsonPropertyName("accentError")]    public string AccentError    { get; set; } = "#D32F2F";

    // ── Data-table tokens (grids in the Report Viewer + Master Dashboard) ──
    // Header band background and the alternating / hover row tints. The
    // values match the legacy hardcoded ones so an existing seeded row
    // upgrades cleanly (additive deserialization fills missing fields
    // from these defaults).
    [JsonPropertyName("tableHeaderBg")]   public string TableHeaderBg   { get; set; } = "#F7F7F7";
    [JsonPropertyName("tableRowHover")]   public string TableRowHover   { get; set; } = "#F1F3F4";
    [JsonPropertyName("tableRowStriped")] public string TableRowStriped { get; set; } = "#FAFAFA";

    // ── Detail-view group / total row tokens ──
    // The grouped Detail View renders as: header band (sticky thead) →
    // group header rows (PapayaWhip beige) → detail rows (white) →
    // group footer / subtotal rows (beige) → sticky grand-total band.
    // Each band gets its own bg/text pair so admins can re-paint the
    // grouping rhythm without touching code.
    [JsonPropertyName("detailGroupHeaderBg")]   public string DetailGroupHeaderBg   { get; set; } = "#FFEFD5";
    [JsonPropertyName("detailGroupHeaderText")] public string DetailGroupHeaderText { get; set; } = "#202124";
    [JsonPropertyName("detailGroupFooterBg")]   public string DetailGroupFooterBg   { get; set; } = "#F5F5DC";
    [JsonPropertyName("detailGroupFooterText")] public string DetailGroupFooterText { get; set; } = "#202124";
    [JsonPropertyName("detailGrandTotalBg")]    public string DetailGrandTotalBg    { get; set; } = "#F7F7F7";
    [JsonPropertyName("detailGrandTotalText")]  public string DetailGrandTotalText  { get; set; } = "#202124";

    // ── Master Dashboard tile header / footer ──
    // Each tile renders as: header band (title + tile-level controls) →
    // content area (chart / grid / etc.) → footer band (row count +
    // refreshed-at metadata, see MasterDashboard tile template). Both
    // bands get their own bg/text pair so admins can paint a consistent
    // accent on the dashboard's chrome without affecting the underlying
    // surface-card token used on dialog bodies and other plain papers.
    [JsonPropertyName("dashboardHeaderBg")]   public string DashboardHeaderBg   { get; set; } = "#FFFFFF";
    [JsonPropertyName("dashboardHeaderText")] public string DashboardHeaderText { get; set; } = "#202124";
    [JsonPropertyName("dashboardFooterBg")]   public string DashboardFooterBg   { get; set; } = "#FAFAFA";
    [JsonPropertyName("dashboardFooterText")] public string DashboardFooterText { get; set; } = "#5F6368";

    // ── Master Dashboard tile grid header / footer (the data table itself) ──
    // Inside each tile is a sortable summary grid (DashboardView's
    // .dash-summary-table). Its sticky <thead> band and sticky totals
    // <tfoot> row each get their own bg/text pair — distinct from the
    // tile-chrome dashboardHeader/Footer tokens because the chrome
    // wraps the tile while these paint the actual data grid.
    [JsonPropertyName("dashboardGridHeaderBg")]   public string DashboardGridHeaderBg   { get; set; } = "#ADD8E6"; // lightblue
    [JsonPropertyName("dashboardGridHeaderText")] public string DashboardGridHeaderText { get; set; } = "#202124";
    [JsonPropertyName("dashboardGridFooterBg")]   public string DashboardGridFooterBg   { get; set; } = "#FFF8DC"; // cornsilk
    [JsonPropertyName("dashboardGridFooterText")] public string DashboardGridFooterText { get; set; } = "#202124";

    // Snapshot of the seed values. Used by GetAsync as a fallback when
    // the DB row is missing, and by AdminThemeTab's "Reset" button.
    public static AppTheme Default() => new();

    // Renders the theme as a `:root` CSS-variable block. Token names use
    // kebab-case (CSS convention); referenced from chrome CSS as
    // `var(--surface-page)`, `var(--text-primary)`, etc.
    public string ToCssBlock() =>
        $@":root {{
    --surface-page:     {SurfacePage};
    --surface-toolbar:  {SurfaceToolbar};
    --surface-card:     {SurfaceCard};
    --surface-strip:    {SurfaceStrip};
    --text-primary:     {TextPrimary};
    --text-secondary:   {TextSecondary};
    --text-muted:       {TextMuted};
    --text-on-toolbar:  {TextOnToolbar};
    --border-default:   {BorderDefault};
    --border-subtle:    {BorderSubtle};
    --accent-primary:   {AccentPrimary};
    --accent-success:   {AccentSuccess};
    --accent-warning:   {AccentWarning};
    --accent-error:     {AccentError};
    --table-header-bg:           {TableHeaderBg};
    --table-row-hover:           {TableRowHover};
    --table-row-striped:         {TableRowStriped};
    --detail-group-header-bg:    {DetailGroupHeaderBg};
    --detail-group-header-text:  {DetailGroupHeaderText};
    --detail-group-footer-bg:    {DetailGroupFooterBg};
    --detail-group-footer-text:  {DetailGroupFooterText};
    --detail-grand-total-bg:     {DetailGrandTotalBg};
    --detail-grand-total-text:   {DetailGrandTotalText};
    --dashboard-header-bg:       {DashboardHeaderBg};
    --dashboard-header-text:     {DashboardHeaderText};
    --dashboard-footer-bg:       {DashboardFooterBg};
    --dashboard-footer-text:     {DashboardFooterText};
    --dashboard-grid-header-bg:    {DashboardGridHeaderBg};
    --dashboard-grid-header-text:  {DashboardGridHeaderText};
    --dashboard-grid-footer-bg:    {DashboardGridFooterBg};
    --dashboard-grid-footer-text:  {DashboardGridFooterText};
}}";
}
