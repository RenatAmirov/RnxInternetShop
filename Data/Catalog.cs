using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

namespace RnxInternetShop.Data;

public sealed class Catalog(NpgsqlDataSource dataSource, IConfiguration configuration, ILogger<Catalog> logger)
    : IDisposable
{
    private const string CacheKey = "rnx-shop:catalog:v1";
    private readonly Lazy<Task<ConnectionMultiplexer>> redis = new(() =>
    {
        var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis")!);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 0;
        options.ConnectTimeout = 1000;
        options.AsyncTimeout = 1000;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        return ConnectionMultiplexer.ConnectAsync(options);
    });

    public async Task<IReadOnlyList<Product>> GetAsync(CancellationToken cancellationToken)
    {
        IDatabase? cache = null;
        try
        {
            cache = (await redis.Value.WaitAsync(cancellationToken)).GetDatabase();
            var cached = await cache.StringGetAsync(CacheKey).WaitAsync(cancellationToken);
            if (cached.HasValue && JsonSerializer.Deserialize<Product[]>(cached.ToString()) is { } products)
                return products;
        }
        catch (Exception exception) when (exception is RedisException or JsonException)
        {
            logger.LogDebug(exception, "Catalog cache miss due to a Redis or data error.");
        }

        await using var command = dataSource.CreateCommand("""
            select id, name, description, category, price, image
            from products order by id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var catalog = new List<Product>();
        while (await reader.ReadAsync(cancellationToken))
        {
            catalog.Add(new Product(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4), reader.GetString(5)));
        }

        if (cache is not null)
        {
            try
            {
                await cache.StringSetAsync(CacheKey, JsonSerializer.Serialize(catalog), TimeSpan.FromMinutes(5))
                    .WaitAsync(cancellationToken);
            }
            catch (RedisException exception)
            {
                logger.LogDebug(exception, "Catalog cache write failed.");
            }
        }

        return catalog;
    }

    public void Dispose()
    {
        if (redis.IsValueCreated && redis.Value.IsCompletedSuccessfully)
            redis.Value.Result.Dispose();
    }
}
