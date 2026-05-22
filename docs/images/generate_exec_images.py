"""Generate executive-facing diagram/chart PNGs for the schema overview doc.

Style rules:
- Material-ish palette (blues/grays) so it matches the app chrome.
- Sans-serif, generous padding, soft shadows.
- Everything rendered at 160 DPI so the Word export looks sharp.
"""
import os

import matplotlib.pyplot as plt
import matplotlib.patches as patches
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch
from matplotlib.lines import Line2D

PRIMARY = "#1A73E8"
PRIMARY_DARK = "#0B4FBE"
SECONDARY = "#5F6368"
ACCENT_GREEN = "#1E8E3E"
ACCENT_YELLOW = "#F9AB00"
ACCENT_RED = "#D93025"
BG = "#F8F9FA"
SURFACE = "#FFFFFF"
TEXT = "#202124"
SUBTLE = "#DADCE0"

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

plt.rcParams.update({
    "font.family": "DejaVu Sans",
    "font.size": 10,
    "axes.titlesize": 14,
    "axes.titleweight": "bold",
    "axes.edgecolor": SUBTLE,
    "axes.labelcolor": TEXT,
    "xtick.color": TEXT,
    "ytick.color": TEXT,
    "figure.facecolor": SURFACE,
    "savefig.facecolor": SURFACE,
})


def _draw_box(ax, x, y, w, h, title, subtitle=None, fill=SURFACE, border=PRIMARY,
              title_color=PRIMARY, subtitle_color=SECONDARY,
              title_size=11, subtitle_size=8.5):
    """Round-cornered box with a centered title and optional subtitle.

    Subtitles are rendered at 8.5pt with linespacing 1.15 — callers should
    pass "\n"-delimited lines sized to fit within the box width. Single-line
    subtitles shouldn't exceed roughly (w_inches * 11) characters at this
    font size.
    """
    box = FancyBboxPatch(
        (x, y), w, h,
        boxstyle="round,pad=0.04,rounding_size=0.12",
        linewidth=1.6, edgecolor=border, facecolor=fill,
    )
    ax.add_patch(box)
    if subtitle:
        # Offset the title upward in proportion to how many subtitle lines
        # there are, so a 3-line subtitle doesn't shove the title off the top.
        lines = subtitle.count("\n") + 1
        title_y_offset = {1: 0.62, 2: 0.70, 3: 0.74}.get(lines, 0.78)
        sub_y_offset = {1: 0.32, 2: 0.33, 3: 0.32}.get(lines, 0.30)
        ax.text(x + w / 2, y + h * title_y_offset, title, ha="center", va="center",
                fontsize=title_size, fontweight="bold", color=title_color)
        ax.text(x + w / 2, y + h * sub_y_offset, subtitle, ha="center", va="center",
                fontsize=subtitle_size, color=subtitle_color, linespacing=1.2)
    else:
        ax.text(x + w / 2, y + h / 2, title, ha="center", va="center",
                fontsize=title_size, fontweight="bold", color=title_color)


def _draw_arrow(ax, x1, y1, x2, y2, color=SECONDARY, style="->", lw=1.4):
    arr = FancyArrowPatch(
        (x1, y1), (x2, y2),
        arrowstyle=style, mutation_scale=14, color=color, lw=lw,
        shrinkA=6, shrinkB=6,
    )
    ax.add_patch(arr)


