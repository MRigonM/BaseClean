namespace BaseClean.Domain.Interfaces;

public interface IUnitOfWork : IReadOnlyUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
} 