using System.Threading;
using System.Threading.Tasks;

namespace OrderApi.Interfaces;

public interface IOrderRepository
{
    Task<int> SaveOrderAsync(
        CancellationToken cancellationToken);
}