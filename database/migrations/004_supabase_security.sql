-- Compatible with local PostgreSQL and Supabase. Browser Data API access is denied.
-- ASP.NET connects using a server-only database owner credential over verified TLS.
DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['venues','customers','stations','sessions','ledger','commands','reservations','reservation_seats','products','orders','audit','outbox','inbox','devices','shifts','payment_orders','provider_events','automation_deliveries','handover_notes','incidents','staff','waitlist'] LOOP
  EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN EXECUTE format('REVOKE ALL ON public.%I FROM anon',t); END IF;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN EXECUTE format('REVOKE ALL ON public.%I FROM authenticated',t); END IF;
 END LOOP;
END $$;
