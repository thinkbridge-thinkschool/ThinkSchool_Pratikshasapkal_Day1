using OrderApi.Dtos;

namespace OrderApi.Strategies;

// Applies tiered discounts for high-value orders.
// Orders >= $200 get 15%, orders >= $100 get 10%, anything below gets no discount.
public class BulkOrderDiscountStrategy : IDiscountStrategy
{
    public decimal Calculate(decimal subtotal, OrderRequest request)
    {
        return subtotal switch
        {
            >= 200m => subtotal * 0.15m,
            >= 100m => subtotal * 0.10m,
            _       => 0m
        };
    }
}
