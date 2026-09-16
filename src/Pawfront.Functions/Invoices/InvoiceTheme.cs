using System.Globalization;
using System.Reflection;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Pawfront.Functions.Invoices;

/// <summary>
/// The shared look of both invoices, lifted from the supplied HTML templates so
/// the two documents stay one design rather than drifting apart.
///
/// The values are the CSS ones verbatim — same greys, same yellow rule, same A4
/// band geometry — because the templates are the spec even though the rendering
/// is QuestPDF rather than a browser.
/// </summary>
internal static class InvoiceTheme
{
    // --- palette (straight from the templates' CSS) --------------------------
    public const string Ink = "#0f1115";        // body text
    public const string Muted = "#5c626c";      // secondary lines
    public const string Label = "#8b9099";      // uppercase section labels
    public const string Faint = "#9aa0a8";      // table headers, footer
    public const string Rule = "#e4e6ea";       // borders
    public const string HairRule = "#f4f5f7";   // row separators
    public const string Band = "#f1f3f5";       // letterhead background
    public const string Card = "#f7f8f9";       // notes / explainer card
    public const string Accent = "#ecaa00";     // the yellow rule under the letterhead

    // --- geometry (mm, matching @page A4 + the CSS padding) -----------------
    public const float PageMarginNone = 0f;
    public const float HeadPaddingX = 14f;
    public const float HeadPaddingTop = 13f;
    public const float HeadPaddingBottom = 12f;
    public const float BodyPaddingX = 14f;
    public const float BodyPaddingTop = 11f;
    public const float BodyPaddingBottom = 12f;
    public const float AccentHeight = 1.1f;     // the 3px rule, in mm

    /// <summary>
    /// CSS pixels to PDF points. The templates express type and spacing in px, and
    /// at the CSS reference resolution of 96 DPI one px is 0.75pt — so treating a
    /// px value as a pt value renders it 33% too large, which is enough to push a
    /// one-page invoice onto a second sheet. Every px-derived number below and in
    /// the two documents goes through this.
    ///
    /// The mm values above are NOT converted: @page and the band padding are
    /// already in millimetres.
    /// </summary>
    public static float Px(float px) => px * 0.75f;

    // --- type scale (the templates' px values, converted to pt) --------------
    public static readonly float DocTitleSize = Px(21f);
    public static readonly float TotalSize = Px(24f);
    public static readonly float PartyNameSize = Px(13f);
    public static readonly float BodySize = Px(10.5f);
    public static readonly float TotalsRowSize = Px(11f);
    public static readonly float LabelSize = Px(9f);
    public static readonly float TableHeaderSize = Px(8f);
    public static readonly float NoteSize = Px(8.5f);
    public static readonly float SubLineSize = Px(9f);
    public static readonly float FooterSize = Px(9f);

    /// <summary>
    /// The Pawfront mark, extracted verbatim from the two HTML templates (both
    /// carried the identical base64 PNG) and embedded so the renderer needs no
    /// network access — a Function fetching its own letterhead over HTTP would be
    /// a failure mode for no benefit.
    /// </summary>
    private static readonly Lazy<byte[]> LogoBytes = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("pawfront-logo.png", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The embedded Pawfront logo is missing from Pawfront.Functions. " +
                "Check the EmbeddedResource entry for Invoices/Assets/pawfront-logo.png.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    public static byte[] Logo => LogoBytes.Value;

    /// <summary>
    /// Swiss-format money, e.g. <c>CHF 1'234.50</c>. Invariant culture with an
    /// explicit apostrophe group separator, so the output cannot depend on the
    /// host's locale — a Function's culture is whatever the platform gives it.
    /// </summary>
    public static string Money(decimal value)
    {
        var format = new NumberFormatInfo
        {
            NumberDecimalSeparator = ".",
            NumberGroupSeparator = "’",
            NumberDecimalDigits = 2
        };

        return "CHF " + value.ToString("N2", format);
    }

    /// <summary>"25 Jul 2026" — the templates' long form, used on the letterhead.</summary>
    public static string LongDate(DateOnly date)
        => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string LongDate(DateTimeOffset value)
        => value.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"28 Jul" — the provider invoice's compact line-item date.</summary>
    public static string ShortDate(DateOnly date)
        => date.ToString("d MMM", CultureInfo.InvariantCulture);

    /// <summary>
    /// "08:30 – 12:30". Wall-clock, rendered verbatim: a booking's schedule is a
    /// reading the provider typed and the parent picked off a slot grid, not an
    /// instant — the same rule NotificationLocalTime settled on. An invoice must
    /// agree with the booking screen it describes.
    /// </summary>
    public static string TimeRange(TimeOnly start, TimeOnly end)
        => $"{start:HH\\:mm} – {end:HH\\:mm}";

    public static TextStyle LabelStyle => TextStyle.Default
        .FontSize(LabelSize).Bold().FontColor(Label).LetterSpacing(0.13f);

    public static TextStyle BodyStyle => TextStyle.Default
        .FontSize(BodySize).FontColor(Ink);

    public static TextStyle MutedStyle => TextStyle.Default
        .FontSize(BodySize).FontColor(Muted);

    public static TextStyle SubLineStyle => TextStyle.Default
        .FontSize(SubLineSize).FontColor(Faint);

    public static TextStyle TableHeaderStyle => TextStyle.Default
        .FontSize(TableHeaderSize).Bold().FontColor(Faint).LetterSpacing(0.12f);

    public static TextStyle NoteStyle => TextStyle.Default
        .FontSize(NoteSize).FontColor(Muted).LineHeight(1.65f);
}
