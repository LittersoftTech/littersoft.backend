"""Regenerate docs/Pawfront.All.postman_collection.json from the live hosts.

Usage:
    # 1. start the three hosts, then capture their OpenAPI documents into docs/:
    curl -s http://localhost:5051/openapi/v1.json -o docs/openapi.provider.json
    curl -s http://localhost:5052/openapi/v1.json -o docs/openapi.parent.json
    curl -s http://localhost:5053/openapi/v1.json -o docs/openapi.chat.json
    # 2. regenerate:
    python docs/generate_postman_collection.py

Re-run after adding or changing any endpoint. This is why the collection does not have
to be hand-maintained, which is how the previous three drifted out of date.
"""

import io
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))

SCRATCH = os.environ.get("PAWFRONT_OPENAPI_DIR", HERE)
OUT = os.path.join(HERE, "Pawfront.All.postman_collection.json")
ENV_OUT = os.path.join(HERE, "Pawfront.All.postman_environment.json")

# Route ids that have a natural collection variable. Anything not here becomes a
# Postman :pathVariable, which shows up as an obvious blank rather than silently
# hitting a wrong resource.
ID_VARS = {
    "providerId", "petParentId", "petId", "bookingId", "nightStayBookingId",
    "eventId", "eventBookingId", "serviceId", "closureId", "photoId", "ticketId",
    "conversationId", "messageId", "chatBlockId", "otpId", "parentMobileOtpId",
    "providerMobileOtpId", "bookingReviewId",
}

FOLDER_TITLES = {
    "HealthEndpoints": "Health",
    "MetadataEndpoints": "Metadata",
    "ProviderOnboardingEndpoints": "Provider Onboarding",
    "ParentOnboardingEndpoints": "Parent Onboarding",
    "DeviceTokenEndpoints": "Device Tokens (push)",
    "ProviderEndpoints": "Providers (legacy in-memory)",
    "ProviderAccountEndpoints": "Provider Account (delete)",
    "ProviderPolicyEndpoints": "Provider Policy",
    "ProviderActiveStatusEndpoints": "Provider Active Status",
    "ProviderAvailabilityEndpoints": "Provider Availability & Slots",
    "ProviderClosureEndpoints": "Provider Closures",
    "ProviderPhotoEndpoints": "Provider Photos",
    "ProviderBannerImageEndpoints": "Provider Banner Image",
    "ProviderServiceBannerEndpoints": "Per-Service Banner",
    "ProviderServiceCatalogEndpoints": "Provider Services Catalog",
    "ProviderEarningsEndpoints": "Earnings (provider)",
    "ParentSpendEndpoints": "Spend & History (parent)",
    "PetSitterEndpoints": "Service - Pet Sitter",
    "PetGroomerEndpoints": "Service - Pet Groomer",
    "PetTrainerEndpoints": "Service - Pet Trainer",
    "PetAdoptionSaleEndpoints": "Service - Pet Adoption & Sale",
    "VetEndpoints": "Service - Vet",
    "ImageUploadEndpoints": "Image Uploads",
    "BookingEndpoints": "Bookings",
    "NightStayBookingEndpoints": "Night-Stay Bookings",
    "PetParentEndpoints": "Pet Parents & Pets",
    "PetParentLookupEndpoints": "Pet Parent & Pet Lookup",
    "ReviewEndpoints": "Reviews & Ratings",
    "SupportTicketEndpoints": "Support Tickets (Report Incident / Report Chat)",
    "EventEndpoints": "Events",
    "EventBookingEndpoints": "Event Ticket Bookings",
    "EventDashboardEndpoints": "Event Organiser Dashboard",
    "BlobImageEndpoints": "Blob Images",
    "AvailabilitySlotsEndpoints": "Provider Availability Slots",
    "ProviderAgendaEndpoints": "Provider Daily Agenda",
    "ProviderDetailsEndpoints": "Provider Public Profile",
    "ProviderSearchEndpoints": "Provider Discovery & Search",
    "ChatIdentityEndpoints": "Identity (/me)",
    "ConversationEndpoints": "Conversations",
    "ChatMessageEndpoints": "Messages",
    "ChatAttachmentEndpoints": "Attachments",
    "ChatBlockEndpoints": "Blocks",
}

