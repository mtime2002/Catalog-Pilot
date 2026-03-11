# Catalog Pilot File Structure Guide

## Goals
- Keep folder intent obvious at a glance.
- Support new catalog types without mixing unrelated logic.
- Keep navigation shallow (usually 2-3 levels deep).

## Current Structure (Organized by Intent)

```text
Components/                -> Blazor UI (pages, layout, routing)
Data/                      -> Local runtime data stores (db/json)
Models/
  Billing/                 -> Subscription and entitlement models
  Catalogs/
    Common/                -> Catalog-agnostic classification/lookup models
    VideoGames/            -> Video-game-specific models
  Identity/                -> User/account records
  Inventory/               -> Inventory records
  Marketplace/             -> Listing/pricing/market response models
  Media/                   -> Uploaded media models
Options/
  Billing/                 -> Billing configuration contracts
  Catalogs/
    Common/                -> Shared catalog integration options
    VideoGames/            -> Video-game classifier/catalog options
  Identity/                -> Auth configuration contracts
  Inventory/               -> Inventory store options
  Marketplace/             -> Marketplace/eBay options
Services/
  Billing/                 -> Billing implementations and interfaces
  Catalogs/
    Common/                -> Shared catalog lookup interfaces/services
    VideoGames/            -> Video-game catalog and classifier services
  Identity/                -> Auth/account services
  Inventory/               -> Inventory persistence services
  Marketplace/             -> Marketplace and listing services
  Media/                   -> Photo/media storage services
scripts/                   -> Python helpers (OCR/barcode)
wwwroot/                   -> Static assets
```

## Adding New Catalog Types
When adding `Clothing`, `CDs`, `Albums`, `Cards`, etc.:

1. Add type-specific folders under:
   - `Models/Catalogs/<CatalogType>/`
   - `Services/Catalogs/<CatalogType>/`
   - `Options/Catalogs/<CatalogType>/` (only when needed)
2. Keep shared cross-catalog logic in `Catalogs/Common`.
3. Avoid putting catalog-type-specific classes into root `Models/` or root `Services/`.

## Depth and Size Rules
- Target max depth: 3 levels from project root for app code.
- Target folder size: 5-15 files before splitting.
- Split by domain intent first, then by technical type only if necessary.

## Future Projects To Add (When Ready)
These can remain in one project for now, then split when complexity increases:

- `CatalogPilot.Domain` (entities/value objects/domain rules)
- `CatalogPilot.Application` (use-cases/orchestration)
- `CatalogPilot.Infrastructure` (SQLite/eBay/Stripe/external integrations)
- `CatalogPilot.Web` (Blazor UI + API endpoints)
- `CatalogPilot.Tests` (unit/integration tests)
- `CatalogPilot.Catalogs.<Type>` modules (optional plugin-style projects per catalog family)

