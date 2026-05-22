"""Wireframe mockup of the proposed ad-hoc filter UI. One image, annotated
rows for the four most common filter shapes. Kept deliberately schematic —
colors + spacing match the app's Material palette but it's a sketch, not a
pixel-perfect MudBlazor render."""
import os
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, Rectangle

PRIMARY = "#1A73E8"
PRIMARY_DARK = "#0B4FBE"
SECONDARY = "#5F6368"
SURFACE = "#FFFFFF"
BG = "#F8F9FA"
LINE = "#DADCE0"
TEXT = "#202124"
ACCENT_GREEN = "#1E8E3E"
MUTED = "#9AA0A6"

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "prototype-filter-ui.png")

plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 10})


def field_box(ax, x, y, w, h, label, value, color=SURFACE, border=LINE,
              value_color=TEXT, italic=False):
    ax.add_patch(FancyBboxPatch((x, y), w, h,
                                boxstyle="round,pad=0.02,rounding_size=0.08",
                                linewidth=1.0, edgecolor=border, facecolor=color))
    ax.text(x + 0.08, y + h - 0.12, label, fontsize=7, color=SECONDARY)
    ax.text(x + 0.08, y + 0.10, value, fontsize=9,
            color=value_color, fontstyle=("italic" if italic else "normal"))


def chip(ax, x, y, w, h, text, fill="#E8F0FE", border=PRIMARY, text_color=PRIMARY_DARK):
    ax.add_patch(FancyBboxPatch((x, y), w, h,
                                boxstyle="round,pad=0.02,rounding_size=0.20",
                                linewidth=1.0, edgecolor=border, facecolor=fill))
    ax.text(x + w / 2, y + h / 2, text, ha="center", va="center",
            fontsize=8.5, color=text_color, fontweight="bold")


def caption(ax, x, y, text, color=SECONDARY, size=8.5, italic=True):
    ax.text(x, y, text, fontsize=size, color=color,
            fontstyle=("italic" if italic else "normal"))


def header(ax, x, y, text, size=11):
    ax.text(x, y, text, fontsize=size, color=TEXT, fontweight="bold")


def delete_x(ax, x, y):
    ax.text(x, y, "×", fontsize=13, color=MUTED, ha="center", va="center")


