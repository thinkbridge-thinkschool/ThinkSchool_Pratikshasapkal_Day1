using Microsoft.Extensions.Logging;
using Moq;
using OrderApi.Dtos;
using OrderApi.Services;

namespace OrderApi.Tests;

public class OrderServiceTests
{
    [Fact]
    public async Task CreateOrderAsync_Should_Return_Response()
    {
        var logger = new Mock<ILogger<OrderService>>();

        var service = new OrderService(
            logger.Object);

        var request = new OrderRequest
        {
            Customer = new CustomerDto
            {
                Name = "Pratiksha",
                Email = "test@test.com"
            },

            Items = new List<OrderItemDto>
            {
                new OrderItemDto
                {
                    ProductId = 1,
                    Quantity = 2,
                    UnitPrice = 100
                }
            }
        };

        var result = await service.CreateOrderAsync(
            request,
            CancellationToken.None);

        Assert.NotNull(result);

        Assert.True(result.Ok);

        Assert.Equal("Pratiksha", result.CustomerName);
    }

    [Fact]
public async Task CreateOrderAsync_Should_Throw_Exception_When_Request_Is_Null()
{
    var logger = new Mock<ILogger<OrderService>>();

    var service = new OrderService(
        logger.Object);

    await Assert.ThrowsAsync<ArgumentNullException>(
        async () =>
        {
            await service.CreateOrderAsync(
                null!,
                CancellationToken.None);
        });
}


[Fact]
public async Task CreateOrderAsync_Should_Return_Correct_Email()
{
    var logger = new Mock<ILogger<OrderService>>();

    var service = new OrderService(
        logger.Object);

    var request = new OrderRequest
    {
        Customer = new CustomerDto
        {
            Name = "Pratiksha",
            Email = "pratiksha@test.com"
        },

        Items = new List<OrderItemDto>
        {
            new OrderItemDto
            {
                ProductId = 1,
                Quantity = 1,
                UnitPrice = 50
            }
        }
    };

    var result = await service.CreateOrderAsync(
        request,
        CancellationToken.None);

    Assert.Equal(
        "pratiksha@test.com",
        result.CustomerEmail);
}


}

