using System.Text.Json.Serialization;

namespace TleReportingDashboard.Web.Services;

// Per-company theme service. Every render path that draws chrome
// (MainLayout, dashboard tiles, etc.) resolves the active theme via
// GetAsync(companyId) and injects a `:root { --token: value; }` block
// so chrome CSS picks up the right palette. Storage:
//
//   • RPT_app_theme.company_id IS NULL → global default. Seeded by the
//     2026-05-04 migration; serves as the fallback when a company hasn't
//     defined its own theme.
//   • RPT_app_theme.company_id = <guid> → per-company override.
//
// GetAsync(companyId): per-company row if present; else global; else the
// hardcoded AppTheme.Default. SaveAsync(theme, companyId, ...): writes to
// the global row when companyId is null; upserts the per-company row
// otherwise. Callers MUST gate per-company writes behind admin auth.
public interface IThemeService
{
    Task<AppTheme> GetAsync(Guid? companyId = null, CancellationToken ct = default);
    Task SaveAsync(AppTheme theme, Guid? companyId, string? updatedBy, CancellationToken ct = default);
}

// Wire-format for the theme JSON. Field names are camelCase so the JSON
// stays compact + readable when admins inspect the row directly. All
// values are CSS color strings (hex or any browser-accepted form);
// validation is intentionally light — admins are the only writers, the
// admin UI uses MudColorPicker which always produces well-formed values.
public sealed class AppTheme
{
    [JsonPropertyName("surfacePage")]    public string SurfacePage    { get; set; } = "#F9FAFB";
    [JsonPropertyName("surfaceToolbar")] public string SurfaceToolbar { get; set; } = "#FFFFFF";
    [JsonPropertyName("surfaceCard")]    public string SurfaceCard    { get; set; } = "#FFFFFF";
    [JsonPropertyName("surfaceStrip")]   public string SurfaceStrip   { get; set; } = "#F1F5F9";

    [JsonPropertyName("textPrimary")]    public string TextPrimary    { get; set; } = "#0F172A";
    [JsonPropertyName("textSecondary")]  public string TextSecondary  { get; set; } = "#475569";
    [JsonPropertyName("textMuted")]      public string TextMuted      { get; set; } = "#94A3B8";
    [JsonPropertyName("textOnToolbar")]  public string TextOnToolbar  { get; set; } = "#0F172A";

    [JsonPropertyName("borderDefault")]  public string BorderDefault  { get; set; } = "#E2E8F0";
    [JsonPropertyName("borderSubtle")]   public string BorderSubtle   { get; set; } = "#F1F5F9";

    [JsonPropertyName("accentPrimary")]  public string AccentPrimary  { get; set; } = "#4F46E5";
    [JsonPropertyName("accentSuccess")]  public string AccentSuccess  { get; set; } = "#10B981";
    [JsonPropertyName("accentWarning")]  public string AccentWarning  { get; set; } = "#F59E0B";
    [JsonPropertyName("accentError")]    public string AccentError    { get; set; } = "#EF4444";

    // ── Data-table tokens (grids in the Report Viewer + Master Dashboard) ──
    // Header band background and the alternating / hover row tints. The
    // values match the legacy hardcoded ones so an existing seeded row
    // upgrades cleanly (additive deserialization fills missing fields
    // from these defaults).
    [JsonPropertyName("tableHeaderBg")]   public string TableHeaderBg   { get; set; } = "#F8FAFC";
    [JsonPropertyName("tableRowHover")]   public string TableRowHover   { get; set; } = "#F8FAFC";
    [JsonPropertyName("tableRowStriped")] public string TableRowStriped { get; set; } = "#FCFCFD";

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

    // Word-style text formatting per band. Bold / Italic / Underline
    // are booleans that emit font-weight, font-style, text-decoration.
    // FontFamily / FontSize are free-form CSS strings (e.g. "Arial",
    // "0.875rem", "14px"). Empty string → "inherit" — the band stays
    // visually identical to the surrounding text. Defaults preserve
    // the previously-hardcoded weights (headers / footers / totals
    // were 600-700; collapsed to 700 here with negligible visual diff).
    [JsonPropertyName("detailGroupHeaderBold")]       public bool   DetailGroupHeaderBold       { get; set; } = true;
    [JsonPropertyName("detailGroupHeaderItalic")]     public bool   DetailGroupHeaderItalic     { get; set; } = false;
    [JsonPropertyName("detailGroupHeaderUnderline")]  public bool   DetailGroupHeaderUnderline  { get; set; } = false;
    [JsonPropertyName("detailGroupHeaderFontFamily")] public string DetailGroupHeaderFontFamily { get; set; } = "";
    [JsonPropertyName("detailGroupHeaderFontSize")]   public string DetailGroupHeaderFontSize   { get; set; } = "";

