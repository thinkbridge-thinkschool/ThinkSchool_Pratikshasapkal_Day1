using System.Linq;
using Microsoft.Extensions.Logging;
using OrderApi.Dtos;
using OrderApi.Interfaces;
using OrderApi.Strategies;

namespace OrderApi.Services;

public class OrderService : IOrderService
{
    private readonly ILogger<OrderService> _logger;
    private readonly IDiscountStrategy _discountStrategy;

    public OrderService(ILogger<OrderService> @object)
    {
    }


    public OrderService(
        ILogger<OrderService> logger,
        IDiscountStrategy discountStrategy)
    {
        _logger = logger;
        _discountStrategy = discountStrategy;
    }

    public async Task<CreateOrderResponse> CreateOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Customer == null)
            {
                throw new ArgumentNullException(nameof(request.Customer));
            }

            if (request.Items == null || request.Items.Count == 0)
            {
                throw new ArgumentException("Items are required");
            }

            if (request.Items.Any(x => x.Quantity < 0))
{
    throw new ArgumentException(
        "Negative quantity is not allowed");
}

            await Task.Delay(1, cancellationToken);

            decimal subtotal = request.Items.Sum(x => x.Quantity * x.UnitPrice);
            decimal discount = _discountStrategy.Calculate(subtotal, request);

            return new CreateOrderResponse
            {
                Ok = true,
                OrderId = 1,
                Total = subtotal - discount,
                Discount = discount,
                CustomerName = request.Customer.Name,
                CustomerEmail = request.Customer.Email
            };
        }
        catch (ArgumentNullException ex)
        {
            _logger.LogError(ex, "Validation failed");

            throw;
        }
    }
}
