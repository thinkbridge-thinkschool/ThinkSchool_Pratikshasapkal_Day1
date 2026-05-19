namespace OrderApi.Dtos;

public class CreateOrderResponse
{
    public bool Ok { get; set; }

    public int OrderId { get; set; }

    public decimal Total { get; set; }

    public decimal Discount { get; set; }

    public string? CustomerName { get; set; }

    public string? CustomerEmail { get; set; }
}