# Sample values by property name. Keeps a generated request runnable-looking instead
# of every string being "string".
BY_NAME = {
    "fcmToken": "fcm_dummy_token_abc123XYZ",
    "deviceId": "device-uuid-9f1c-aa11-bb22",
    "devicePlatform": "Android",
    "firstName": "Alex", "lastName": "Meier",
    "gender": "Male",
    "mobileCountryCode": "+41", "mobileNumber": "791234567",
    "addressLine": "Bahnhofstrasse 12", "city": "Zurich", "zipCode": "8001",
    "latitude": 47.3769, "longitude": 8.5417,
    "description": "Short free-text description.",
    "email": "test@example.com",
    "bookerName": "Alex Meier", "bookerEmail": "test@example.com",
    "bookerMobile": "+41791234567",
    "paymentMethod": "Cash", "payoutMethods": ["Cash"],
    "otpCode": "123456",
    "petType": "Dog", "petName": "Bruno", "breed": "Labrador", "weight": 24.5,
    "microchipId": "985112345678903",
    "vaccinationStatus": "Vaccinated", "sterilizationStatus": "Sterilized",
    "temperament": "Friendly",
    "bookingType": "SingleDay",
    "locationType": "ParentLocation",
    "status": "CONFIRMED",
    "note": "Optional free-text note.",
    "reason": "Short reason, recorded on the severance row.",
    "comment": "What happened, in your own words.",
    "reply": "Answering support's question.",
    "rating": 5,
    "title": "Puppy Social Meetup",
    "isPaid": False, "price": 25.0,
    "serviceItemCode": "BathAndDry",
    "durationHours": 2, "granularityMinutes": 30,
    "blobUrl": "https://<account>.blob.core.windows.net/provider-images/...",
    "text": "Hello!",
    "clientMessageId": "11111111-1111-4111-8111-111111111111",
    "kind": "Text",
    "upToSequence": 1,
    "counterpartyId": "{{counterpartyId}}",
    "isActive": True,
    "acknowledgeExistingBookings": False,
    "acknowledgeTermsChanges": False,
    # Server-validated enums — a literal "string" here is a guaranteed 400.
    "identityType": "Passport",
    "reviewerType": "Parent",
    "raisedByType": "PetParent",
    "ticketType": "BookingIncident",
    "eventType": "Physical",
    "eventCategory": "PetTraining",
    "cancellationPolicy": "NoRefund",
    "paymentStatus": "Paid",
    "serviceLocation": "ParentsPlace",
    "providerType": "PetSitter",
    "sortBy": "Date",
    "sortDirection": "Desc",
    "period": "Monthly",
}


def sample(schema, spec, name=None, depth=0, seen=None):
    """Build an example value for a schema. $refs are resolved against components."""
    seen = seen or set()
    if not isinstance(schema, dict):
        return None
    if "$ref" in schema:
        ref = schema["$ref"]
        if ref in seen or depth > 6:
            return {}
        seen = seen | {ref}
        target = spec
        for part in ref.lstrip("#/").split("/"):
            target = target.get(part, {})
        return sample(target, spec, name, depth + 1, seen)

    # A nullable body parameter is emitted as oneOf[{type:null}, {$ref:...}] rather
    # than a bare $ref. Without this the whole body collapsed to the default
    # "string" — which is how POST bulk-cancel came out as a quoted string.
    for combinator in ("oneOf", "anyOf"):
        if combinator in schema:
            for branch in schema[combinator]:
                if isinstance(branch, dict) and branch.get("type") != "null":
                    return sample(branch, spec, name, depth + 1, seen)
            return None
    if "allOf" in schema:
        merged = {}
        for branch in schema["allOf"]:
            got = sample(branch, spec, name, depth + 1, seen)
            if isinstance(got, dict):
                merged.update(got)
        return merged

    if name in BY_NAME and "properties" not in schema:
        return BY_NAME[name]

    types = schema.get("type")
    if isinstance(types, list):
        types = [t for t in types if t != "null"]
        types = types[0] if types else "string"

    if "enum" in schema and schema["enum"]:
        return schema["enum"][0]

    if types == "object" or "properties" in schema:
        out = {}
        for prop, sub in (schema.get("properties") or {}).items():
            out[prop] = sample(sub, spec, prop, depth + 1, seen)
        return out
    if types == "array":
        item = sample(schema.get("items", {}), spec, name, depth + 1, seen)
        return [item] if item is not None else []
    if types == "boolean":
        return False
    if types in ("integer", "number"):
        return 0
    # string
    fmt = schema.get("format")
    if fmt == "uuid":
        return "00000000-0000-0000-0000-000000000000"
    if fmt == "date":
        return "2026-09-01"
    if fmt == "date-time":
        return "2026-09-01T10:00:00Z"
    if fmt in ("time", "time-span"):
        return "10:00:00"
    return "string"


