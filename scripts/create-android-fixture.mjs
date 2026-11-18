import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync, copyFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';

const sourceRoot = resolve(process.argv[2] ?? '../raad-inventory');
const output = resolve(process.argv[3] ?? 'artifacts/fixtures');
mkdirSync(output, { recursive: true });
const { LedgerStore } = await import(pathToFileURL(join(sourceRoot, 'src/data/ledger.ts')).href);
const path = join(output, `android-v8-${randomUUID()}.db`);
const db = new DatabaseSync(path);
const port = {
  exec: async sql => { db.exec(sql); },
  run: async (sql, ...params) => { const result = db.prepare(sql).run(...params); return { changes: Number(result.changes) }; },
  get: async (sql, ...params) => db.prepare(sql).get(...params) ?? null,
  all: async (sql, ...params) => db.prepare(sql).all(...params),
};
const store = new LedgerStore(port, { id: randomUUID, now: () => new Date('2026-11-18T10:00:00Z') });
await store.init();
const customer = await store.createCustomer({ operationKey: randomUUID(), name: 'عميل تجريبي أحمد', phone: '+20 100 000 0000' });
const products = [];
for (let i = 1; i <= 12; i++) products.push(await store.saveProduct({ operationKey: randomUUID(), name: `منتج ${i.toString().padStart(3, '0')} — تيل فرامل R3D`, price: '125.05', openingQuantity: '1000', threshold: '10' }));
async function sale(index, initialPayment) {
  const draft = await store.startDraft();
  await store.addDraftProducts(draft.id, [products[index].id, products[index + 1].id]);
  await store.updateDraftLine(draft.id, products[index].id, { quantity: '2' });
  await store.updateCheckout(draft.id, { customerId: customer.id, initialPayment });
  const invoice = await store.issueInvoice(draft.id); await store.acknowledgeIssue(invoice.id); return invoice;
}
const invoice = await sale(0, '100.00');
await store.recordPayment({ operationKey: randomUUID(), invoiceId: invoice.id, amount: '50.00' });
const payments = await store.listPayments(invoice.id);
await store.correctPayment({ operationKey: randomUUID(), invoiceId: invoice.id, paymentId: payments.items[0].id, replacement: '25.00', reason: 'تصحيح اختبار' });
const cancelled = await sale(3, '0');
await store.cancelInvoice({ operationKey: randomUUID(), invoiceId: cancelled.id });
const deleted = await sale(6, '375.15');
await store.deleteInvoice({ invoiceId: deleted.id });
await store.archiveProduct(products[10].id);
const draft = await store.startDraft(); await store.addDraftProduct(draft.id, products[9].id);
await store.updateDraftLine(draft.id, products[9].id, { quantity: '٠٢', unitPrice: '١٢٥٫٠٥' });
const expected = { schemaVersion: db.prepare('PRAGMA user_version').get().user_version, dataset: await store.getDataset(), today: await store.getToday(), invoice: await store.getInvoice(invoice.id) };
db.exec('PRAGMA wal_checkpoint(TRUNCATE)'); db.close();
copyFileSync(path, join(output, 'android-v8.db'));
writeFileSync(join(output, 'expected.json'), JSON.stringify(expected, null, 2));
if (process.argv.includes('--large')) {
  const large = new DatabaseSync(join(output, 'large.db'));
  // Reuse the actual source schema, including indexes and views; fixture rows are synthetic.
  const source = new DatabaseSync(path);
  const schema = source.prepare("SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type='view',rowid").all();
  source.close(); large.exec('PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;');
  for (const row of schema) large.exec(row.sql);
  large.exec('PRAGMA user_version=8; BEGIN');
  const time = '2026-11-18T10:00:00.000Z';
  large.prepare("INSERT INTO dataset VALUES(1,?,'EGP')").run('synthetic-performance-dataset');
  large.prepare('INSERT INTO customers(reference_number,id,name,name_key,reference,created_at,phone,phone_key) VALUES(1,?,?,?,?,?,?,?)').run('c1','عميل أداء','عميل أداء','C-0001',time,null,'');
  const product = large.prepare('INSERT INTO products(id,name,name_key,price,threshold,created_at,updated_at) VALUES(?,?,?,?,?,?,?)');
  const movement = large.prepare('INSERT INTO stock_movements(id,product_id,kind,quantity,occurred_at,source_id) VALUES(?,?,?,?,?,?)');
  for (let i = 0; i < 10000; i++) {
    const name = `منتج ${i.toString().padStart(5, '0')} فرامل R3D`;
    product.run(`p${i}`, name, name.toLowerCase(), 12505, 10, time, time);
    movement.run(`opening-${i}`, `p${i}`, 'opening', 1000, time, null);
  }
  const invoiceRow = large.prepare('INSERT INTO invoices(id,reference,issue_key,issued_at,customer_id,customer_name,customer_name_key,total) VALUES(?,?,?,?,?,?,?,?)');
  const item = large.prepare('INSERT INTO invoice_items VALUES(?,?,?,?,?,?)');
  const payment = large.prepare('INSERT INTO payments(id,invoice_id,amount,received_at,created_at) VALUES(?,?,?,?,?)');
  for (let i = 0; i < 100000; i++) {
    const id = `i${i}`; invoiceRow.run(id, `INV-20261118-${i.toString(16).toUpperCase().padStart(20,'0')}`, `issue-${i}`, time, 'c1', 'عميل أداء', 'عميل أداء', 125050);
    for (let j = 0; j < 10; j++) { const p = (i * 10 + j) % 10000; item.run(id, `p${p}`, `منتج ${p.toString().padStart(5,'0')} فرامل R3D`, 1, 12505, j); movement.run(`sale-${i}-${j}`, `p${p}`, 'sale', -1, time, id); }
    payment.run(`payment-${i}`, id, 50000, time, time);
  }
  large.exec('COMMIT; ANALYZE; PRAGMA wal_checkpoint(TRUNCATE)'); large.close();
}
console.log(JSON.stringify({ fixture: join(output, 'android-v8.db'), expected: join(output, 'expected.json'), large: process.argv.includes('--large') ? join(output, 'large.db') : null }));
