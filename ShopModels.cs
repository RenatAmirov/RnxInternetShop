using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace RnxInternetShop;

public sealed record Product(int Id, string Name, string Description, string Category, decimal Price, string Image);

public sealed record CartItem(int ProductId, string Name, string Image, decimal UnitPrice, int Quantity)
{
    public decimal Total => UnitPrice * Quantity;
}

public sealed record Cart(Guid Revision, IReadOnlyList<CartItem> Items)
{
    public decimal Subtotal => Items.Sum(item => item.Total);
    public int Count => Items.Sum(item => item.Quantity);
}

public sealed class CheckoutForm : IValidatableObject
{
    [Required(ErrorMessage = "Укажите имя.")]
    [StringLength(100, ErrorMessage = "Имя может содержать до 100 символов.")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Укажите электронную почту.")]
    [EmailAddress(ErrorMessage = "Проверьте адрес электронной почты.")]
    [StringLength(254, ErrorMessage = "Адрес может содержать до 254 символов.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Укажите город, улицу и номер дома.")]
    [StringLength(500, ErrorMessage = "Адрес может содержать до 500 символов.")]
    public string Address { get; set; } = "";

    [RegularExpression(@"^\+[1-9][0-9]{7,14}$", ErrorMessage = "Введите номер в формате +79991234567.")]
    public string? Phone { get; set; }
    public bool SmsRequested { get; set; }
    public Guid Revision { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SmsRequested && string.IsNullOrWhiteSpace(Phone))
            yield return new ValidationResult("Укажите номер для SMS.", [nameof(Phone)]);
    }
}

public sealed record OrderSummary(Guid Id, long Number, decimal Subtotal, decimal Shipping,
    DateTime CreatedAt, int Quantity)
{
    public decimal Total => Subtotal + Shipping;
}

public sealed record OrderDetails(long Number, string Name, string Email, string Address, string? Phone,
    decimal Subtotal, decimal Shipping, DateTime CreatedAt, string? Notification, IReadOnlyList<CartItem> Items)
{
    public decimal Total => Subtotal + Shipping;
}

public sealed record OrderPlaced(Guid EventId, Guid OrderId, bool SmsRequested);

public enum CartChange { Add, Decrease, Remove }

public static class ShopDisplay
{
    public const decimal FreeShippingFrom = 5000m;
    public const decimal StandardShipping = 390m;
    public const int MaxQuantity = 20;

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly TimeZoneInfo StoreZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Samara");

    public static decimal ShippingFor(decimal subtotal) => subtotal == 0 || subtotal >= FreeShippingFrom
        ? 0 : StandardShipping;

    public static string Money(decimal amount) => amount.ToString("#,0.##", Russian) + " ₽";
    public static string Image(string slug) => $"/images/{slug}.webp";
    public static string Date(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, StoreZone)
        .ToString("d MMMM yyyy", Russian);

    public static string ItemCount(int count)
    {
        var ending = count % 100 is >= 11 and <= 14 ? "товаров" : (count % 10) switch
        {
            1 => "товар",
            2 or 3 or 4 => "товара",
            _ => "товаров"
        };
        return $"{count} {ending}";
    }
}
