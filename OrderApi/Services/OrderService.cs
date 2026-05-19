using System.Linq;
using Microsoft.Extensions.Logging;
using OrderApi.Dtos;
using OrderApi.Interfaces;

namespace OrderApi.Services;

public class OrderService : IOrderService
{
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        ILogger<OrderService> logger)
    {
        _logger = logger;
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

            await Task.Delay(1, cancellationToken);

            decimal total = request.Items.Sum(x =>
                x.Quantity * x.UnitPrice);

            return new CreateOrderResponse
            {
                Ok = true,
                OrderId = 1,
                Total = total,
                Discount = 0,
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