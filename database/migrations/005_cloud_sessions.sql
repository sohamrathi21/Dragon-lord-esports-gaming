-- Shared ASP.NET Data Protection key ring keeps staff cookies verifiable across container instances.
CREATE TABLE IF NOT EXISTS data_protection_keys(
 id bigserial PRIMARY KEY,
 friendly_name text NOT NULL,
 xml text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE public.data_protection_keys ENABLE ROW LEVEL SECURITY;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN REVOKE ALL ON public.data_protection_keys FROM anon; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN REVOKE ALL ON public.data_protection_keys FROM authenticated; END IF;
END $$;
