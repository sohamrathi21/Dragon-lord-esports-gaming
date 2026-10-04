-- Development data only. Never execute on a production database.
INSERT INTO venues(id,name,domain) VALUES('11111111-1111-1111-1111-111111111111','Dragon Lord Esports Gaming','dragonlord.gg') ON CONFLICT DO NOTHING;
INSERT INTO stations(id,venue_id,zone,hourly_minor)
SELECT 'PC-'||lpad(i::text,2,'0'),'11111111-1111-1111-1111-111111111111',CASE WHEN i<=8 THEN 'Standard' ELSE 'VIP' END,CASE WHEN i<=8 THEN 10000 ELSE 15000 END FROM generate_series(1,12) i ON CONFLICT DO NOTHING;
INSERT INTO stations(id,venue_id,zone,hourly_minor) VALUES('PS5-1','11111111-1111-1111-1111-111111111111','Console',20000),('PS5-2','11111111-1111-1111-1111-111111111111','Console',20000) ON CONFLICT DO NOTHING;
INSERT INTO customers(id,venue_id,name,email,member,balance_minor) VALUES('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111','Arjun Mehta','arjun@example.com',true,100000) ON CONFLICT DO NOTHING;
INSERT INTO ledger(id,venue_id,customer_id,kind,amount_minor,reference,actor) VALUES('33333333-3333-3333-3333-333333333333','11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','Payment',100000,'demo-opening','Demo seed') ON CONFLICT DO NOTHING;
INSERT INTO products(id,venue_id,name,price_minor,stock) VALUES('44444444-4444-4444-4444-444444444444','11111111-1111-1111-1111-111111111111','Red Bull',15000,24) ON CONFLICT DO NOTHING;
