using BaseClean.Domain;
using BaseClean.Domain.Entities;
using BaseClean.Domain.Interfaces;
using BaseClean.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BaseClean.Infrastructure.Repositories;

public class GenericRepository<TEntity, TKey> : IGenericRepository<TEntity, TKey> where TEntity : BaseEntity<TKey>
{
    private readonly DbSet<TEntity> _dbSet;

    public GenericRepository(ApplicationDbContext context)
    {
        _dbSet = context.Set<TEntity>();
    }

    public async Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync(id, cancellationToken);
    }

    public async Task<List<TEntity>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.ToListAsync(cancellationToken);
    }
    
    public async Task<TKey> AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        return entity.Id;
    }
    
    public Task Update(TEntity entity, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_dbSet.Update(entity));
            
    }
    
    public Task Delete(TEntity entity, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_dbSet.Remove(entity));
    }

    public IQueryable<TEntity> GetAll()
    {
        return _dbSet.AsQueryable();
    }
}