# Some endpoints are tagged with the ASSEMBLY name rather than an endpoint class —
# the whole chat host, plus health/metadata/blob-images and the image uploads and
# event counters on the two CRUD hosts. Those would collapse into one folder, so the
# folder is derived from the path instead. Ordered: first match wins.
PATH_FOLDERS = [
    ("/health", "Health"),
    ("/metadata", "Metadata"),
    ("/blob-images", "Blob Images"),
    ("/me", "Identity (/me)"),
    ("/conversations/{conversationId}/messages", "Messages"),
    ("/conversations/{conversationId}/attachments", "Attachments"),
    ("/conversations/{conversationId}/bookings", "Thread Jobs (View Jobs)"),
    ("/conversations/{conversationId}/read", "Read State"),
    ("/conversations", "Conversations"),
    ("/blocks", "Blocks"),
    ("/events/{eventId}/views", "Event Engagement Counters"),
    ("/events/{eventId}/shares", "Event Engagement Counters"),
    ("/events/{eventId}/inquiries", "Event Engagement Counters"),
    ("/services/pet-sitter", "Service - Pet Sitter"),
    ("/services/pet-groomer", "Service - Pet Groomer"),
    ("/services/pet-trainer", "Service - Pet Trainer"),
    ("/services/pet-adoption-sale", "Service - Pet Adoption & Sale"),
    ("/services/vet", "Service - Vet"),
]


def collect_form_properties(schema, spec, depth=0):
    """Flatten a multipart schema's properties.

    A handler taking `IFormFile file` plus a `[FromForm] string` emits the two as
    separate `allOf` branches rather than one property bag, so reading only the
    top-level `properties` loses every non-file field — POST /pet-parents/{id}/identity
    came out with no fields at all.
    """
    out = {}
    if not isinstance(schema, dict) or depth > 6:
        return out
    if "$ref" in schema:
        target = spec
        for part in schema["$ref"].lstrip("#/").split("/"):
            target = target.get(part, {})
        return collect_form_properties(target, spec, depth + 1)
    for key in ("allOf", "oneOf", "anyOf"):
        for branch in schema.get(key, []) or []:
            out.update(collect_form_properties(branch, spec, depth + 1))
    out.update(schema.get("properties") or {})
    return out


def _seg_match(prefix, path):
    """Segment-aware containment. A plain `prefix in path` matches `/me` inside
    `/messages`, which silently swallowed the whole Messages folder."""
    return path == prefix or path.endswith(prefix) or (prefix + "/") in path


def resolve_folder(tag, path):
    """Prefer the endpoint-class tag; fall back to the path when it is the assembly."""
    if tag and not tag.startswith("Pawfront."):
        return FOLDER_TITLES.get(tag, tag)
    for prefix, folder in PATH_FOLDERS:
        if _seg_match(prefix, path):
            return folder
    return "Other"


