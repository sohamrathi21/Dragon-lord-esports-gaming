# Cloud backend configuration

Vercel routes `/api/*` and `/hubs/*` to the ASP.NET Core service and serves all other URLs from the existing React app. Both routes use the current app origin, so its SameSite=Strict HttpOnly staff cookie works without cross-origin CORS.

Set the Vercel **Production** environment variable `ConnectionStrings__Cafe` to the Supabase PostgreSQL URI before deploying the API. Use Supabase Database → Connect → **Session pooler** for Vercel's IPv4 network (port 5432), with SSL certificate verification enabled. Configure `Runtime__Mode=Cloud`; this deliberately keeps active session and reservation commands disabled until a venue controller acknowledges them. The ASP.NET service applies numbered SQL migrations at startup, serialized with a PostgreSQL advisory lock. An app-level PostgreSQL pool keeps function connection counts bounded; start with `MaxPoolSize=5` in the connection string.

The API stores the ASP.NET data-protection key ring in the private `data_protection_keys` PostgreSQL table so staff cookies work across container instances. The table has RLS enabled and client roles revoked. Keep the PostgreSQL credential server-only. Set a durable `Razorpay__KeyId`, `Razorpay__KeySecret` and `Razorpay__WebhookSecret` as Vercel environment variables to enable hosted payments; never put them in VITE variables or in Git. Leave `Razorpay__AllowLive=false` while configuring and test the signed webhook first.

Before opening staff sign-up to the public, create the owner account. The startup database is empty by design. Deploy behind Vercel Deployment Protection for initial owner registration; create the owner, verify sign-in, then remove protection when ready. No production users or payments are generated automatically.

Vercel Services run ASP.NET in request-driven containers and do not provide a durable local controller, physical station locking or reliable background timer. Keep the Windows venue controller online for session ownership and outage continuity. Signals from the production controller are a remaining integration contract; until it's deployed, session start, reservations and timer-based session expiry must be handled at the venue.
