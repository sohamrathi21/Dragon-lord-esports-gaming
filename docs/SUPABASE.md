# Supabase PostgreSQL

The ASP.NET API uses PostgreSQL through Npgsql and can connect to Supabase. No Supabase service key or database password belongs in the frontend.

1. Create a Supabase project and copy the server database connection details from its Connect dialog.
2. Set the server's `ConnectionStrings__Cafe` to `Host=YOUR_HOST;Port=5432;Database=postgres;Username=YOUR_SERVER_USER;Password=YOUR_PASSWORD;SSL Mode=VerifyFull`.
3. Use the direct connection or session pooler for migrations and the API. Do not assume transaction pooling supports every migration operation.
4. Run `dotnet DragonLord.Api.dll --migrate`. Migrations include btree_gist and reservation exclusion constraints.
5. Migration 004 enables RLS and revokes table access from Supabase's browser roles. All business operations go through the authenticated ASP.NET API. The server connection must own the application tables; browser anon keys cannot access café data.
6. Provision a separate local venue PostgreSQL database for offline operation. A cloud Supabase database alone does not provide local outage resilience. Cross-database synchronization is not yet a production feature.

Status: compatibility and security migration implemented; a hosted Supabase project is not provisioned until account access and connection details are available.
