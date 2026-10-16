# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

ChubbyBunniesLevels is the backend for **Chubby Bunnies**'s Level Editor: email/password sign-in and
save/load of level JSON files in Azure Blob storage (`Tests` and `Production` folders). It is the
sibling of `MochiSortLevels`, `MochiMixLevels` and `MochiDokuLevels` (`/Users/hagay-admin/Mochi-Labs/`),
and was copied from `MochiDokuLevels` (itself scaffolded from `MochiMixLevels.Api` at its storage+auth
stage). There is no level engine here (yet); only storage + auth.

## Projects (`ChubbyBunniesLevels.slnx`)

- **`ChubbyBunniesLevels.Api`** — ASP.NET Core minimal-API web service (`net10.0`). `GET /health`,
  Swagger UI (`/swagger`), auth API, level storage API. Config shape (`AzureBlob:*`, `ApiKey`/
  `ApiKeys`, `Auth:UsersPath`) matches the Mochi Sort / Mochi Mix APIs so the Level Editor client code
  can be pointed at it by swapping the base URL.

```bash
dotnet build ChubbyBunniesLevels.slnx
dotnet run --project ChubbyBunniesLevels.Api        # http://localhost:5173, Swagger at /swagger
```

Local dev needs `AzureBlob:ConnectionString` (e.g. in `appsettings.Development.json` or
`dotnet user-secrets`); without it storage endpoints return 503. Don't commit the connection string.

## Level storage API (`/api/storage/{folder}/...`)

`{folder}` is `tests` or `production` (case-insensitive) → `Tests/` / `Production/` blob prefixes in
the `chubbybunnies-levels` container.

- `GET /levels` — list level files. `GET /levels/{fileName}` — download raw JSON.
- `PUT /levels/{fileName}` — upload/overwrite (body = raw level JSON). **Production auto-bumps
  `Level.Ver`** and therefore requires a top-level `Level` object; Tests saves the body as-is.
  If Chubby Bunnies's level schema doesn't have `Level.Ver`, revisit `TryApplyProductionLevelVersion`.
- `DELETE /levels/{fileName}`; `GET /levels/zip` (optional inclusive `fromLevel`/`toLevel` on
  `Level_N.json` names).
- `GET /DownloadLevel`, `POST /DownloadLevels` (≤50) — return JSON only when server `Level.Ver` is
  newer than the client's version; cached in memory 5 min per file.

## Auth

Two separate mechanisms — don't conflate them:
- **`X-Api-Key`** (`Security/ApiKeyAuth.cs`) guards everything except `/health`, `/swagger`,
  `/api/auth`. If `ApiKey`/`ApiKeys` are empty, all endpoints are open (local dev only).
- **Email/password** (`POST /api/auth/login`, `POST /api/auth/verify`) — stateless, no session;
  users in `ChubbyBunniesLevels.Api/auth-allowlist.json` (`User`/`Admin` roles), hot-reloaded on file
  change. Passwords are plaintext, same tradeoff as the sibling repos. The list was copied from
  Mochi Doku/Mix/Sort (same team).

## Deployment

`.github/workflows/deploy-api.yml` — on push to `main` touching `ChubbyBunniesLevels.Api/**`, publishes
and deploys to the `chubbybunnies-api` Azure App Service (needs the `AZURE_CREDENTIALS` repo secret).

Azure resources (Tile Train subscription, `israelcentral`), provisioned 2026-10-16:
- Resource group `chubbybunnies-api-rg`: App Service plan `chubbybunnies-api-plan` (Linux, Free F1) and web app
  `chubbybunnies-api` (`DOTNETCORE|10.0`) → https://chubbybunnies-api.azurewebsites.net
- Blob container `chubbybunnies-levels` in the shared `tiletrainstorage` account
  (`TileTrainStorageResourceGroup`), with `Tests/.keep` / `Production/.keep` placeholders so the
  folders show in the portal (`.keep` isn't `.json`, so it never appears in level listings).
- App settings on the web app: `AzureBlob__ConnectionString`, `AzureBlob__ContainerName`,
  `AzureBlob__TestsPrefix`, `AzureBlob__ProductionPrefix`, `ApiKey` (its own key, not shared with the
  sibling APIs). Web app is HTTPS-only.
- Deploy credential: service principal `chubbybunnies-api-deploy`, Contributor scoped **only** to
  `chubbybunnies-api-rg` — its `--sdk-auth` JSON is the `AZURE_CREDENTIALS` GitHub secret.