# ── 1. System architecture ─────────────────────────────────────────────────
def architecture():
    # Widened to 12x6 so the middle app box can fit its multi-line subtitle
    # without clipping against the side boxes. All boxes sit on a 0.3-unit
    # grid for consistent alignment.
    fig, ax = plt.subplots(figsize=(12, 6), dpi=160)
    ax.set_xlim(0, 12)
    ax.set_ylim(0, 6)
    ax.axis("off")
    ax.set_title("System Architecture", pad=18, color=TEXT, fontsize=15)

    # Users (left column)
    _draw_box(ax, 0.3, 4.0, 2.2, 1.2, "Users",
              "Browser\nEntra SSO",
              fill="#E8F0FE", border=PRIMARY)
    _draw_box(ax, 0.3, 2.2, 2.2, 1.2, "Admins",
              "Schema +\nconnection mgmt",
              fill="#FEF7E0", border=ACCENT_YELLOW, title_color="#7B5E00")

    # Web app (middle) — widest because its subtitle has the most detail.
    _draw_box(ax, 3.3, 3.1, 5.0, 2.1, "Reporting Web App",
              "Blazor Server\nReport Library · Builder\nMaster Dashboard · Scheduler",
              fill=SURFACE, border=PRIMARY_DARK)

    # Worker
    _draw_box(ax, 3.3, 0.6, 5.0, 1.5, "Background Worker",
              "Hangfire\nscheduled emails · exports",
              fill="#E6F4EA", border=ACCENT_GREEN, title_color=ACCENT_GREEN)

    # ConfigDb (right top)
    _draw_box(ax, 9.1, 4.0, 2.6, 1.5, "ConfigDb",
              "Reports · Schemas\nUsers · Dashboards",
              fill=SURFACE, border=SECONDARY, title_color=TEXT)

    # Tenant data sources (right bottom)
    _draw_box(ax, 9.1, 1.4, 2.6, 2.1, "Tenant Data Sources",
              "SQL Server · Postgres\nper-company connections",
              fill=SURFACE, border=SECONDARY, title_color=TEXT)

    # Arrows
    _draw_arrow(ax, 2.5, 4.6, 3.3, 4.5)
    _draw_arrow(ax, 2.5, 2.8, 3.3, 3.5)
    _draw_arrow(ax, 8.3, 4.7, 9.1, 4.7, color=PRIMARY_DARK)
    _draw_arrow(ax, 8.3, 3.5, 9.1, 2.9, color=PRIMARY_DARK)
    _draw_arrow(ax, 5.8, 3.1, 5.8, 2.1, color=ACCENT_GREEN, style="<->")
    _draw_arrow(ax, 8.3, 1.4, 9.1, 2.0, color=ACCENT_GREEN)

    fig.tight_layout()
    fig.savefig(os.path.join(OUT_DIR, "architecture.png"), bbox_inches="tight")
    plt.close(fig)


# ── 2. Capability footprint (horizontal bar) ───────────────────────────────
def capabilities():
    capabilities_data = [
        ("Ad-hoc Reporting", 95),
        ("Saved Report Library", 95),
        ("Scheduled Email Delivery", 90),
        ("Interactive Dashboards", 85),
        ("Chart Visualizations", 80),
        ("Cross-Company Support", 90),
        ("Role-Based Admin", 85),
        ("Report Sharing", 80),
        ("Excel / CSV Export", 95),
        ("Grid Templates", 75),
    ]
    labels = [c[0] for c in capabilities_data]
    values = [c[1] for c in capabilities_data]

    fig, ax = plt.subplots(figsize=(9, 5.5), dpi=160)
    ax.barh(labels, values, color=PRIMARY, edgecolor="none", height=0.62)
    for i, v in enumerate(values):
        ax.text(v + 1.5, i, f"{v}%", va="center", fontsize=9,
                color=SECONDARY, fontweight="bold")
    ax.set_xlim(0, 110)
    ax.invert_yaxis()
    ax.set_title("Capability Coverage", pad=14, color=TEXT)
    ax.set_xlabel("Maturity", color=SECONDARY, fontsize=9)
    ax.tick_params(axis="x", colors=SECONDARY)
    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)
    ax.spines["left"].set_color(SUBTLE)
    ax.spines["bottom"].set_color(SUBTLE)
    ax.set_axisbelow(True)
    ax.grid(axis="x", color=SUBTLE, linewidth=0.6, alpha=0.5)

    fig.tight_layout()
    fig.savefig(os.path.join(OUT_DIR, "capabilities.png"), bbox_inches="tight")
    plt.close(fig)


