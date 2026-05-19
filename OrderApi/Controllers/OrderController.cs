using Microsoft.AspNetCore.Mvc;
using OrderApi.Dtos;
using OrderApi.Interfaces;

namespace OrderApi.Controllers;

[ApiController]
[Route("api/orders")]
public class OrderController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrderController(
        IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<ActionResult<CreateOrderResponse>> Post(
        [FromBody] OrderRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return BadRequest("Request cannot be null");
        }

        var response = await _orderService.CreateOrderAsync(
            request,
            cancellationToken);

        return Created(
            $"/api/orders/{response.OrderId}",
            response);
    }

}