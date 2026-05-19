using System.Threading;
using System.Threading.Tasks;
using OrderApi.Dtos;

namespace OrderApi.Interfaces;

public interface IOrderService
{
    Task<CreateOrderResponse> CreateOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken);
}