# ── 3. User roles + permissions ────────────────────────────────────────────
def user_roles():
    # Widened to 11x5 and boxes sized at 3.2 wide with ~0.4 gutters so every
    # subtitle word can sit on its own line without wrapping outside.
    fig, ax = plt.subplots(figsize=(11, 5), dpi=160)
    ax.set_xlim(0, 11)
    ax.set_ylim(0, 5)
    ax.axis("off")
    ax.set_title("Users & Permissions", pad=14, color=TEXT, fontsize=15)

    _draw_box(ax, 0.4, 2.7, 3.2, 1.9,
              "End User",
              "Builds and runs\nown reports\nViews shared reports",
              fill="#E8F0FE", border=PRIMARY)
    _draw_box(ax, 3.9, 2.7, 3.2, 1.9,
              "Power User",
              "All End User rights\nMaster Dashboard owner\nSchedules + broad sharing",
              fill="#D2E3FC", border=PRIMARY_DARK,
              title_color=PRIMARY_DARK)
    _draw_box(ax, 7.4, 2.7, 3.2, 1.9,
              "Admin",
              "Schema + connection mgmt\nCross-user visibility\nGrid Templates",
              fill="#FEF7E0", border=ACCENT_YELLOW, title_color="#7B5E00")

    _draw_box(ax, 2.75, 0.4, 5.5, 1.6,
              "Microsoft Entra SSO",
              "Every action traces to the user's Entra identity.\nNo application-managed passwords.",
              fill=SURFACE, border=SECONDARY, title_color=TEXT)

    for x in (2.0, 5.5, 9.0):
        _draw_arrow(ax, x, 2.7, x, 2.0, color=SECONDARY)

    fig.tight_layout()
    fig.savefig(os.path.join(OUT_DIR, "user-roles.png"), bbox_inches="tight")
    plt.close(fig)


# ── 4. Multi-tenant model ──────────────────────────────────────────────────
def multi_tenant():
    fig, ax = plt.subplots(figsize=(9.5, 4.5), dpi=160)
    ax.set_xlim(0, 10)
    ax.set_ylim(0, 4.5)
    ax.axis("off")
    ax.set_title("Multi-Company Model — one app, many tenants", pad=14, color=TEXT)

    _draw_box(ax, 3.5, 3.2, 3.0, 1.1, "Reporting App",
              "Single deployment", fill=PRIMARY, border=PRIMARY_DARK,
              title_color="white", subtitle_color="#D2E3FC")

    # Three companies — generic placeholder names so the diagram works
    # for any tenant. No real customer or third-party platform names.
    companies = [
        (0.3, "Company A", "SQL Server · operational DB", ACCENT_GREEN),
        (3.7, "Company B", "Postgres · CRM mirror",      ACCENT_YELLOW),
        (7.1, "Company C", "SQL Server · warehouse",      PRIMARY),
    ]
    for x, name, src, color in companies:
        _draw_box(ax, x, 1.4, 2.6, 1.3, name, src, fill=SURFACE,
                  border=color, title_color=color)
        _draw_arrow(ax, x + 1.3, 2.7, x + 1.3, 3.2, color=color)

    # Tenant data sources row
    for x, name, src, color in companies:
        _draw_box(ax, x, 0.1, 2.6, 1.0,
                  "Tenant Database", src, fill=SURFACE,
                  border=SECONDARY, title_color=SECONDARY,
                  subtitle_color=SECONDARY)
        _draw_arrow(ax, x + 1.3, 1.1, x + 1.3, 1.4, color=SECONDARY)

    fig.tight_layout()
    fig.savefig(os.path.join(OUT_DIR, "multi-tenant.png"), bbox_inches="tight")
    plt.close(fig)


