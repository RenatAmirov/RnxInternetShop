using Npgsql;

namespace RnxInternetShop.Data;

public sealed class Orders(NpgsqlDataSource dataSource)
{
    public async Task<Cart> GetCartAsync(Guid owner, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadCartAsync(connection, null, owner, ct);
    }

    public async Task<bool> ChangeCartAsync(Guid owner, int productId, CartChange change, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var ensure = connection.Command(transaction, """
            insert into carts (id, revision) values (@owner, @revision) on conflict do nothing
            """, ("owner", owner), ("revision", Guid.NewGuid()));
        await ensure.ExecuteNonQueryAsync(ct);
        await LockCartAsync(connection, transaction, owner, ct);

        var sql = change switch
        {
            CartChange.Add => """
                with added as (
                    insert into cart_items (cart_id, product_id, quantity)
                    select @owner, id, 1 from products where id = @product
                    on conflict (cart_id, product_id) do update set quantity = cart_items.quantity + 1
                    where cart_items.quantity < @limit
                    returning 1
                )
                select exists (select 1 from added)
                """,
            CartChange.Decrease => """
                with removed as (
                    delete from cart_items where cart_id = @owner and product_id = @product and quantity = 1
                    returning 1
                ), decreased as (
                    update cart_items set quantity = quantity - 1
                    where cart_id = @owner and product_id = @product and quantity > 1
                    returning 1
                )
                select exists (select 1 from removed union all select 1 from decreased)
                """,
            CartChange.Remove => """
                with removed as (
                    delete from cart_items where cart_id = @owner and product_id = @product returning 1
                )
                select exists (select 1 from removed)
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        await using var command = connection.Command(transaction, sql,
            ("owner", owner), ("product", productId), ("limit", ShopDisplay.MaxQuantity));
        var changed = await command.ExecuteScalarAsync(ct) is true;
        if (changed)
            await NewRevisionAsync(connection, transaction, owner, ct);

        await transaction.CommitAsync(ct);
        return changed;
    }

    public async Task<Guid?> PlaceAsync(Guid owner, CheckoutForm form, CancellationToken ct)
    {
        if (form.Revision == Guid.Empty)
            return null;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var revision = await LockCartAsync(connection, transaction, owner, ct);
        if (revision is null)
            return null;

        await using var existing = connection.Command(transaction, """
            select id from orders where cart_id = @owner and checkout_token = @token
            """, ("owner", owner), ("token", form.Revision));
        if (await existing.ExecuteScalarAsync(ct) is Guid existingOrder)
            return existingOrder;

        if (revision != form.Revision)
            return null;

        var cart = await ReadCartAsync(connection, transaction, owner, ct);
        if (cart.Items.Count == 0)
            return null;

        var orderId = Guid.NewGuid();
        await using var create = connection.Command(transaction, """
            insert into orders
                (id, cart_id, checkout_token, customer_name, email, address, phone, subtotal, shipping)
            values (@order, @owner, @token, @name, @email, @address, nullif(@phone, ''), @subtotal, @shipping)
            """, ("order", orderId), ("owner", owner), ("token", form.Revision),
            ("name", form.Name.Trim()), ("email", form.Email.Trim()), ("address", form.Address.Trim()),
            ("phone", form.SmsRequested ? form.Phone! : ""), ("subtotal", cart.Subtotal),
            ("shipping", ShopDisplay.ShippingFor(cart.Subtotal)));
        await create.ExecuteNonQueryAsync(ct);

        // Persist the same price snapshot used for the total, even if the catalog changes concurrently.
        await using var items = connection.Command(transaction, """
            insert into order_items (order_id, product_id, name, image, unit_price, quantity)
            select @order, * from unnest(@products, @names, @images, @prices, @quantities)
            """, ("order", orderId), ("products", cart.Items.Select(item => item.ProductId).ToArray()),
            ("names", cart.Items.Select(item => item.Name).ToArray()),
            ("images", cart.Items.Select(item => item.Image).ToArray()),
            ("prices", cart.Items.Select(item => item.UnitPrice).ToArray()),
            ("quantities", cart.Items.Select(item => item.Quantity).ToArray()));
        await items.ExecuteNonQueryAsync(ct);

        await using var complete = connection.Command(transaction, """
            insert into outbox (id, order_id, sms_requested) values (@event, @order, @sms);
            delete from cart_items where cart_id = @owner;
            """, ("event", Guid.NewGuid()), ("order", orderId), ("sms", form.SmsRequested), ("owner", owner));
        await complete.ExecuteNonQueryAsync(ct);
        await NewRevisionAsync(connection, transaction, owner, ct);
        await transaction.CommitAsync(ct);
        return orderId;
    }

    public async Task<IReadOnlyList<OrderSummary>> ListAsync(Guid owner, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.Command(null, """
            select o.id, o.number, o.subtotal, o.shipping, o.created_at, sum(i.quantity)::int
            from orders o join order_items i on i.order_id = o.id
            where o.cart_id = @owner group by o.id order by o.created_at desc
            """, ("owner", owner));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var orders = new List<OrderSummary>();
        while (await reader.ReadAsync(ct))
        {
            orders.Add(new OrderSummary(reader.GetGuid(0), reader.GetInt64(1), reader.GetDecimal(2),
                reader.GetDecimal(3), reader.GetDateTime(4), reader.GetInt32(5)));
        }
        return orders;
    }

    public async Task<OrderDetails?> FindAsync(Guid owner, Guid orderId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.Command(null, """
            select o.number, o.customer_name, o.email, o.address, o.phone, o.subtotal, o.shipping,
                   o.created_at, n.message, i.product_id, i.name, i.image, i.unit_price, i.quantity
            from orders o join order_items i on i.order_id = o.id
            left join notifications n on n.order_id = o.id
            where o.cart_id = @owner and o.id = @order
            order by i.product_id
            """, ("owner", owner), ("order", orderId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var items = new List<CartItem>();
        var details = new OrderDetails(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDecimal(5),
            reader.GetDecimal(6), reader.GetDateTime(7), reader.IsDBNull(8) ? null : reader.GetString(8), items);
        do
        {
            items.Add(ReadItem(reader, 9));
        } while (await reader.ReadAsync(ct));
        return details;
    }

    private static async Task<Cart> ReadCartAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid owner, CancellationToken ct)
    {
        await using var command = connection.Command(transaction, """
            select c.revision, p.id, p.name, p.image, p.price, i.quantity
            from carts c left join cart_items i on i.cart_id = c.id
            left join products p on p.id = i.product_id
            where c.id = @owner order by p.id
            """, ("owner", owner));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new Cart(Guid.Empty, []);

        var revision = reader.GetGuid(0);
        var items = new List<CartItem>();
        do
        {
            if (!reader.IsDBNull(1))
                items.Add(ReadItem(reader, 1));
        } while (await reader.ReadAsync(ct));
        return new Cart(revision, items);
    }

    private static CartItem ReadItem(NpgsqlDataReader reader, int index) => new(
        reader.GetInt32(index), reader.GetString(index + 1), reader.GetString(index + 2),
        reader.GetDecimal(index + 3), reader.GetInt32(index + 4));

    private static async Task<Guid?> LockCartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid owner, CancellationToken ct)
    {
        await using var command = connection.Command(transaction,
            "select revision from carts where id = @owner for update", ("owner", owner));
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task NewRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid owner, CancellationToken ct)
    {
        await using var command = connection.Command(transaction, "update carts set revision = @revision where id = @owner",
            ("revision", Guid.NewGuid()), ("owner", owner));
        await command.ExecuteNonQueryAsync(ct);
    }
}
