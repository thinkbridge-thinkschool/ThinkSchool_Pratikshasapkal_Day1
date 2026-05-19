using OrderApi.Dtos;

namespace OrderApi.Strategies;

public interface IDiscountStrategy
{
    decimal Calculate(decimal subtotal, OrderRequest request);
}
