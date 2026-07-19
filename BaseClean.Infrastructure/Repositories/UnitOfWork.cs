using BaseClean.Domain.Interfaces;
using BaseClean.Infrastructure.Data;

namespace BaseClean.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        await _context.SaveChangesAsync(cancellationToken);
}