# ── 5. Report lifecycle flow ───────────────────────────────────────────────
def report_flow():
    fig, ax = plt.subplots(figsize=(10, 2.8), dpi=160)
    ax.set_xlim(0, 10)
    ax.set_ylim(0, 2.8)
    ax.axis("off")
    ax.set_title("How a Report Gets Built and Delivered", pad=14, color=TEXT)

    steps = [
        ("Pick Fields",    "From the curated catalog"),
        ("Add Filters",    "Date ranges, categories"),
        ("Shape Output",   "Group, sort, aggregate"),
        ("Visualize",      "Grid, chart, dashboard"),
        ("Deliver",        "Save · Share · Schedule"),
    ]
    x = 0.2
    w = 1.76
    gap = 0.12
    for i, (title, sub) in enumerate(steps):
        _draw_box(ax, x, 0.8, w, 1.4, title, sub,
                  fill=SURFACE, border=PRIMARY, title_color=PRIMARY_DARK)
        if i < len(steps) - 1:
            _draw_arrow(ax, x + w, 1.5, x + w + gap, 1.5, color=SECONDARY)
        x += w + gap

    fig.tight_layout()
    fig.savefig(os.path.join(OUT_DIR, "report-flow.png"), bbox_inches="tight")
    plt.close(fig)


# ── 6. Entity-relationship diagram (for the engineering doc) ───────────────
def _erd_table(ax, x, y, w, h, name, rows, header_fill=PRIMARY, header_text="white"):
    """Draws a rounded titled box with a list of column rows underneath.

    rows is a list of (col_text, is_pk, is_fk) tuples. Keys render in bold
    with a small marker on the left.
    """
    # Header
    header_h = 0.32
    header = FancyBboxPatch(
        (x, y + h - header_h), w, header_h,
        boxstyle="round,pad=0.02,rounding_size=0.08",
        linewidth=0, facecolor=header_fill,
    )
    ax.add_patch(header)
    ax.text(x + w / 2, y + h - header_h / 2, name,
            ha="center", va="center",
            fontsize=9, fontweight="bold", color=header_text)
    # Body
    body = FancyBboxPatch(
        (x, y), w, h - header_h + 0.04,
        boxstyle="round,pad=0.02,rounding_size=0.08",
        linewidth=1.1, edgecolor=SECONDARY, facecolor=SURFACE,
    )
    ax.add_patch(body)
    # Rows
    row_h = (h - header_h - 0.08) / max(len(rows), 1)
    for i, (txt, is_pk, is_fk) in enumerate(rows):
        ry = y + h - header_h - 0.04 - (i + 0.5) * row_h
        marker = ""
        if is_pk and is_fk:
            marker = "◆"
        elif is_pk:
            marker = "●"
        elif is_fk:
            marker = "◇"
        if marker:
            ax.text(x + 0.08, ry, marker, ha="left", va="center",
                    fontsize=7, color=PRIMARY if is_pk else ACCENT_GREEN)
        ax.text(x + 0.22, ry, txt, ha="left", va="center",
                fontsize=7.5, color=TEXT,
                fontweight="bold" if is_pk else "normal")


def _fk_line(ax, x1, y1, x2, y2, color=ACCENT_GREEN):
    line = Line2D([x1, x2], [y1, y2], color=color, linewidth=1.0, alpha=0.75)
    ax.add_line(line)
    # dot at the target (child) end
    ax.plot(x2, y2, "o", markersize=3, color=color)


