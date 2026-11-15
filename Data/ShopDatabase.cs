using Npgsql;

namespace RnxInternetShop.Data;

public static class ShopDatabase
{
    public static async Task InitializeAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        using var resource = typeof(ShopDatabase).Assembly.GetManifestResourceStream("RnxInternetShop.Data.Schema.sql")
            ?? throw new InvalidOperationException("Embedded database schema is missing.");
        using var reader = new StreamReader(resource);
        var sql = await reader.ReadToEndAsync(cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static NpgsqlCommand Command(this NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        return command;
    }
}
