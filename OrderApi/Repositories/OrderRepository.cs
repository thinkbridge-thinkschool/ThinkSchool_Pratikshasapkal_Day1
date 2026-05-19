using System.Threading;
using System.Threading.Tasks;
using OrderApi.Interfaces;

namespace OrderApi.Repositories;

public class OrderRepository : IOrderRepository
{
    public async Task<int> SaveOrderAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);

        return 1;
    }
}