def path_segments(path, host_key):
    """URL path -> Postman path array, binding well-known ids to variables."""
    segs, vars_used = [], []
    for raw in path.strip("/").split("/"):
        m = re.fullmatch(r"\{(.+)\}", raw)
        if not m:
            segs.append(raw)
            continue
        pname = m.group(1)
        if pname in ID_VARS:
            segs.append("{{%s}}" % pname)
        else:
            segs.append(":" + pname)
            vars_used.append(pname)
    return segs, vars_used


def build(spec, host_key, base_var, token_var):
    folders = {}
    methods = ("get", "post", "put", "patch", "delete")

    for path, item in sorted(spec.get("paths", {}).items()):
        for method in methods:
            op = item.get(method)
            if not isinstance(op, dict):
                continue

            tag = (op.get("tags") or [""])[0]
            folder = resolve_folder(tag, path)

            params = op.get("parameters") or []
            query = [p for p in params if p.get("in") == "query"]

            segs, blank_vars = path_segments(path, host_key)
            raw = "{{%s}}/%s" % (base_var, "/".join(segs))

            url = {"raw": raw, "host": ["{{%s}}" % base_var], "path": segs}
            if query:
                qs = []
                for p in query:
                    val = BY_NAME.get(p["name"], "")
                    qs.append({
                        "key": p["name"],
                        "value": "" if val in (None, "") else str(val),
                        # Disabled so a GET runs unfiltered by default; tick the ones
                        # you want in the Postman UI.
                        "disabled": True,
                    })
                url["query"] = qs
                url["raw"] = raw + "?" + "&".join("%s=" % p["name"] for p in query)
            if blank_vars:
                url["variable"] = [{"key": v, "value": ""} for v in blank_vars]

            request = {"method": method.upper(), "header": [], "url": url}

            body = op.get("requestBody") or {}
            content = body.get("content") or {}
            if "application/json" in content:
                schema = content["application/json"].get("schema", {})
                example = sample(schema, spec)
                request["header"].append({"key": "Content-Type", "value": "application/json"})
                request["body"] = {
                    "mode": "raw",
                    "raw": json.dumps(example, indent=2, ensure_ascii=False),
                    "options": {"raw": {"language": "json"}},
                }
            elif "multipart/form-data" in content:
                schema = content["multipart/form-data"].get("schema", {})
                fields = []
                for prop, sub in collect_form_properties(schema, spec).items():
                    is_file = "IFormFile" in json.dumps(sub)
                    if is_file:
                        fields.append({"key": prop, "type": "file", "src": []})
                    else:
                        fields.append({
                            "key": prop, "type": "text",
                            "value": str(sample(sub, spec, prop) or ""),
                        })
                request["body"] = {"mode": "formdata", "formdata": fields}

            name = "%s %s" % (method.upper(), path.replace("/api/v1", ""))
            folders.setdefault(folder, []).append({"name": name, "request": request})

    return [
        {"name": f, "item": sorted(items, key=lambda r: r["name"])}
        for f, items in sorted(folders.items())
    ]


