namespace Pawfront.Application.Storage;

public static class DocumentationCatalog
{
    public static string? ResolveFileName(string category, bool providerHost) => category.ToLowerInvariant() switch
    {
        "terms-and-usage" => "Pawfront_Terms_of_Usage_and_Annexes_v19_DRAFT.docx",
        "provider-faq" when providerHost => "Pawfront_Provider_FAQs_v8.xlsx",
        "parent-faq" when !providerHost => "Pawfront_Parent_FAQs_v8.xlsx",
        "cookies-policy" => "Pawfront_Cookie_Policy_v2_DRAFT.docx",
        "privacy-policy" => "Pawfront_Privacy_Policy_v6_DRAFT.docx",
        "community-faq" => "Pawfront_Community_FAQs_v8.xlsx",
        _ => null
    };

    public static string ContentType(string fileName) => fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
        ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
}

public interface IDocumentationStorage
{
    Task<BlobDownload?> DownloadDocumentAsync(string fileName, CancellationToken cancellationToken);
}
