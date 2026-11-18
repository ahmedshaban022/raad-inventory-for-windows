CREATE TABLE dataset (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    id TEXT NOT NULL UNIQUE,
    currency TEXT NOT NULL
  );

CREATE TABLE products (
    id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    name_key TEXT NOT NULL UNIQUE,
    price INTEGER NOT NULL CHECK (price BETWEEN 0 AND 999999999999),
    threshold INTEGER NOT NULL CHECK (threshold BETWEEN 0 AND 2147483647),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
  , archived_at TEXT);

CREATE TABLE stock_movements (
    id TEXT PRIMARY KEY NOT NULL,
    product_id TEXT NOT NULL REFERENCES products(id),
    kind TEXT NOT NULL CHECK (kind IN ('opening', 'addition', 'correction', 'sale', 'cancellation')),
    quantity INTEGER NOT NULL CHECK (quantity != 0 AND quantity BETWEEN -2147483647 AND 2147483647),
    occurred_at TEXT NOT NULL,
    source_id TEXT,
    reason TEXT
  );

CREATE INDEX stock_movements_product_date ON stock_movements(product_id, occurred_at DESC, id DESC);

CREATE TABLE operations (
    key TEXT PRIMARY KEY NOT NULL,
    scope TEXT NOT NULL,
    result_json TEXT NOT NULL
  );

CREATE TABLE customers (
    reference_number INTEGER PRIMARY KEY,
    id TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    name_key TEXT NOT NULL,
    reference TEXT NOT NULL UNIQUE,
    created_at TEXT NOT NULL
  , phone TEXT, phone_key TEXT NOT NULL DEFAULT '', archived_at TEXT);

CREATE INDEX customers_name ON customers(name_key, id);

CREATE TABLE invoice_draft (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    id TEXT UNIQUE NOT NULL,
    initial_payment TEXT NOT NULL DEFAULT '',
    customer_id TEXT REFERENCES customers(id)
  );

CREATE TABLE draft_lines (
    draft_id TEXT NOT NULL REFERENCES invoice_draft(id) ON DELETE CASCADE,
    product_id TEXT NOT NULL REFERENCES products(id),
    quantity TEXT NOT NULL,
    unit_price TEXT NOT NULL,
    position INTEGER NOT NULL,
    PRIMARY KEY (draft_id, product_id),
    UNIQUE (draft_id, position)
  );

CREATE TABLE invoices (
    id TEXT PRIMARY KEY NOT NULL,
    reference TEXT UNIQUE NOT NULL,
    issue_key TEXT UNIQUE NOT NULL,
    issued_at TEXT NOT NULL,
    customer_id TEXT REFERENCES customers(id),
    customer_name TEXT,
    customer_name_key TEXT,
    total INTEGER NOT NULL CHECK (total BETWEEN 0 AND 999999999999),
    cancelled_at TEXT,
    CHECK ((customer_id IS NULL AND customer_name IS NULL AND customer_name_key IS NULL)
      OR (customer_id IS NOT NULL AND customer_name IS NOT NULL AND customer_name_key IS NOT NULL))
  );

CREATE INDEX invoices_issue_date ON invoices(issued_at, id);

CREATE TABLE invoice_items (
    invoice_id TEXT NOT NULL REFERENCES invoices(id),
    product_id TEXT NOT NULL REFERENCES products(id),
    name TEXT NOT NULL,
    quantity INTEGER NOT NULL CHECK (quantity > 0),
    unit_price INTEGER NOT NULL CHECK (unit_price BETWEEN 0 AND 999999999999),
    position INTEGER NOT NULL,
    PRIMARY KEY (invoice_id, product_id),
    UNIQUE (invoice_id, position)
  );

CREATE TABLE payments (
    id TEXT PRIMARY KEY NOT NULL,
    invoice_id TEXT NOT NULL REFERENCES invoices(id),
    amount INTEGER NOT NULL CHECK (amount BETWEEN 1 AND 999999999999),
    received_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    replaces_payment_id TEXT REFERENCES payments(id)
  );

CREATE TABLE payment_reversals (
    id TEXT PRIMARY KEY NOT NULL,
    payment_id TEXT NOT NULL UNIQUE REFERENCES payments(id),
    reason TEXT NOT NULL,
    created_at TEXT NOT NULL
  );

CREATE INDEX payments_receipt_date ON payments(received_at);

CREATE TABLE customer_links (
    invoice_id TEXT PRIMARY KEY NOT NULL REFERENCES invoices(id),
    customer_id TEXT NOT NULL REFERENCES customers(id),
    created_at TEXT NOT NULL
  );

CREATE INDEX customer_links_customer ON customer_links(customer_id, invoice_id);

CREATE INDEX invoices_customer_date ON invoices(customer_id, issued_at, id);

CREATE VIEW effective_payments AS
    SELECT p.* FROM payments p
    WHERE NOT EXISTS (SELECT 1 FROM payment_reversals r WHERE r.payment_id = p.id);

CREATE VIEW invoice_paid AS
    SELECT i.*, COALESCE((SELECT SUM(p.amount) FROM effective_payments p WHERE p.invoice_id = i.id), 0) AS paid
    FROM invoices i;

CREATE VIEW invoice_balances AS
    SELECT i.*, l.customer_id AS linked_customer_id, c.name AS linked_customer_name,
      c.name_key AS linked_customer_name_key,
      CASE WHEN i.cancelled_at IS NOT NULL THEN 0 ELSE i.total - i.paid END AS balance,
      CASE
        WHEN i.cancelled_at IS NOT NULL THEN 'Cancelled'
        WHEN i.paid = i.total THEN 'Paid'
        WHEN i.paid = 0 THEN 'Unpaid'
        ELSE 'Partially paid'
      END AS status
    FROM invoice_paid i
    LEFT JOIN customer_links l ON l.invoice_id = i.id
    LEFT JOIN customers c ON c.id = l.customer_id;

CREATE TABLE pending_issue (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    invoice_id TEXT NOT NULL REFERENCES invoices(id)
  );

CREATE INDEX payments_invoice_order ON payments(invoice_id, created_at);

CREATE VIEW product_stock AS
  SELECT p.id, p.name, p.name_key, p.price, p.threshold, p.archived_at,
    COALESCE((SELECT SUM(m.quantity) FROM stock_movements m WHERE m.product_id = p.id), 0) AS quantity
  FROM products p;

PRAGMA user_version = 8;
