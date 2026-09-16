using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Pawfront.Functions.Invoices;

/// <summary>
/// Line-item table styling, shared so the two invoices' tables look identical
/// even though their columns differ.
/// </summary>
internal static class InvoiceTable
{
    /// <summary>
    /// A column heading: small, uppercase, letterspaced, bottom-aligned over a
    /// hairline rule — the templates' <c>th</c>.
    /// </summary>
    public static void HeaderCell(TableCellDescriptor header, string text, bool right = false)
    {
        var cell = header.Cell()
            .BorderBottom(1).BorderColor(InvoiceTheme.Rule)
            .PaddingTop(InvoiceTheme.Px(10f)).PaddingBottom(InvoiceTheme.Px(6f)).PaddingRight(InvoiceTheme.Px(9f))
            .AlignBottom();

        if (right)
        {
            // The amount column is right-aligned and, being last, drops the right
            // padding so it sits flush with the content edge.
            cell = cell.PaddingRight(InvoiceTheme.Px(0f)).AlignRight();
        }

        cell.Text(text).Style(InvoiceTheme.TableHeaderStyle);
    }

    /// <summary>
    /// A body cell: hairline separator beneath, top-aligned.
    ///
    /// Returns the container rather than void so it binds to QuestPDF's
    /// <c>Element(Func&lt;IContainer, IContainer&gt;)</c> overload — the
    /// <c>Action</c> one returns void and cannot be chained onto.
    /// </summary>
    public static IContainer BodyCell(IContainer container)
        => container
            .BorderBottom(1).BorderColor(InvoiceTheme.HairRule)
            .PaddingVertical(InvoiceTheme.Px(9f)).PaddingRight(InvoiceTheme.Px(9f))
            .AlignTop();

    /// <summary>The compact variant the provider invoice's single job row uses.</summary>
    public static IContainer CompactCell(IContainer container)
        => container
            .BorderBottom(1).BorderColor(InvoiceTheme.HairRule)
            .PaddingVertical(InvoiceTheme.Px(6f)).PaddingRight(InvoiceTheme.Px(9f))
            .AlignMiddle();
}

/// <summary>
/// A short, human-quotable reference for a party — "PRV-1A2B3C4D".
///
/// The supplied templates show "Provider ID PRV-100482" and "Customer ID
/// PAR-204871", implying a sequential per-party number. NO SUCH COLUMN EXISTS:
/// providers and pet parents are identified only by GUID, and minting a sequence
/// for them would be a schema change well outside an invoicing feature. The first
/// eight hex characters of the GUID are stable, unique enough to quote over the
/// phone, and — unlike a made-up counter — actually resolve back to the row.
/// </summary>
internal static class ShortId
{
    public static string For(string prefix, Guid id)
        => $"{prefix}-{id.ToString("N")[..8].ToUpperInvariant()}";
}
