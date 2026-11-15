select pg_advisory_xact_lock(793018104);

create table if not exists products (
    id integer primary key,
    name text not null,
    description text not null,
    category text not null,
    price numeric(10,2) not null check (price > 0),
    image text not null
);

insert into products (id, name, description, category, price, image) values
    (1, 'Кружка «Утро»', 'Керамогранит · 350 мл', 'table', 1290, 'mug'),
    (2, 'Салфетки «Олива»', 'Лён · набор из 2 штук', 'textile', 1690, 'napkins'),
    (3, 'Ваза «Терра»', 'Матовая керамика · 18 см', 'decor', 2490, 'vase'),
    (4, 'Свеча «Тёплый свет»', 'Соевый воск · 180 г', 'decor', 1490, 'candle'),
    (5, 'Тарелки «Песок»', 'Керамогранит · набор из 2 штук', 'table', 2790, 'plates'),
    (6, 'Плед «Дюна»', 'Хлопок · 130 × 170 см', 'textile', 3890, 'throw')
on conflict (id) do nothing;

create table if not exists carts (
    id uuid primary key,
    revision uuid not null
);

create table if not exists cart_items (
    cart_id uuid not null references carts(id) on delete cascade,
    product_id integer not null references products(id),
    quantity integer not null check (quantity between 1 and 20),
    primary key (cart_id, product_id)
);

create table if not exists orders (
    id uuid primary key,
    number bigint generated always as identity (start with 1001) unique,
    cart_id uuid not null references carts(id),
    checkout_token uuid not null unique,
    customer_name text not null,
    email text not null,
    address text not null,
    phone text,
    subtotal numeric(12,2) not null,
    shipping numeric(10,2) not null,
    created_at timestamptz not null default now()
);
create index if not exists orders_cart_created on orders(cart_id, created_at desc);

create table if not exists order_items (
    order_id uuid not null references orders(id),
    product_id integer not null,
    name text not null,
    image text not null,
    unit_price numeric(10,2) not null,
    quantity integer not null,
    primary key (order_id, product_id)
);

create table if not exists outbox (
    id uuid primary key,
    order_id uuid not null unique references orders(id),
    sms_requested boolean not null,
    created_at timestamptz not null default now(),
    published_at timestamptz
);
create index if not exists outbox_unpublished on outbox(created_at) where published_at is null;

create table if not exists processed_effects (
    event_id uuid not null references outbox(id),
    effect text not null,
    processed_at timestamptz not null default now(),
    primary key (event_id, effect)
);

create table if not exists notifications (
    order_id uuid primary key references orders(id),
    message text not null,
    created_at timestamptz not null default now()
);

create table if not exists order_journal (
    event_id uuid primary key references outbox(id),
    order_id uuid not null references orders(id),
    event_type text not null,
    created_at timestamptz not null default now()
);

create table if not exists demo_sms_inbox (
    event_id uuid primary key references outbox(id),
    recipient text not null,
    message text not null,
    created_at timestamptz not null default now()
);
