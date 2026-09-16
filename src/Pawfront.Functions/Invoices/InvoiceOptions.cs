namespace Pawfront.Functions.Invoices;

/// <summary>
/// Everything about invoicing that is a business decision rather than a fact
/// about a booking: who Littersoft is on paper, and the two rates the provider's
/// fee invoice quotes.
///
/// In configuration rather than constants because all of it changes without a
/// code change being the right vehicle — an address moves, and the launch offer
/// ending is a business date, not a deployment.
/// </summary>
public sealed class InvoiceOptions
{
    public const string SectionName = "Invoicing";

    public LittersoftIdentity Littersoft { get; init; } = new();

    /// <summary>
    /// Discount applied to the Pawfront fee on the provider's invoice. 100 while
    /// the launch offer runs, which is what makes "Total due" CHF 0.00 — set it to
    /// 0 to start actually billing the fee.
    /// </summary>
    public decimal LaunchDiscountPercentage { get; init; } = 100m;

    /// <summary>
    /// Swiss VAT on the Pawfront fee. Applied to the fee AFTER the discount, so it
    /// is CHF 0.00 while the offer runs. The rate is kept from the supplied
    /// template; the fee percentage itself is NOT — that comes from
    /// <c>Payments:PawfrontFeePercentage</c>, the same value the booking detail
    /// and the earnings figures use, so an invoice cannot quote a different
    /// commission from the app.
    /// </summary>
    public decimal MwstPercentage { get; init; } = 8.1m;
}

/// <summary>
/// Littersoft GmbH as it appears on the provider's invoice.
///
/// No UID / MWST number: none exists in the schema and none is configured here on
/// purpose — printing an invented tax identifier on a financial document is worse
/// than omitting the line. Add a property here when the real number is available.
/// </summary>
public sealed class LittersoftIdentity
{
    public string Name { get; init; } = "Littersoft GmbH";
    public string AddressLine { get; init; } = "Mühlackerstrasse 69";
    public string Zip { get; init; } = "8046";
    public string City { get; init; } = "Zürich, Switzerland";
    public string Email { get; init; } = "support@littersoft.com";
    public string Website { get; init; } = "www.littersoft.com";

    /// <summary>The issuer block in the page footer.</summary>
    public string FooterText =>
        $"{Name} · {AddressLine}, {Zip} {City}\n{Email} · {Website}";
}
