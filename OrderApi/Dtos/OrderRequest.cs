namespace OrderApi.Dtos;

public class OrderRequest
{
    public CustomerDto? Customer { get; set; }

    public List<OrderItemDto>? Items { get; set; }
}

public class CustomerDto
{
    public string? Name { get; set; }

    public string? Email { get; set; }

    public Address? Address { get; set; }
}

public class OrderItemDto
{
    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }
}

public class Address
{
    public string? Line1 { get; set; }

    public string? City { get; set; }
}