def erd():
    fig, ax = plt.subplots(figsize=(16, 11), dpi=160)
    ax.set_xlim(0, 16)
    ax.set_ylim(0, 11)
    ax.axis("off")
    ax.set_title("ConfigDb — Entity Relationship Diagram", pad=14,
                 color=TEXT, fontsize=16)
    # Legend
    ax.text(0.2, 10.45, "●  PK        ◇  FK        ◆  PK+FK",
            fontsize=8.5, color=SECONDARY, fontweight="bold")

    # Layout: 4 columns × 3-4 rows. Coordinates picked to leave room for FK lines.
    # Anchor: companies (top center).
    companies = (6.5, 8.2, 3.0, 2.0)  # x, y, w, h
    _erd_table(ax, *companies, "RPT_companies", [
        ("id", True, False),
        ("code (unique)", False, False),
        ("name", False, False),
        ("data_source_type", False, False),
        ("is_active", False, False),
    ])

    # company_connections — below companies
    connections = (6.5, 5.1, 3.0, 2.6)
    _erd_table(ax, *connections, "RPT_company_connections", [
        ("id", True, False),
        ("company_id", False, True),
        ("name / connection_type", False, False),
        ("is_default / is_active", False, False),
        ("ss_* (SQL Server fields)", False, False),
        ("pg_* (Postgres fields)", False, False),
        ("pg_display_timezone", False, False),
        ("table_filter_sql / schema_filter_sql", False, False),
    ])

    # saved_reports — below connections
    saved_reports = (6.5, 1.9, 3.0, 2.7)
    _erd_table(ax, *saved_reports, "RPT_saved_reports", [
        ("id", True, False),
        ("company_id (vestigial)", False, True),
        ("connection_id", False, True),
        ("owner_id / owner_email", False, False),
        ("name / field_ids (JSON)", False, False),
        ("filters / aggregations (JSON)", False, False),
        ("column_state (JSON)", False, False),
        ("primary_table", False, False),
        ("grid_template_id (soft link)", False, False),
    ])

    # LEFT COLUMN — user/identity tables
    user_companies = (0.4, 9.0, 2.8, 1.6)
    _erd_table(ax, *user_companies, "RPT_user_companies", [
        ("user_id", True, False),
        ("company_id", True, True),
        ("permission (View|Edit)", False, False),
        ("is_default", False, False),
    ], header_fill=SECONDARY)

    admins = (0.4, 6.9, 2.8, 1.7)
    _erd_table(ax, *admins, "RPT_admins", [
        ("id", True, False),
        ("email / user_id", False, False),
        ("scope (global|company)", False, False),
        ("company_id (nullable)", False, True),
    ], header_fill=SECONDARY)

    user_prefs = (0.4, 4.3, 2.8, 2.4)
    _erd_table(ax, *user_prefs, "RPT_user_preferences", [
        ("user_id", True, False),
        ("company_id", True, True),
        ("is_dark_mode / default_page_size", False, False),
        ("report_page_sizes (JSON)", False, False),
        ("master_dashboard_title/logo", False, False),
        ("schema_builder_company_id", False, False),
        ("schema_builder_connection_id", False, False),
        ("report_library_company_id", False, False),
    ], header_fill=SECONDARY)

    master_tabs = (0.4, 2.6, 2.8, 1.5)
    _erd_table(ax, *master_tabs, "RPT_master_dashboard_tabs", [
        ("id (identity)", True, False),
        ("company_id", False, True),
        ("user_id / label", False, False),
        ("sort_order / title_align", False, False),
    ], header_fill=SECONDARY)

    master_tiles = (0.4, 0.5, 2.8, 1.9)
    _erd_table(ax, *master_tiles, "RPT_master_dashboard_tiles", [
        ("id (identity)", True, False),
        ("company_id", False, True),
        ("source_company_id", False, True),
        ("user_id / tab_id / report_id", False, False),
        ("col_span / height / sort_order", False, False),
    ], header_fill=SECONDARY)

    # RIGHT COLUMN — schema / catalog
    schema_config = (12.8, 9.0, 2.9, 1.6)
    _erd_table(ax, *schema_config, "RPT_schema_config", [
        ("connection_id (PK)", True, True),
        ("company_id (vestigial)", False, True),
        ("json (SchemaConfig)", False, False),
        ("updated_by / updated_at", False, False),
    ], header_fill=ACCENT_GREEN)

    schema_hist = (12.8, 7.0, 2.9, 1.7)
    _erd_table(ax, *schema_hist, "RPT_schema_config_history", [
        ("history_id (identity)", True, False),
        ("connection_id", False, True),
        ("company_id (vestigial)", False, True),
        ("json (SchemaConfig snapshot)", False, False),
        ("updated_by / updated_at", False, False),
    ], header_fill=ACCENT_GREEN)

    primary_tables = (12.8, 4.7, 2.9, 2.0)
    _erd_table(ax, *primary_tables, "RPT_custom_primary_tables", [
        ("id", True, False),
        ("connection_id", False, True),
        ("table_name / alias", False, False),
        ("is_primary / is_default_primary", False, False),
        ("created_by_id / created_by_email", False, False),
    ], header_fill=ACCENT_GREEN)

    grid_templates = (12.8, 2.4, 2.9, 2.0)
    _erd_table(ax, *grid_templates, "RPT_grid_templates", [
        ("id", True, False),
        ("company_id / connection_id", False, True),
        ("name / description / is_shared", False, False),
        ("owner_id / owner_email", False, False),
        ("field_ids / column_state (JSON)", False, False),
    ], header_fill=ACCENT_GREEN)

    # BOTTOM CENTER — report children
    shares = (10.1, 0.3, 2.6, 1.6)
    _erd_table(ax, *shares, "RPT_report_shares", [
        ("id", True, False),
        ("report_id", False, True),
        ("company_id", False, True),
        ("shared_with_id / type", False, False),
        ("permission (viewer|editor)", False, False),
    ], header_fill="#8AB4F8")

    schedules = (3.4, 0.3, 2.6, 1.9)
    _erd_table(ax, *schedules, "RPT_report_schedules", [
        ("id", True, False),
        ("report_id", False, True),
        ("company_id", False, True),
        ("cron / schedule_pattern (JSON)", False, False),
        ("subject / recipients / cc / bcc", False, False),
        ("attachment_format / is_active", False, False),
    ], header_fill="#8AB4F8")

    # ── Foreign-key lines ──────────────────────────────────────────────────
    # companies ← company_connections
    _fk_line(ax, companies[0] + 1.5, companies[1],
             connections[0] + 1.5, connections[1] + connections[3])
    # company_connections ← saved_reports
    _fk_line(ax, connections[0] + 1.5, connections[1],
             saved_reports[0] + 1.5, saved_reports[1] + saved_reports[3])
    # companies ← user_companies
    _fk_line(ax, companies[0], companies[1] + 1.6,
             user_companies[0] + user_companies[2], user_companies[1] + 0.9)
    # companies ← admins
    _fk_line(ax, companies[0], companies[1] + 0.5,
             admins[0] + admins[2], admins[1] + 0.9)
    # companies ← user_prefs (via company_id)
    _fk_line(ax, companies[0], companies[1] + 0.2,
             user_prefs[0] + user_prefs[2], user_prefs[1] + 2.0)
    # companies ← master_tabs
    _fk_line(ax, companies[0], companies[1],
             master_tabs[0] + master_tabs[2], master_tabs[1] + 0.8)
    # companies ← master_tiles  (both company_id + source_company_id dashed)
    _fk_line(ax, companies[0], companies[1] - 0.2,
             master_tiles[0] + master_tiles[2], master_tiles[1] + 1.0)
    # connections ← schema_config
    _fk_line(ax, connections[0] + connections[2], connections[1] + 2.0,
             schema_config[0], schema_config[1] + 0.8)
    # connections ← schema_config_history
    _fk_line(ax, connections[0] + connections[2], connections[1] + 1.5,
             schema_hist[0], schema_hist[1] + 0.8)
    # connections ← custom_primary_tables
    _fk_line(ax, connections[0] + connections[2], connections[1] + 1.0,
             primary_tables[0], primary_tables[1] + 1.0)
    # connections ← grid_templates
    _fk_line(ax, connections[0] + connections[2], connections[1] + 0.3,
             grid_templates[0], grid_templates[1] + 1.0)
    # saved_reports ← shares
    _fk_line(ax, saved_reports[0] + saved_reports[2] - 0.3, saved_reports[1],
             shares[0] + 1.3, shares[1] + shares[3])
    # saved_reports ← schedules
    _fk_line(ax, saved_reports[0] + 0.3, saved_reports[1],
             schedules[0] + 1.3, schedules[1] + schedules[3])

    fig.tight_layout()
    fig.savefig(os.path.join(OUT_DIR, "erd.png"), bbox_inches="tight")
    plt.close(fig)


if __name__ == "__main__":
    architecture()
    capabilities()
    user_roles()
    multi_tenant()
    report_flow()
    erd()
    print("Generated:")
    for name in ("architecture", "capabilities", "user-roles", "multi-tenant", "report-flow", "erd"):
        path = os.path.join(OUT_DIR, f"{name}.png")
        print(f"  {path}  ({os.path.getsize(path) / 1024:.1f} KB)")