    [JsonPropertyName("detailGroupFooterBold")]       public bool   DetailGroupFooterBold       { get; set; } = true;
    [JsonPropertyName("detailGroupFooterItalic")]     public bool   DetailGroupFooterItalic     { get; set; } = false;
    [JsonPropertyName("detailGroupFooterUnderline")]  public bool   DetailGroupFooterUnderline  { get; set; } = false;
    [JsonPropertyName("detailGroupFooterFontFamily")] public string DetailGroupFooterFontFamily { get; set; } = "";
    [JsonPropertyName("detailGroupFooterFontSize")]   public string DetailGroupFooterFontSize   { get; set; } = "";

    [JsonPropertyName("detailGrandTotalBold")]        public bool   DetailGrandTotalBold        { get; set; } = true;
    [JsonPropertyName("detailGrandTotalItalic")]      public bool   DetailGrandTotalItalic      { get; set; } = false;
    [JsonPropertyName("detailGrandTotalUnderline")]   public bool   DetailGrandTotalUnderline   { get; set; } = false;
    [JsonPropertyName("detailGrandTotalFontFamily")]  public string DetailGrandTotalFontFamily  { get; set; } = "";
    [JsonPropertyName("detailGrandTotalFontSize")]    public string DetailGrandTotalFontSize    { get; set; } = "";

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

    // Tile-chrome text formatting (paired with the *Text / *Bg tokens
    // above). Header defaults to bold to match the previously-inline
    // font-weight: 700 on the tile title; footer stays normal-weight
    // since it's a low-visual-priority metadata strip.
    [JsonPropertyName("dashboardHeaderBold")]       public bool   DashboardHeaderBold       { get; set; } = true;
    [JsonPropertyName("dashboardHeaderItalic")]     public bool   DashboardHeaderItalic     { get; set; } = false;
    [JsonPropertyName("dashboardHeaderUnderline")]  public bool   DashboardHeaderUnderline  { get; set; } = false;
    [JsonPropertyName("dashboardHeaderFontFamily")] public string DashboardHeaderFontFamily { get; set; } = "";
    [JsonPropertyName("dashboardHeaderFontSize")]   public string DashboardHeaderFontSize   { get; set; } = "";

    [JsonPropertyName("dashboardFooterBold")]       public bool   DashboardFooterBold       { get; set; } = false;
    [JsonPropertyName("dashboardFooterItalic")]     public bool   DashboardFooterItalic     { get; set; } = false;
    [JsonPropertyName("dashboardFooterUnderline")]  public bool   DashboardFooterUnderline  { get; set; } = false;
    [JsonPropertyName("dashboardFooterFontFamily")] public string DashboardFooterFontFamily { get; set; } = "";
    [JsonPropertyName("dashboardFooterFontSize")]   public string DashboardFooterFontSize   { get; set; } = "";

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

    // Tile-data-grid text formatting (paired with the *Text / *Bg
    // tokens above). Both default to bold — matches the existing
    // font-weight: 600 on .dash-summary-table thead th and
    // .dash-footer-cell.
    [JsonPropertyName("dashboardGridHeaderBold")]       public bool   DashboardGridHeaderBold       { get; set; } = true;
    [JsonPropertyName("dashboardGridHeaderItalic")]     public bool   DashboardGridHeaderItalic     { get; set; } = false;
    [JsonPropertyName("dashboardGridHeaderUnderline")]  public bool   DashboardGridHeaderUnderline  { get; set; } = false;
    [JsonPropertyName("dashboardGridHeaderFontFamily")] public string DashboardGridHeaderFontFamily { get; set; } = "";
    [JsonPropertyName("dashboardGridHeaderFontSize")]   public string DashboardGridHeaderFontSize   { get; set; } = "";