def main():
    hosts = [
        ("provider", "Provider host (Pawfront.Api)", "providerBaseUrl", "providerToken",
         "Firebase project `littersoftprovider`. Requires the `FirebaseUser` policy."),
        ("parent", "Pet Parent host (Pawfront.PetParentApi)", "parentBaseUrl", "parentToken",
         "Separate Firebase project. Requires the `PetParentUser` policy; every "
         "`/pet-parents/{petParentId}` and `/pets/{petId}` route is ownership-filtered "
         "from the JWT."),
        ("chat", "Chat host (Pawfront.ChatApi)", "chatBaseUrl", "chatToken",
         "Accepts BOTH Firebase projects behind one `ChatUser` policy - paste either a "
         "provider or a parent token. The SignalR hub at `/hubs/chat` is not an HTTP "
         "endpoint and cannot be exercised from Postman; it takes the same token via "
         "`?access_token=`."),
    ]

    top = []
    total = 0
    for key, title, base_var, token_var, note in hosts:
        spec = json.load(io.open(os.path.join(SCRATCH, "openapi.%s.json" % key), encoding="utf-8"))
        items = build(spec, key, base_var, token_var)
        count = sum(len(f["item"]) for f in items)
        total += count
        top.append({
            "name": "%s  (%d requests)" % (title, count),
            "description": note,
            "item": items,
            # Auth is set per HOST folder, not per request: the three hosts take
            # different tokens, and a single collection-level bearer would silently
            # send a provider token to the parent API.
            "auth": {"type": "bearer", "bearer": [{"key": "token", "value": "{{%s}}" % token_var, "type": "string"}]},
        })

    collection = {
        "info": {
            "name": "Pawfront - All Hosts (Provider, Parent, Chat)",
            "description": (
                "Every HTTP endpoint across the three Pawfront hosts, generated from each "
                "host's live OpenAPI document.\n\n"
                "**Envelope.** Every response is "
                "`{ \"success\": bool, \"data\": T|null, \"error\": { \"code\", \"message\" }|null }`. "
                "Error codes are per-endpoint; see CLAUDE.md for the full map.\n\n"
                "**Auth.** Each host folder carries its own bearer token variable "
                "(`providerToken` / `parentToken` / `chatToken`) because the hosts validate "
                "different Firebase projects. Get a token with the Firebase "
                "`signInWithPassword` REST call - see the snippet in CLAUDE.md.\n\n"
                "**Ids.** Well-known route ids are bound to collection variables "
                "(`{{providerId}}`, `{{bookingId}}`, `{{ticketId}}`, ...). Set them once in the "
                "collection or environment. Anything else appears as a Postman `:pathVariable` "
                "to fill in per request.\n\n"
                "**Query params are disabled by default** so a GET runs unfiltered; tick the "
                "ones you want.\n\n"
                "**Bodies** are generated from the request DTO schemas with plausible sample "
                "values - check enums and ids before sending. Responses are not described: "
                "these minimal-API endpoints declare no `ProducesResponseType`, so the OpenAPI "
                "document carries only bare 200s."
            ),
            "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
        },
        "item": top,
        "variable": [
            {"key": "providerBaseUrl", "value": "http://localhost:5051"},
            {"key": "parentBaseUrl", "value": "http://localhost:5052"},
            {"key": "chatBaseUrl", "value": "http://localhost:5053"},
            {"key": "providerToken", "value": "PASTE_PROVIDER_FIREBASE_ID_TOKEN"},
            {"key": "parentToken", "value": "PASTE_PARENT_FIREBASE_ID_TOKEN"},
            {"key": "chatToken", "value": "PASTE_EITHER_FIREBASE_ID_TOKEN"},
        ] + [{"key": v, "value": ""} for v in sorted(ID_VARS)] + [
            {"key": "counterpartyId", "value": "", "description": "The other party in a chat thread."},
        ],
    }

    io.open(OUT, "w", encoding="utf-8").write(json.dumps(collection, indent=2, ensure_ascii=False))

    env = {
        "id": "e2e20000-0000-4000-8000-000000000003",
        "name": "Pawfront - All Hosts (local dev)",
        "_postman_variable_scope": "environment",
        "values": [
            {"key": "providerBaseUrl", "value": "http://localhost:5051", "enabled": True},
            {"key": "parentBaseUrl", "value": "http://localhost:5052", "enabled": True},
            {"key": "chatBaseUrl", "value": "http://localhost:5053", "enabled": True},
            {"key": "providerToken", "value": "", "type": "secret", "enabled": True},
            {"key": "parentToken", "value": "", "type": "secret", "enabled": True},
            {"key": "chatToken", "value": "", "type": "secret", "enabled": True},
        ] + [{"key": v, "value": "", "enabled": True} for v in sorted(ID_VARS)],
    }
    io.open(ENV_OUT, "w", encoding="utf-8").write(json.dumps(env, indent=2, ensure_ascii=False))

    print("wrote %s" % OUT)
    for f in top:
        print("  %-55s %d folders" % (f["name"], len(f["item"])))
    print("TOTAL requests: %d" % total)


main()
