using OrderApi.Dtos;

namespace OrderApi.Strategies;

// Default strategy — preserves the original behaviour (no discount applied).
public class NoDiscountStrategy : IDiscountStrategy
{
    public decimal Calculate(decimal subtotal, OrderRequest request) => 0m;
}