    [JsonPropertyName("dashboardGridFooterBold")]       public bool   DashboardGridFooterBold       { get; set; } = true;
    [JsonPropertyName("dashboardGridFooterItalic")]     public bool   DashboardGridFooterItalic     { get; set; } = false;
    [JsonPropertyName("dashboardGridFooterUnderline")]  public bool   DashboardGridFooterUnderline  { get; set; } = false;
    [JsonPropertyName("dashboardGridFooterFontFamily")] public string DashboardGridFooterFontFamily { get; set; } = "";
    [JsonPropertyName("dashboardGridFooterFontSize")]   public string DashboardGridFooterFontSize   { get; set; } = "";

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
    --detail-group-header-weight:    {Weight(DetailGroupHeaderBold)};
    --detail-group-header-style:     {Style(DetailGroupHeaderItalic)};
    --detail-group-header-deco:      {Deco(DetailGroupHeaderUnderline)};
    --detail-group-header-font:      {FontFamily(DetailGroupHeaderFontFamily)};
    --detail-group-header-size:      {FontSize(DetailGroupHeaderFontSize)};
    --detail-group-footer-bg:    {DetailGroupFooterBg};
    --detail-group-footer-text:  {DetailGroupFooterText};
    --detail-group-footer-weight:    {Weight(DetailGroupFooterBold)};
    --detail-group-footer-style:     {Style(DetailGroupFooterItalic)};
    --detail-group-footer-deco:      {Deco(DetailGroupFooterUnderline)};
    --detail-group-footer-font:      {FontFamily(DetailGroupFooterFontFamily)};
    --detail-group-footer-size:      {FontSize(DetailGroupFooterFontSize)};
    --detail-grand-total-bg:     {DetailGrandTotalBg};
    --detail-grand-total-text:   {DetailGrandTotalText};
    --detail-grand-total-weight:     {Weight(DetailGrandTotalBold)};
    --detail-grand-total-style:      {Style(DetailGrandTotalItalic)};
    --detail-grand-total-deco:       {Deco(DetailGrandTotalUnderline)};
    --detail-grand-total-font:       {FontFamily(DetailGrandTotalFontFamily)};
    --detail-grand-total-size:       {FontSize(DetailGrandTotalFontSize)};
    --dashboard-header-bg:       {DashboardHeaderBg};
    --dashboard-header-text:     {DashboardHeaderText};
    --dashboard-header-weight:       {Weight(DashboardHeaderBold)};
    --dashboard-header-style:        {Style(DashboardHeaderItalic)};
    --dashboard-header-deco:         {Deco(DashboardHeaderUnderline)};
    --dashboard-header-font:         {FontFamily(DashboardHeaderFontFamily)};
    --dashboard-header-size:         {FontSize(DashboardHeaderFontSize)};
    --dashboard-footer-bg:       {DashboardFooterBg};
    --dashboard-footer-text:     {DashboardFooterText};
    --dashboard-footer-weight:       {Weight(DashboardFooterBold)};
    --dashboard-footer-style:        {Style(DashboardFooterItalic)};
    --dashboard-footer-deco:         {Deco(DashboardFooterUnderline)};
    --dashboard-footer-font:         {FontFamily(DashboardFooterFontFamily)};
    --dashboard-footer-size:         {FontSize(DashboardFooterFontSize)};
    --dashboard-grid-header-bg:    {DashboardGridHeaderBg};
    --dashboard-grid-header-text:  {DashboardGridHeaderText};
    --dashboard-grid-header-weight:  {Weight(DashboardGridHeaderBold)};
    --dashboard-grid-header-style:   {Style(DashboardGridHeaderItalic)};
    --dashboard-grid-header-deco:    {Deco(DashboardGridHeaderUnderline)};
    --dashboard-grid-header-font:    {FontFamily(DashboardGridHeaderFontFamily)};
    --dashboard-grid-header-size:    {FontSize(DashboardGridHeaderFontSize)};
    --dashboard-grid-footer-bg:    {DashboardGridFooterBg};
    --dashboard-grid-footer-text:  {DashboardGridFooterText};
    --dashboard-grid-footer-weight:  {Weight(DashboardGridFooterBold)};
    --dashboard-grid-footer-style:   {Style(DashboardGridFooterItalic)};
    --dashboard-grid-footer-deco:    {Deco(DashboardGridFooterUnderline)};
    --dashboard-grid-footer-font:    {FontFamily(DashboardGridFooterFontFamily)};
    --dashboard-grid-footer-size:    {FontSize(DashboardGridFooterFontSize)};
}}";

    // Helpers that map theme flags / strings into safe CSS-variable
    // values. Empty inputs collapse to "inherit" so an admin who
    // hasn't touched a band sees no visual change from the parent
    // element. The four formatters keep the ToCssBlock template
    // readable instead of forcing an inline ternary on every line.
    private static string Weight(bool bold) => bold ? "700" : "400";
    private static string Style(bool italic) => italic ? "italic" : "normal";
    private static string Deco(bool underline) => underline ? "underline" : "none";
    private static string FontFamily(string family) =>
        string.IsNullOrWhiteSpace(family) ? "inherit" : family;
    private static string FontSize(string size) =>
        string.IsNullOrWhiteSpace(size) ? "inherit" : size;
}
