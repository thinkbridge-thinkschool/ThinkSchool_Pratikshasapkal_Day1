using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class CollectionRepository : ICollectionRepository
{
    private readonly AppDbContext _db;

    public CollectionRepository(
        AppDbContext db)
    {
        _db = db;
    }

    public async Task<Collection?> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        return await _db.Collections
            .Include(x => x.Items)
            .FirstOrDefaultAsync(
                x => x.Id == id,
                cancellationToken);
    }

    public async Task Add(
        Collection collection,
        CancellationToken cancellationToken)
    {
        _db.Collections.Add(collection);

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    public async Task Update(
        Collection collection,
        CancellationToken cancellationToken)
    {
        _db.Collections.Update(collection);

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    public async Task Delete(
        Collection collection,
        CancellationToken cancellationToken)
    {
        _db.Collections.Remove(collection);

        await _db.SaveChangesAsync(
            cancellationToken);
    }
}