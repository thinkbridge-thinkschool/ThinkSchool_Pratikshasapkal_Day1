using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OrderApi.Tests;

public class OrderApiIntegrationTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OrderApiIntegrationTests(
        WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_Order_Should_Return_Created()
    {
        var client = _factory.CreateClient();

        var json = """
        {
            "customer": {
                "name": "Pratiksha",
                "email": "test@test.com"
            },
            "items": [
                {
                    "productId": 1,
                    "quantity": 1,
                    "unitPrice": 100
                }
            ]
        }
        """;

        var content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(
            "/api/orders",
            content);

        Assert.True(response.IsSuccessStatusCode);
    }
}