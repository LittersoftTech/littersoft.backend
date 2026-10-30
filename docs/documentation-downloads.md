# Documentation downloads

Both hosts expose `GET /api/v1/documents/{category}` using their existing Firebase authentication. Send `Authorization: Bearer <token>` for the relevant host. No request body is required.

| Category | Provider host | Parent host | File |
| --- | --- | --- | --- |
| `terms-and-usage` | Yes | Yes | Pawfront_Terms_of_Usage_and_Annexes_v19_DRAFT.docx |
| `provider-faq` | Yes | No | Pawfront_Provider_FAQs_v8.xlsx |
| `parent-faq` | No | Yes | Pawfront_Parent_FAQs_v8.xlsx |
| `cookies-policy` | Yes | Yes | Pawfront_Cookie_Policy_v2_DRAFT.docx |
| `privacy-policy` | Yes | Yes | Pawfront_Privacy_Policy_v6_DRAFT.docx |
| `community-faq` | Yes | Yes | Pawfront_Community_FAQs_v8.xlsx |

A successful response streams the file with the DOCX/XLSX content type and `Content-Disposition: attachment` including its original filename. Categories are case-insensitive. Unknown categories, FAQs unavailable on the current host, and missing blobs return 404 with no body.

Downloads use the existing blob-storage credentials and the `documentation` container in that storage account (production: `littersoftbs`). Clients cannot supply arbitrary URLs or filenames. Documentation is not added to the existing general-purpose blob read/delete allowlist.

Example: `GET https://<parent-host>/api/v1/documents/parent-faq`.

Validation: `dotnet run --project tests/Pawfront.Documentation.Checks` checks mapping, host restrictions, streamed bytes, MIME types, attachment filenames, and missing blobs using a fake storage service.
