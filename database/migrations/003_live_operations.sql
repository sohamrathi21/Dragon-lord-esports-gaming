CREATE TABLE IF NOT EXISTS staff(id uuid PRIMARY KEY,venue_id uuid NOT NULL REFERENCES venues,email text NOT NULL UNIQUE,name text NOT NULL,password_hash text NOT NULL,role text NOT NULL CHECK(role IN('Owner','Manager','Cashier')));
CREATE TABLE IF NOT EXISTS waitlist(id uuid PRIMARY KEY,venue_id uuid NOT NULL REFERENCES venues,name text NOT NULL,size integer NOT NULL CHECK(size BETWEEN 1 AND 14));
ALTER TABLE venues ADD COLUMN IF NOT EXISTS warning_minutes integer NOT NULL DEFAULT 10 CHECK(warning_minutes BETWEEN 1 AND 60);
ALTER TABLE stations ADD COLUMN IF NOT EXISTS games text NOT NULL DEFAULT 'Valorant, Counter-Strike 2, Fortnite, Dota 2';
ALTER TABLE products ADD COLUMN IF NOT EXISTS category text NOT NULL DEFAULT 'Refreshments';
ALTER TABLE products ADD COLUMN IF NOT EXISTS emoji text NOT NULL DEFAULT '☕';