def fig_mockup():
    fig, ax = plt.subplots(figsize=(13, 10), dpi=160)
    ax.set_xlim(0, 13)
    ax.set_ylim(0, 10)
    ax.axis("off")
    fig.patch.set_facecolor(BG)

    # Outer card
    ax.add_patch(FancyBboxPatch((0.2, 0.2), 12.6, 9.6,
                                boxstyle="round,pad=0.04,rounding_size=0.12",
                                linewidth=1.1, edgecolor=LINE, facecolor=SURFACE))

    # Title strip
    header(ax, 0.5, 9.3, "Filters", size=13)
    caption(ax, 0.5, 9.0,
            "Admin-curated filters appear first; user-added filters sit below under Advanced.",
            italic=True)

    # ── Curated chips row (what exists today) ─────────────────────────────
    caption(ax, 0.5, 8.6, "CURATED  (quick toggles configured by admin)",
            color=SECONDARY, italic=False, size=8)
    chip(ax, 0.5,  8.05, 2.2, 0.42, "Milestone: Funded × Applied")
    chip(ax, 2.85, 8.05, 1.8, 0.42, "Active only")
    chip(ax, 4.7,  8.05, 2.1, 0.42, "Loan Officer: Sarah J.")
    ax.add_patch(Rectangle((0.5, 7.7), 12.0, 0.02, facecolor=LINE, edgecolor="none"))

    # ── Advanced filters section ──────────────────────────────────────────
    caption(ax, 0.5, 7.4, "ADVANCED  (ad-hoc — user adds one row per filter)",
            color=SECONDARY, italic=False, size=8)

    def row(y, field, field_subtitle, op, value_label, value_text,
            sql, index_note, index_ok=True):
        # row background
        ax.add_patch(FancyBboxPatch((0.5, y), 12.0, 0.9,
                                    boxstyle="round,pad=0.02,rounding_size=0.08",
                                    linewidth=0.7, edgecolor=LINE, facecolor="#FBFCFD"))
        # Field picker
        field_box(ax, 0.65, y + 0.08, 2.5, 0.72, "Field", field)
        ax.text(0.73, y + 0.18, field_subtitle, fontsize=7, color=MUTED, fontstyle="italic")
        # Op picker
        field_box(ax, 3.3, y + 0.08, 1.8, 0.72, "Operator", op, value_color=PRIMARY_DARK)
        # Value
        field_box(ax, 5.25, y + 0.08, 3.8, 0.72, value_label, value_text)
        # Delete icon
        delete_x(ax, 9.3, y + 0.45)
        # SQL + index badge
        ax.text(9.55, y + 0.63, sql, fontsize=7.8, color=TEXT,
                fontfamily="monospace")
        index_color = ACCENT_GREEN if index_ok else "#D93025"
        index_prefix = "✓ idx" if index_ok else "⚠ idx"
        ax.text(9.55, y + 0.20,
                f"{index_prefix}  {index_note}",
                fontsize=7.5, color=index_color, fontweight="bold")

    row(6.25,
        "Created Date",
        "date · TZ-flagged",
        "Between",
        "From → To",
        "2024-04-01  →  2024-04-22",
        "col >= $1 AND col < $2   ($1,$2 = UTC-converted bounds)",
        "uses existing btree on col — no DB change")
    row(5.25,
        "Created Date",
        "date · TZ-flagged",
        "Relative",
        "Preset",
        "Yesterday",
        "col >= $1 AND col < $2   (yesterday PT → UTC range)",
        "uses existing btree on col — no DB change")
    row(4.25,
        "Status",
        "text · codeset-backed",
        "Is any of",
        "Values",
        "Funded  Applied  Pre-Qual",
        "col IN ($1, $2, $3)",
        "uses existing btree on col")
    row(3.25,
        "Loan Amount",
        "currency",
        "≥",
        "Value",
        "$100,000",
        "col >= $1",
        "uses existing btree on col")

    # Add-filter button
    chip(ax, 0.5, 2.70, 1.9, 0.42, "+  Add filter",
         fill=SURFACE, border=PRIMARY, text_color=PRIMARY)

    # ── Storage + emission note ───────────────────────────────────────────
    header(ax, 0.5, 2.25, "Storage shape (filters column on RPT_saved_reports)", size=10)
    ax.text(0.5, 1.95,
            "{ \"filters\": [ { \"field\":\"created_at\", \"op\":\"between\", \"values\":[\"2024-04-01\",\"2024-04-22\"] },",
            fontsize=8, color=TEXT, fontfamily="monospace")
    ax.text(0.5, 1.72,
            "              { \"field\":\"created_at\", \"op\":\"relative\", \"values\":[\"yesterday\"] },",
            fontsize=8, color=TEXT, fontfamily="monospace")
    ax.text(0.5, 1.49,
            "              { \"field\":\"status\",     \"op\":\"in_list\", \"values\":[\"Funded\",\"Applied\",\"Pre-Qual\"] },",
            fontsize=8, color=TEXT, fontfamily="monospace")
    ax.text(0.5, 1.26,
            "              { \"field\":\"loan_amount\",\"op\":\"gte\",     \"values\":[100000] } ] }",
            fontsize=8, color=TEXT, fontfamily="monospace")

    # Legend
    ax.text(0.5, 0.75, "INDEXING", fontsize=8, color=SECONDARY, fontweight="bold")
    ax.text(0.5, 0.50,
            "All filter operators keep the raw column on the LHS, so existing btree indexes are used as-is.",
            fontsize=8, color=ACCENT_GREEN)
    ax.text(0.5, 0.28,
            "Timezone math happens on the RHS (app-computed UTC bounds) — no tenant-DB schema changes required.",
            fontsize=8, color=ACCENT_GREEN)

    fig.tight_layout()
    fig.savefig(OUT, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)


if __name__ == "__main__":
    fig_mockup()
    print(f"Generated: {OUT} ({os.path.getsize(OUT) / 1024:.1f} KB)")
