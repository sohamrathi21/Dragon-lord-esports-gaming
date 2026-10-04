# Dragon Lord Esports Gaming

React/TypeScript gaming café dashboard with an ASP.NET Core 8 API, PostgreSQL migrations and SignalR updates. Branding, pricing and venue settings are configurable. Live mode is the default; `?demo=1` explicitly opens the isolated browser demo with 12 PCs, 2 consoles and sample data.

## Run locally

Install Node.js 24, .NET SDK 8 and PostgreSQL 16. Copy `.env.example` values into server environment settings; never commit secrets. `docker compose up -d` starts PostgreSQL when Docker is available.

1. `npm ci`
2. Set `ConnectionStrings__Cafe` to your PostgreSQL connection string and `Runtime__Mode=VenueController`.
3. `dotnet run --project services/DragonLord.Api -- --migrate`
4. Set `ASPNETCORE_ENVIRONMENT=Development` and `ASPNETCORE_URLS=http://127.0.0.1:5080`, then run `dotnet run --project services/DragonLord.Api`.
5. Run `npm run dev` and open http://127.0.0.1:5173. Create your first owner account using a new password. Live setup creates the venue and 14 stations with empty customer and payment records.

This workspace also supports an ignored `.runtime/backend-config.json` containing `{"ConnectionStrings":{"Cafe":"YOUR_CONNECTION"},"Runtime":{"Mode":"VenueController"}}`; `scripts/start-local.ps1` loads it. Environment templates do not contain working credentials.

## Implemented

Staff authentication with owner, manager and cashier permissions; customer profiles; cash payments; prepaid session reservations; server-calculated integer billing; start, pause, resume, extend, transfer and end; idempotent commands and wallet ledger; receipts; reservation overlap prevention; stock-linked orders; refunds and compensation reasons; reports and CSV; handover and incident records; cashier reconciliation; live SignalR notifications; Razorpay order creation and captured-payment verification; n8n event outbox with retries.

## Verify

`npm run typecheck`, `npm test`, `npm run build`, `dotnet test services/DragonLord.Tests`. API integration tests require a separate migrated test database and API on port 5081: `node tests/backend.integration.mjs`. Never run these mutation tests against a business database.

## Deploy

Vercel builds the frontend using `vercel.json`. Host ASP.NET separately using `services/DragonLord.Api/Dockerfile`; route `/api` and `/hubs` to it under the same public HTTPS origin so authentication cookies and WebSockets work. Initialize the owner privately before exposing the API. Persist ASP.NET Data Protection keys, configure trusted reverse proxies, backups, TLS and server secrets. Vercel alone does not run the Windows service or ASP.NET venue controller.

Supabase can host the cloud PostgreSQL database: see [Supabase setup](docs/SUPABASE.md). Apply every database migration with the API's `--migrate` command. Keep database credentials on the server.

For Razorpay, set server key ID, key secret and webhook secret, configure the webhook endpoint `/api/payments/razorpay/webhook`, and enable live mode only after your account is activated and test payments pass. No gaming-platform passwords are collected.

n8n is installed locally in the ignored runtime directory. `scripts/start-n8n.ps1` starts it. Import `automations/cafe-events.json`, configure Header Auth with `X-Dragon-Token` matching `N8n__HeaderSecret`, then publish the workflow. This workflow validates and acknowledges events; external notifications need separately configured destinations and duplicate-event handling.

## Production work remaining

Independent customer authentication and booking checkout; reservation deposits and no-show fees; signed Windows agent installation and update pipeline; restricted OS accounts and device policies; authenticated device/controller synchronization and outage recovery between local and cloud databases; payment-provider end-to-end testing; external API hosting, backups and operational monitoring. Agent and controller projects are clearly labelled simulators and do not lock PCs or consoles. Browser launcher previews do not launch installed games.

Detailed contracts and limitations are in [integrations](docs/INTEGRATIONS.md). Forecasting, tournaments and AI remain later extensions.

## Windows download

Public `/download` and the workspace navigation show Windows release details. Configure `VITE_WINDOWS_INSTALLER_URL` with the HTTPS URL of a real `.exe` or `.msi`, plus `VITE_WINDOWS_APP_VERSION`, `VITE_WINDOWS_DOWNLOAD_SIZE` and `VITE_WINDOWS_SUPPORTED_VERSIONS`, then rebuild. Until all release fields are present, the button shows Coming soon. Downloads begin only on click; users start installation themselves. No installer has been published.
