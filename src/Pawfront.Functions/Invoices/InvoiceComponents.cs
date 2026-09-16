using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Pawfront.Functions.Invoices;

/// <summary>
/// The pieces both invoices share: the letterhead band, the yellow rule, the
/// two-column parties block and the footer.
///
/// They live here rather than in either document so the two cannot drift — the
/// whole point of the supplied templates being one design in two variants.
/// </summary>
internal static class InvoiceComponents
{
    /// <summary>
    /// The grey band: the Pawfront mark on the left, "Invoice" plus its reference
    /// and issue date on the right. Bleeds to the paper edge, which is why the
    /// page carries no margin of its own and the padding is applied here.
    /// </summary>
    public static void Letterhead(IContainer container, string invoiceNumber, DateTimeOffset issuedAtUtc)
    {
        container
            .Background(InvoiceTheme.Band)
            .PaddingHorizontal(InvoiceTheme.HeadPaddingX, Unit.Millimetre)
            .PaddingTop(InvoiceTheme.HeadPaddingTop, Unit.Millimetre)
            .PaddingBottom(InvoiceTheme.HeadPaddingBottom, Unit.Millimetre)
            .Row(row =>
            {
                row.RelativeItem().AlignTop().Height(InvoiceTheme.Px(34f)).Image(InvoiceTheme.Logo).FitHeight();

                row.RelativeItem().AlignRight().Column(column =>
                {
                    column.Item().AlignRight().Text("Invoice")
                        .FontSize(InvoiceTheme.DocTitleSize).Bold().FontColor(InvoiceTheme.Ink);

                    // Constrained to the block's own width and right-aligned, so
                    // the labels sit beside their values as the template's
                    // two-column `dl` does. Without a width the labels drift to the
                    // far left of the header's right half.
                    column.Item()
                        .PaddingTop(InvoiceTheme.Px(12f))
                        .AlignRight()
                        .Width(InvoiceTheme.Px(205f))
                        .Column(meta =>
                        {
                            MetaRow(meta, "INVOICE NO.", invoiceNumber);
                            MetaRow(meta, "ISSUED", InvoiceTheme.LongDate(issuedAtUtc));
                        });
                });
            });
    }

    private static void MetaRow(ColumnDescriptor column, string label, string value)
    {
        column.Item().PaddingBottom(InvoiceTheme.Px(3f)).Row(row =>
        {
            // Label left, value hard right, exactly the template's
            // `grid-template-columns: auto auto; justify-content: end`.
            row.AutoItem().PaddingRight(InvoiceTheme.Px(14f)).AlignBottom()
                .Text(label).Style(InvoiceTheme.LabelStyle);
            row.RelativeItem().AlignBottom().AlignRight().Text(value)
                .FontSize(InvoiceTheme.Px(10.5f)).SemiBold().FontColor(InvoiceTheme.Ink);
        });
    }

    /// <summary>The 3px yellow rule that separates an invoice from a statement at a glance.</summary>
    public static void AccentRule(IContainer container)
        => container.Height(InvoiceTheme.AccentHeight, Unit.Millimetre).Background(InvoiceTheme.Accent);

    /// <summary>
    /// "Invoice issued by" / "Invoice issued to", side by side. Each party is a
    /// name plus however many free lines the caller supplies; blank lines are
    /// dropped rather than left as gaps, which is what keeps the block tidy when a
    /// provider has no business address on file.
    /// </summary>
    public static void Parties(IContainer container, InvoiceParty issuedBy, InvoiceParty issuedTo)
    {
        container.Row(row =>
        {
            row.RelativeItem().Element(c => Party(c, "INVOICE ISSUED BY", issuedBy));
            row.ConstantItem(InvoiceTheme.Px(44f));
            row.RelativeItem().Element(c => Party(c, "INVOICE ISSUED TO", issuedTo));
        });
    }

    private static void Party(IContainer container, string label, InvoiceParty party)
    {
        container.Column(column =>
        {
            column.Item().Text(label).Style(InvoiceTheme.LabelStyle);

            column.Item().PaddingTop(InvoiceTheme.Px(8f)).Text(party.Name)
                .FontSize(InvoiceTheme.PartyNameSize).Bold().FontColor(InvoiceTheme.Ink);

            foreach (var line in party.Lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                column.Item().PaddingTop(InvoiceTheme.Px(3f)).Text(line)
                    .FontSize(InvoiceTheme.BodySize).FontColor(InvoiceTheme.Muted).LineHeight(1.45f);
            }
        });
    }

