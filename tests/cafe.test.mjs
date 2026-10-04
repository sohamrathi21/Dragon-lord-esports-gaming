import { test } from 'node:test';
import assert from 'node:assert/strict';
import { seed, transact, charge, availableBalance } from '../lib/cafe.ts';
const now = Date.now();
test('vertical slice: customer, cash, session, receipt and revenue', () => {
 let s = transact(seed(now), 'customer', { name: 'Test Player', member: true }, 'Cashier', now);
 const id = s.customers.at(-1).id;
 s = transact(s, 'payment', { customerId: id, amount: 20000, key: 'cash-1' }, 'Cashier', now);
 s = transact(s, 'start', { customerId: id, stationId: 'PC-03', minutes: 60 }, 'Cashier', now);
 const session = s.sessions.at(-1);
 assert.equal(availableBalance(s, id), 10000);
 s = transact(s, 'end', { sessionId: session.id }, 'Cashier', now + 30 * 60000);
 assert.equal(s.customers.at(-1).balance, 15000);
 assert.equal(s.ledger[0].amount, -5000);
 assert.equal(s.ledger[0].kind, 'Session');
 assert.equal(s.stations[2].status, 'Available');
});
test('duplicate payment and duplicate session end do not double charge', () => {
 let s = seed(now); const request = { customerId: 'c0', amount: 10000, key: 'repeat' };
 s = transact(s, 'payment', request, 'Cashier', now); const balance = s.customers[0].balance;
 s = transact(s, 'payment', request, 'Cashier', now); assert.equal(s.customers[0].balance, balance);
 s = transact(s, 'end', { sessionId: 's0' }, 'Cashier', now); const after = s.customers[0].balance;
 s = transact(s, 'end', { sessionId: 's0' }, 'Cashier', now); assert.equal(s.customers[0].balance, after);
});
test('reservations reject overlap but accept touching intervals', () => {
 let s = seed(now); const b = { customerId: 'c0', stations: ['PC-03'], start: now + 60000, end: now + 3600000 };
 s = transact(s, 'booking', b, 'Cashier', now);
 assert.throws(() => transact(s, 'booking', b, 'Cashier', now), /overlap/);
 assert.doesNotThrow(() => transact(s, 'booking', { ...b, start: b.end, end: b.end + 60000 }, 'Cashier', now));
});
test('cashier cannot refund, adjust stock, compensate or change settings', () => {
 for (const action of ['refund', 'stock', 'compensation', 'settings', 'maintenance']) assert.throws(() => transact(seed(now), action, {}, 'Cashier'), /required/);
 assert.throws(() => transact(seed(now), 'settings', {}, 'Manager'), /owner/);
});
test('pause and recovery exclude interruption time and cap prepaid charge', () => {
 let s = seed(now); s = transact(s, 'pause', { sessionId: 's0' }, 'Manager', now);
 const frozen = charge(s.sessions[0], now); assert.equal(charge(s.sessions[0], now + 600000), frozen);
 s = transact(s, 'resume', { sessionId: 's0' }, 'Manager', now + 600000);
 assert.equal(charge(s.sessions[0], now + 600000), frozen);
 assert.equal(charge(s.sessions[0], now + 100000000), s.sessions[0].reserved);
});
test('outage blocks new authorization but lets an existing session end', () => {
 let s = transact(seed(now), 'network', {}, 'Owner', now);
 assert.throws(() => transact(s, 'start', { customerId: 'c0', stationId: 'PC-03', minutes: 60 }, 'Cashier', now), /online/);
 assert.throws(() => transact(s, 'extend', { sessionId: 's0', minutes: 30 }, 'Cashier', now), /online/);
 assert.doesNotThrow(() => transact(s, 'end', { sessionId: 's0' }, 'Cashier', now));
});
test('transfer preserves rate and refuses occupied station', () => {
 const s = seed(now); assert.throws(() => transact(s, 'transfer', { sessionId: 's0', stationId: 'PC-02' }, 'Cashier', now));
 const result = transact(s, 'transfer', { sessionId: 's0', stationId: 'PC-03' }, 'Cashier', now);
 assert.equal(result.sessions[0].rate, s.sessions[0].rate); assert.equal(result.stations[0].status, 'Available');
});
test('one minute rounds up in integer minor units', () => { const x = seed(now).sessions[0]; assert.equal(charge({ ...x, started: now, pausedMs: 0 }, now + 1), 167); });
test('order cancellation restores stock and refunds exactly once', () => {
 let s = seed(now); const before = s.products[0].stock; const balance = s.customers[0].balance;
 s = transact(s, 'order', { customerId: 'c0', stationId: 'PC-01', productId: 'p0', qty: 1 }, 'Cashier', now);
 const id = s.orders[0].id; s = transact(s, 'orderStatus', { id, status: 'Cancelled' }, 'Cashier', now);
 assert.equal(s.products[0].stock, before); assert.equal(s.customers[0].balance, balance);
 assert.throws(() => transact(s, 'orderStatus', { id, status: 'Cancelled' }, 'Cashier', now));
});