    /// <summary>An uppercase section heading, e.g. "SERVICE DETAILS".</summary>
    public static void SectionHeading(IContainer container, string text)
        => container.Text(text).Style(InvoiceTheme.LabelStyle).FontSize(InvoiceTheme.LabelSize);

    /// <summary>
    /// One label/amount line in a totals block — full content width, label hard
    /// left and amount hard right, so it lines up with the item table above it.
    /// </summary>
    public static void TotalsRow(
        ColumnDescriptor column,
        string label,
        string value,
        bool topRule = false,
        bool bold = false,
        string? labelSuffix = null)
    {
        var item = column.Item().PaddingVertical(InvoiceTheme.Px(7f));
        if (topRule)
        {
            item = item.BorderTop(1).BorderColor(InvoiceTheme.Rule).PaddingTop(InvoiceTheme.Px(7f));
        }

        item.Row(row =>
        {
            row.RelativeItem().Text(text =>
            {
                var span = text.Span(label).FontSize(InvoiceTheme.TotalsRowSize);
                span = bold ? span.SemiBold().FontColor(InvoiceTheme.Ink) : span.FontColor(InvoiceTheme.Muted);

                if (!string.IsNullOrWhiteSpace(labelSuffix))
                {
                    text.Span(" " + labelSuffix)
                        .FontSize(InvoiceTheme.TotalsRowSize).FontColor(InvoiceTheme.Label);
                }
            });

            var amount = row.AutoItem().Text(value)
                .FontSize(InvoiceTheme.TotalsRowSize)
                .FontColor(InvoiceTheme.Ink);

            if (bold)
            {
                amount.SemiBold();
            }
        });
    }

    /// <summary>
    /// The grand total: label and amount at the same weight and size, boxed by two
    /// heavy rules. Both templates render it this way.
    /// </summary>
    public static void GrandTotal(IContainer container, string label, string value)
    {
        container
            .PaddingTop(InvoiceTheme.Px(6f))
            .BorderTop(2).BorderBottom(2).BorderColor(InvoiceTheme.Ink)
            .PaddingVertical(InvoiceTheme.Px(14f))
            .Row(row =>
            {
                row.RelativeItem().AlignBottom().Text(label)
                    .FontSize(InvoiceTheme.TotalSize).Bold().FontColor(InvoiceTheme.Ink);
                row.AutoItem().AlignBottom().Text(value)
                    .FontSize(InvoiceTheme.TotalSize).Bold().FontColor(InvoiceTheme.Ink);
            });
    }

    /// <summary>
    /// The page foot: an optional issuer block on the left, the page marker and the
    /// no-signature note on the right.
    /// </summary>
    public static void Footer(IContainer container, string? leftText)
    {
        container
            .PaddingTop(InvoiceTheme.Px(14f))
            .BorderTop(1).BorderColor(InvoiceTheme.Rule)
            .PaddingTop(InvoiceTheme.Px(14f))
            .Row(row =>
            {
                row.RelativeItem().Text(leftText ?? string.Empty)
                    .FontSize(InvoiceTheme.FooterSize).FontColor(InvoiceTheme.Faint).LineHeight(1.65f);

                row.AutoItem().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(s => s
                        .FontSize(InvoiceTheme.FooterSize)
                        .FontColor(InvoiceTheme.Faint)
                        .LineHeight(1.65f));
                    text.AlignRight();
                    // Both documents are single-page by construction (one job, one
                    // line item), but the page numbers are live rather than a
                    // hardcoded "Page 1 of 1" so an unusually long address cannot
                    // produce a second sheet that lies about it.
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                    text.Line("");
                    text.Span("Computer-generated — no signature required.");
                });
            });
    }
}

/// <summary>A party on an invoice: a bold name plus free-form address lines.</summary>
internal sealed record InvoiceParty(string Name, IReadOnlyList<string?> Lines);
